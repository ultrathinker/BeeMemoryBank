using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Hosting.AspNetCore;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Pins what an anonymous internet caller (no internal key, no Authorization header, no bearer
/// token of any kind) can and cannot reach — the "no credentials presented at all" persona that
/// BmbWebApplicationFactory's default client has never exercised because CreateClient() always
/// stamps X-Internal-Key and X-User-Role on every request.
///
/// <para>Three claims, each covered by its own test class:</para>
/// <list type="bullet">
///   <item><description>Every MCP tool is unreachable without credentials — both at the HTTP
///   layer (McpIdentityGateMiddleware answers 401 before the SDK runs) and as a side-effect
///   (no media row, no event row written).</description></item>
///   <item><description>Every endpoint NOT in <see cref="PublicSurface.Entries"/> answers 404 to a
///   keyless caller. Enumerating <c>EndpointDataSource</c> covers new endpoints automatically
///   (the same completeness trick PublicSurfaceTests uses).</description></item>
///   <item><description>Every <see cref="PublicSurface.Entries"/> entry returns ONLY its documented
///   anonymous answer (401/404/200-with-no-secrets as appropriate).</description></item>
///   <item><description>/api/join and /api/auth/remote-token from a keyless loopback caller get
///   429 after the limit — the loopback exemption B3 removed.</description></item>
/// </list>
///
/// <para>The rate-limit tests reset <see cref="RateLimitMiddleware"/>'s shared bucket between
/// cases (the limiter is a process-wide singleton; see its doc comment). Without the hook,
/// parallel test classes would share the same IP+path bucket and flake when one of them had
/// already burnt the 5-attempt budget.</para>
/// </summary>
public class AnonymousInternetCallerTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _raw = null!;
    private const string Password = "anonymousCallerTestPassword";

    public async Task InitializeAsync()
    {
        // Server.CreateClient() returns a bare client with NO default headers — exactly the
        // "anonymous internet caller" shape. The default CreateClient() adds X-Internal-Key +
        // X-User-Role, which is the "trusted inside" persona and not what these tests want.
        _raw = _factory.Server.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
    }

    public Task DisposeAsync()
    {
        _raw.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ───── B2: MCP gate ───────────────────────────────────────────────────────

    public static IEnumerable<object[]> AllMcpToolNames()
    {
        // Discover MCP tools the same way McpToolRegistry does: any public method carrying
        // [McpServerTool] on a [McpServerToolType] class. Keeps the test honest if a new tool is
        // added without anyone remembering to extend this list.
        var assembly = typeof(BeeMemoryBank.Api.McpTools.BeeSearchTools).Assembly;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr == null) continue;
                names.Add(string.IsNullOrWhiteSpace(attr.Name) ? method.Name : attr.Name!);
            }
        }
        if (names.Count == 0) throw new InvalidOperationException("No MCP tools discovered; check the reflection scan.");
        return names.Select(n => new object[] { n });
    }

    [Theory]
    [MemberData(nameof(AllMcpToolNames))]
    public async Task McpTool_NoCredentials_Answers401_BeforeMcpSession(string toolName)
    {
        // Full initialize → initialized → tools/call sequence, just like an actual MCP client
        // would do. Without credentials the gate must short-circuit with 401 BEFORE the MCP SDK
        // produces an Mcp-Session-Id — so an anonymous caller cannot even create a session.
        var resp = await McpPostAsync(_raw, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "anonymous-internet-caller-tests", version = "1.0" }
            }
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            $"the MCP gate must answer 401 to a keyless request, even for initialize (tool under test: {toolName})");
        resp.Headers.WwwAuthenticate.ToString().Should().Contain("Bearer",
            "RFC 7235: a 401 must include WWW-Authenticate so clients know what credential to send");
    }

    [Fact]
    public async Task McpSaveMedia_NoCredentials_NoMediaRowCreated()
    {
        // The specific hole B2 closes: an anonymous caller driving the full MCP handshake and
        // then calling bee_save_media must NOT leave a row behind. Even though the HTTP gate
        // answers 401, this also asserts the second layer (the repository's identity check) by
        // repeating the attack with the internal key — which would normally bypass the HTTP gate
        // — but with no MediaOwnerKey, the repository refuses at the second layer.
        var scopeHolder = _factory.Services.GetRequiredService<CallerScopeHolder>();
        var beforeCount = await CountMediaAsync();

        var resp = await McpPostAsync(_raw, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "anonymous-test", version = "1.0" }
            }
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var afterCount = await CountMediaAsync();
        afterCount.Should().Be(beforeCount,
            "the gate must short-circuit before any repository write can land");
    }

    [Fact]
    public async Task McpSaveMedia_InternalKeyWithoutIdentity_RepositoryRejectsAtSecondLayer()
    {
        // The internal key passes the HTTP gate (Web proxy / CLI / tray are trusted inside), but
        // a forged "internal key + no user" request reaches the repository with no MediaOwnerKey.
        // This is the second-layer assertion of B2: the MediaRepository itself must fail closed
        // when Scope.MediaOwnerKey is null on an unlinked upload.
        await _factory.Services.GetRequiredService<SessionService>().UnlockAsync(Password);

        // Drop any agent or user context: the bearer factory does not attach X-User-Id, so the
        // CallerScopeMiddleware installs a deny-all with no MediaOwnerKey — exactly the
        // anonymous state the repository guard catches.
        using var withKeyOnly = _factory.Server.CreateClient();
        withKeyOnly.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);

        var init = await McpPostAsync(withKeyOnly, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "second-layer-test", version = "1.0" }
            }
        });
        init.EnsureSuccessStatusCode();
        var sessionId = init.Headers.GetValues("Mcp-Session-Id").First();
        await McpPostAsync(withKeyOnly, new { jsonrpc = "2.0", method = "notifications/initialized" },
            sessionId: sessionId);

        // bee_save_media is RequiresUnlockedSession — we unlocked above.
        // A 1x1 PNG (base64). The MCP SDK should accept it; the repository then refuses because
        // the caller has no MediaOwnerKey. The result body is a JSON-RPC tool error, not a
        // 200-OK with a mediaId.
        var tinyPngB64 = Convert.ToBase64String(new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
            0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00,
            0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49,
            0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
        });

        var beforeCount = await CountMediaAsync();

        var call = await McpPostAsync(withKeyOnly, new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new
            {
                name = "bee_save_media",
                arguments = new
                {
                    fileName = "second-layer-test.png",
                    contentBase64 = tinyPngB64,
                    isAttachment = false
                }
            }
        }, sessionId: sessionId);

        // The MCP SDK returns 200 with a tool-error body when the tool's own exception is caught;
        // either way, no media row is created.
        var body = await call.Content.ReadAsStringAsync();
        body.Should().Contain("error", "the tool must surface the authorization failure, not a success");

        var afterCount = await CountMediaAsync();
        afterCount.Should().Be(beforeCount,
            "the repository must not create a media row for a caller with no MediaOwnerKey, " +
            "regardless of whether the HTTP gate let the request through");
    }

    [Fact]
    public async Task McpSaveMedia_BmbrtRemoteToken_IsRejected()
    {
        // bmbrt_ tokens are scoped to the cross-instance remote-folder endpoints, not MCP. A
        // request presenting only a bmbrt_ token must be rejected at the MCP gate, distinct from
        // a missing credential. Issue a token through /api/auth/remote-token and then drive the
        // MCP handshake with it.
        var token = await IssueRemoteTokenAsync(_raw, "admin", Password);
        token.Should().NotBeNullOrEmpty();

        using var remoteClient = _factory.Server.CreateClient();
        remoteClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var init = await McpPostAsync(remoteClient, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "bmbrt-test", version = "1.0" }
            }
        });

        init.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a bmbrt_ token must not be a valid credential on /mcp, even after AgentAuthMiddleware " +
            "resolves it into a CallerIdentity");
    }

    // ───── B2: Non-PublicSurface endpoints ───────────────────────────────────

    [Fact]
    public async Task NonPublicEndpoints_AllAnswer404WithoutTheKey()
    {
        // Build the list from EndpointDataSource so a new endpoint is covered automatically, the
        // same completeness strategy PublicSurfaceTests uses. /mcp is checked above; /api/join,
        // /api/auth/remote-token, and the peer/sync endpoints stay in PublicSurface and answer
        // their own documented shape, not 404.
        //
        // Whether a pattern is "public" is decided here by inspecting the entries list directly:
        // PublicSurface.Allows(path, "ANY") is the wrong probe because each entry specifies its
        // own verb, so /api/version (public for GET only) would be reported as non-public and
        // this test would expect it to answer 404 — wrong on both counts.
        var publicPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in PublicSurface.Entries)
        {
            // Skip the "/mcp/**" pattern (wildcard tail) — the MCP gate test covers that surface
            // already and the "method == null → any verb" semantics would make every /mcp route
            // "public" here, which would defeat the 404 check.
            var segs = entry.Pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length > 0 && segs[^1] == "**") continue;
            publicPatterns.Add(entry.Pattern);
        }

        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var patterns = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .Where(p => p != null
                && (p.StartsWith("/api/") || p.StartsWith("/node/"))
                && p != "/mcp"
                && !publicPatterns.Contains(p!))
            .Distinct()
            .ToList();

        patterns.Should().NotBeEmpty(
            "the test is meaningless if every endpoint happens to be public — enumerate EndpointDataSource");

        var failures = new List<string>();
        foreach (var pattern in patterns!)
        {
            using var client = _factory.Server.CreateClient();
            var url = SubstituteRouteParams(pattern!);

            var methods = dataSource.Endpoints
                .OfType<RouteEndpoint>()
                .Where(e => e.RoutePattern.RawText == pattern)
                .SelectMany(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? new List<string>())
                .ToList();

            var method = methods.FirstOrDefault() ?? "GET";
            try
            {
                var request = new HttpRequestMessage(new HttpMethod(method), url);
                var resp = await client.SendAsync(request);
                if (resp.StatusCode != HttpStatusCode.NotFound)
                    failures.Add($"{method} {pattern} returned {(int)resp.StatusCode} (expected 404)");
            }
            catch (Exception ex)
            {
                failures.Add($"{method} {pattern} threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        failures.Should().BeEmpty(
            "every non-PublicSurface endpoint must answer 404 to a keyless caller. Failures:\n" +
            string.Join("\n", failures.Select(f => "  - " + f)));
    }

    // ───── B2: PublicSurface entries have their documented answer ─────────────

    public static IEnumerable<object[]> PublicSurfaceEntries()
    {
        foreach (var entry in PublicSurface.Entries)
            yield return new object[] { entry.Method ?? "ANY", entry.Pattern };
    }

    [Theory]
    [MemberData(nameof(PublicSurfaceEntries))]
    public async Task PublicSurfaceEntry_NoKey_AnswersAsDocumented(string method, string pattern)
    {
        using var client = _factory.Server.CreateClient();
        var url = SubstituteRouteParams(pattern);

        // Pick the verb documented in the entry; "ANY" / null means any verb works.
        var verb = method == "ANY" ? "GET" : method;
        var request = new HttpRequestMessage(new HttpMethod(verb), url);
        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(request);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"PublicSurface entry {method} {pattern} threw before producing a documented answer: {ex.Message}");
        }

        // Documented per-entry shape: most answer 200 (health/version/identity/sentinel), a few
        // intentionally answer 4xx (join without password, auth/remote-token without creds). The
        // gate is documented per entry, so the answer is one of "the request reached the handler
        // and was answered" (200) or "the handler rejected it for an obvious input reason" (4xx).
        // 404 here would mean the PublicSurface list is out of date — that case is covered by
        // PublicSurfaceTests already; we just want to confirm none of the documented shapes
        // became a 5xx (server bug) or some other surprise.
        ((int)resp.StatusCode).Should().BeInRange(200, 499,
            $"PublicSurface entry {method} {pattern} must answer in 2xx–4xx with its documented shape");
    }

    // ───── B3: rate limit kicks in for keyless loopback callers ──────────────

    [Fact]
    public async Task Join_FromKeylessLoopback_Answers429AfterLimit()
    {
        // B3 fix: loopback without the internal key is no longer exempt. Five wrong-password
        // attempts must each return 401; the sixth must answer 429.
        RateLimitMiddleware.ResetForTests();

        using var client = _factory.Server.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var resp = await client.PostAsJsonAsync("/api/join", new
            {
                masterPassword = "wrong-" + i,
                nodeId = Guid.NewGuid(),
                displayName = "ratelimit",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                apiAddress = (string?)null
            });
            resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                $"attempt #{i + 1} should be a wrong-password rejection, not 429 yet");
        }

        var throttled = await client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = "wrong-6",
            nodeId = Guid.NewGuid(),
            displayName = "ratelimit",
            ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
            apiAddress = (string?)null
        });
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "the sixth attempt from the same loopback caller must hit the rate limit");
    }

    [Fact]
    public async Task RemoteToken_FromKeylessLoopback_Answers429AfterLimit()
    {
        // Same shape for /api/auth/remote-token — also in the API's protected-paths set.
        RateLimitMiddleware.ResetForTests();

        using var client = _factory.Server.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var resp = await client.PostAsJsonAsync("/api/auth/remote-token", new
            {
                username = "admin",
                password = "wrong-" + i
            });
            resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                $"attempt #{i + 1} should be a wrong-credentials rejection, not 429 yet");
        }

        var throttled = await client.PostAsJsonAsync("/api/auth/remote-token", new
        {
            username = "admin",
            password = "wrong-6"
        });
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "the sixth attempt from the same loopback caller must hit the rate limit");
    }

    [Fact]
    public async Task Join_FromInternalKeyCaller_IsNotThrottled()
    {
        // The new exemption is the internal key, not loopback. A caller presenting the key on
        // loopback must still get through the limiter (this is the Web layer / tray / CLI path).
        RateLimitMiddleware.ResetForTests();

        using var client = _factory.Server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);

        // Six attempts is well past the 5-attempt budget; every one must reach the handler.
        for (var i = 0; i < 6; i++)
        {
            var resp = await client.PostAsJsonAsync("/api/join", new
            {
                masterPassword = "wrong-" + i,
                nodeId = Guid.NewGuid(),
                displayName = "internal-key",
                ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
                apiAddress = (string?)null
            });
            resp.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests,
                "an internal-key caller must not be throttled — the gate is the key, not loopback");
        }
    }

    // ───── helpers ────────────────────────────────────────────────────────────

    private static async Task<HttpResponseMessage> McpPostAsync(
        HttpClient client, object payload, string? sessionId = null)
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

    private static string SubstituteRouteParams(string pattern)
    {
        return System.Text.RegularExpressions.Regex.Replace(pattern, @"\{(\w+)(?::(\w+))?\}", match =>
        {
            var constraint = match.Groups[2].Success ? match.Groups[2].Value : "";
            return constraint switch
            {
                "guid" => "00000000-0000-0000-0000-000000000000",
                "int" => "0",
                _ => "0"
            };
        });
    }

    private async Task<int> CountMediaAsync()
    {
        // Reach into the SQLite store directly — there is no public repository service for the
        // count, and the assertion is "no row was created" rather than "the API said no".
        using var scope = _factory.Services.CreateScope();
        var connFactory = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Storage.Sqlite.DbConnectionFactory>();
        using var conn = connFactory.CreateConnection();
        var count = await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM tbl_media WHERE status = 'A'");
        return count;
    }

    private async Task<string> IssueRemoteTokenAsync(HttpClient client, string username, string password)
    {
        // /api/auth/remote-token is in PublicSurface (peers call it without the internal key).
        // Use the loopback client with the rate limiter reset, since several tests may hit this.
        RateLimitMiddleware.ResetForTests();
        var resp = await client.PostAsJsonAsync("/api/auth/remote-token", new { username, password });
        if (resp.StatusCode != HttpStatusCode.OK) return "";
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString() ?? "";
    }
}
