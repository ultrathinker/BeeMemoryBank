using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Exercises McpSessionGuardMiddleware through a real ASP.NET Core pipeline (unlike
/// McpToolsTests/McpAclTests, which construct MCP tool classes directly and therefore bypass all
/// middleware). Uses BmbWebApplicationFactory — the same WebApplicationFactory-style test host
/// ApiIntegrationTests uses — driving the actual /mcp endpoint over HTTP with a minimal
/// hand-rolled JSON-RPC client that mirrors the handshake in BeeUploadTools' embedded
/// bmb-upload.py script (initialize → notifications/initialized → tools/call).
/// </summary>
public class McpSessionGuardMiddlewareTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string Password = "mcpGuardTestPassword";

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ───── MCP JSON-RPC test helpers ───────────────────────────────────────────

    private static async Task<HttpResponseMessage> McpPostAsync(
        HttpClient client, object payload, string? sessionId = null, string? bearer = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (sessionId != null)
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        if (bearer != null)
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
        return await client.SendAsync(req);
    }

    private static async Task<JsonElement> ParseMcpBodyAsync(HttpResponseMessage resp)
    {
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
        var body = await resp.Content.ReadAsStringAsync();
        if (contentType.Contains("event-stream"))
        {
            foreach (var rawLine in body.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (!line.StartsWith("data:")) continue;
                var chunk = line[5..].Trim();
                if (chunk.Length > 0)
                    return JsonSerializer.Deserialize<JsonElement>(chunk);
            }
            throw new InvalidOperationException($"No 'data:' line found in SSE body: {body}");
        }
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    /// <summary>
    /// Performs the initialize → notifications/initialized handshake and returns the resulting
    /// Mcp-Session-Id, ready to use as the sessionId argument for a subsequent tools/call.
    /// </summary>
    private static async Task<string> InitMcpSessionAsync(HttpClient client, string? bearer = null)
    {
        var initResp = await McpPostAsync(client, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "mcp-session-guard-tests", version = "1.0" }
            }
        }, bearer: bearer);
        initResp.EnsureSuccessStatusCode();

        if (!initResp.Headers.TryGetValues("Mcp-Session-Id", out var values))
            throw new InvalidOperationException("MCP initialize response carried no Mcp-Session-Id header.");
        var sessionId = values.First();

        var initializedResp = await McpPostAsync(client, new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        }, sessionId, bearer);
        initializedResp.EnsureSuccessStatusCode();

        return sessionId;
    }

    private static async Task<JsonElement> CallToolAsync(
        HttpClient client, string sessionId, string toolName, object arguments, string? bearer = null)
    {
        var resp = await McpPostAsync(client, new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new { name = toolName, arguments }
        }, sessionId, bearer);
        resp.EnsureSuccessStatusCode();
        return await ParseMcpBodyAsync(resp);
    }

    // ───── Check B: session-locked guard ───────────────────────────────────────

    [Fact]
    public async Task ToolsCall_RequiresUnlockedSessionTool_WhileLocked_IsBlockedWithLockMessage()
    {
        // Fresh node: session starts locked (see ApiIntegrationTests.Session_Status_InitiallyLocked).
        var sessionId = await InitMcpSessionAsync(_client);

        var result = await CallToolAsync(_client, sessionId, "bee_get_article_version",
            new { id = Guid.NewGuid(), versionNumber = 1 });

        result.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        var text = result.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        text.Should().Contain("Bank is locked");
    }

    [Fact]
    public async Task ToolsCall_RequiresUnlockedSessionTool_WhileUnlocked_ReachesRealTool()
    {
        await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password });

        // Create an article and update it once so a version-1 snapshot exists to fetch.
        var create = await _client.PostAsJsonAsync("/api/articles", new
        {
            title = "Guard Test Article",
            treePath = "/GuardTests",
            content = "original content"
        });
        create.EnsureSuccessStatusCode();
        var article = await create.Content.ReadFromJsonAsync<ArticleResponse>();

        var update = await _client.PutAsJsonAsync($"/api/articles/{article!.Id}", new { content = "updated content" });
        update.EnsureSuccessStatusCode();

        var sessionId = await InitMcpSessionAsync(_client);

        var result = await CallToolAsync(_client, sessionId, "bee_get_article_version",
            new { id = article.Id, versionNumber = 1 });

        var text = result.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        text.Should().NotContain("Bank is locked");
        text.Should().Contain("original content");
    }

    [Fact]
    public async Task ToolsCall_ToolNotRequiringUnlockedSession_WhileLocked_IsNotBlocked()
    {
        // bee_list_articles carries no [RequiresUnlockedSession] — the guard must not touch it,
        // regardless of session state.
        var sessionId = await InitMcpSessionAsync(_client);

        var result = await CallToolAsync(_client, sessionId, "bee_list_articles", new { });

        var isError = result.GetProperty("result").TryGetProperty("isError", out var isErrorEl) && isErrorEl.GetBoolean();
        isError.Should().BeFalse();
        var text = result.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        text.Should().NotContain("Bank is locked");
    }

    // ───── Check A: revoked/unresolved agent key ───────────────────────────────

    [Fact]
    public async Task ToolsCall_WithUnrecognizedBeeBearerKey_IsRejectedRegardlessOfTool()
    {
        var sessionId = await InitMcpSessionAsync(_client, bearer: "bee_doesnotexist12345");

        var result = await CallToolAsync(_client, sessionId, "bee_list_articles", new { }, bearer: "bee_doesnotexist12345");

        result.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        var text = result.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        text.Should().Contain("agent key");
    }

    [Fact]
    public async Task ToolsCall_WithNoAuthorizationHeader_DoesNotTriggerAgentKeyCheck()
    {
        // The default test client carries no Authorization header (only X-Internal-Key /
        // X-User-Role, added by BmbWebApplicationFactory.CreateClient()). Confirms Check A is
        // scoped specifically to a presented-but-unresolved "Bearer bee_..." token, and doesn't
        // false-positive on "no auth at all".
        var sessionId = await InitMcpSessionAsync(_client);

        var result = await CallToolAsync(_client, sessionId, "bee_list_articles", new { });

        var text = result.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        text.Should().NotContain("agent key");
    }
}
