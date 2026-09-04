using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// End-to-end proof that a truncated MCP response can only be read back by the agent that
/// produced it (S6). Drives the real /mcp endpoint with real <c>bee_</c> agent keys through the
/// full middleware chain, so what is asserted is production behaviour and not a directly
/// constructed tool class — McpResponseManagerTests covers the same rule at unit level, including
/// the owner-user / superadmin / tampered-envelope cases.
///
/// Setup mirrors the threat: agent A is owned by the superadmin and reads a large article in
/// /Secret; agent B is owned by a user explicitly denied that folder, so the continuation guid is
/// the only path B could ever have to that content.
///
/// The JSON-RPC helpers mirror McpSessionGuardMiddlewareTests (initialize →
/// notifications/initialized → tools/call).
/// </summary>
public class McpContinueOwnershipTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _adminClient = null!;
    private HttpClient _superadminMcpClient = null!;
    private HttpClient _agentAClient = null!;
    private HttpClient _agentBClient = null!;

    private const string Password = "mcpContinueOwnershipPassword";

    // Sits at the very start of the article body, i.e. far past the 500-char preview the
    // truncation envelope carries — so finding it proves the spooled file was actually read.
    private const string Marker = "SECRET-S6-CONTINUATION-MARKER";

    private Guid _articleId;

    public async Task InitializeAsync()
    {
        _adminClient = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);

        (await _adminClient.PostAsJsonAsync("/api/session/unlock", new { password = Password }))
            .EnsureSuccessStatusCode();

        var login = await _adminClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password });
        login.EnsureSuccessStatusCode();
        var adminUserId = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetInt32();

        var folder = await _adminClient.PostAsJsonAsync("/api/folders", new { path = "/Secret" });
        folder.EnsureSuccessStatusCode();
        var folderId = (await folder.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // ~45k chars of body: comfortably over the 10,000-token default limit (~3 bytes/token),
        // so bee_get_article is forced to spool and hand back a continuation guid.
        var create = await _adminClient.PostAsJsonAsync("/api/articles", new
        {
            title = "S6 Continuation Article",
            treePath = "/Secret",
            content = Marker + new string('a', 45_000)
        });
        create.EnsureSuccessStatusCode();
        _articleId = (await create.Content.ReadFromJsonAsync<ArticleResponse>())!.Id;

        var restrictedUser = await _adminClient.PostAsJsonAsync("/api/users", new
        {
            username = "s6_restricted",
            displayName = "S6 Restricted",
            password = "s6RestrictedPassword",
            role = "user"
        });
        restrictedUser.EnsureSuccessStatusCode();
        var restrictedUserId = (await restrictedUser.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        (await _adminClient.PostAsJsonAsync($"/api/restrictions/user/{restrictedUserId}",
            new { folderId, effect = "deny" })).EnsureSuccessStatusCode();

        _agentAClient = CreateAgentClient(await CreateAgentAsync("agent-a", adminUserId));
        _agentBClient = CreateAgentClient(await CreateAgentAsync("agent-b", restrictedUserId));

        // The web UI's shape: internal key + the proxied user headers, no agent bearer.
        _superadminMcpClient = _factory.CreateClient();
        _superadminMcpClient.DefaultRequestHeaders.Add("X-User-Id", adminUserId.ToString());
    }

    public Task DisposeAsync()
    {
        _adminClient.Dispose();
        _superadminMcpClient.Dispose();
        _agentAClient.Dispose();
        _agentBClient.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ───── Setup helpers ───────────────────────────────────────────────────────

    /// <summary>Creates an active agent owned by <paramref name="ownerUserId"/>, returns its key.</summary>
    private async Task<string> CreateAgentAsync(string name, int ownerUserId)
    {
        var session = _factory.Services.GetRequiredService<SessionService>();
        var masterDek = session.GetMasterDek();
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var (ciphertext, iv) = AgentKeyHelper.EncryptDek(apiKey, masterDek);
        Array.Clear(masterDek);

        using var scope = _factory.Services.CreateScope();
        var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        await agentRepo.CreateAsync(new Agent
        {
            Name = name,
            KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey),
            KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
            EncryptedDek = ciphertext,
            DekIV = iv,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            OwnerUserId = ownerUserId
        });
        return apiKey;
    }

    /// <summary>
    /// A client carrying only the agent bearer — deliberately no X-Internal-Key, so the agent
    /// cannot reach the header-trusting branch of CallerIdentity and its identity is the key.
    /// </summary>
    private HttpClient CreateAgentClient(string apiKey)
    {
        var client = _factory.Server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    // ───── MCP JSON-RPC helpers ────────────────────────────────────────────────

    private static async Task<HttpResponseMessage> McpPostAsync(HttpClient client, object payload, string? sessionId = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (sessionId != null)
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
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

    private static async Task<string> InitMcpSessionAsync(HttpClient client)
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
                clientInfo = new { name = "mcp-continue-ownership-tests", version = "1.0" }
            }
        });
        initResp.EnsureSuccessStatusCode();

        if (!initResp.Headers.TryGetValues("Mcp-Session-Id", out var values))
            throw new InvalidOperationException("MCP initialize response carried no Mcp-Session-Id header.");
        var sessionId = values.First();

        (await McpPostAsync(client, new { jsonrpc = "2.0", method = "notifications/initialized" }, sessionId))
            .EnsureSuccessStatusCode();

        return sessionId;
    }

    /// <summary>Calls one tool and returns the tool result's text payload.</summary>
    private static async Task<string> CallToolAsync(HttpClient client, string toolName, object arguments)
    {
        var sessionId = await InitMcpSessionAsync(client);
        var resp = await McpPostAsync(client, new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new { name = toolName, arguments }
        }, sessionId);
        resp.EnsureSuccessStatusCode();

        var body = await ParseMcpBodyAsync(resp);
        return body.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    /// <summary>
    /// Has agent A fetch the oversized article, which overflows its token limit and gets spooled.
    /// Returns the continuation guid handed back in the truncation envelope.
    /// </summary>
    private async Task<string> SpoolAsAgentAAsync()
    {
        var text = await CallToolAsync(_agentAClient, "bee_get_article", new { id = _articleId });

        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("truncated").GetBoolean()
            .Should().BeTrue("the fixture article must be big enough to be spooled to the continuation store");
        return doc.RootElement.GetProperty("guid").GetString()!;
    }

    // ───── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Continue_ByTheAgentThatSpooledIt_ReturnsTheContent()
    {
        var guid = await SpoolAsAgentAAsync();

        var text = await CallToolAsync(_agentAClient, "bee_continue", new { guid, offset = 0, ignoreLimit = true });

        text.Should().Contain(Marker);
    }

    [Fact]
    public async Task Continue_ByADifferentAgent_IsByteIdenticalToAGuidThatNeverExisted()
    {
        var guid = await SpoolAsAgentAAsync();

        // Sanity: agent B's own key genuinely cannot see this article — the continuation guid is
        // the only route to it, which is exactly what the ownership check has to close.
        var direct = await CallToolAsync(_agentBClient, "bee_get_article", new { id = _articleId });
        direct.Should().NotContain(Marker);

        var stolen = await CallToolAsync(_agentBClient, "bee_continue", new { guid, offset = 0, ignoreLimit = true });
        var neverExisted = await CallToolAsync(_agentBClient, "bee_continue",
            new { guid = Guid.NewGuid().ToString("N"), offset = 0, ignoreLimit = true });

        stolen.Should().NotContain(Marker);
        // Byte-identical, not merely "both an error": anything narrower would tell agent B which
        // guids exist, which is half of the attack it just failed.
        stolen.Should().Be(neverExisted);
        stolen.Should().Contain("not found or expired");
    }

    [Fact]
    public async Task Continue_BySuperadminWebCaller_IsRefused_NoAdminBypass()
    {
        var guid = await SpoolAsAgentAAsync();

        var result = await CallToolAsync(_superadminMcpClient, "bee_continue",
            new { guid, offset = 0, ignoreLimit = true });

        result.Should().NotContain(Marker);
        result.Should().Contain("not found or expired");
    }
}
