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
/// <see cref="BmbWebApplicationFactory.CreateClient"/> has never exercised because it always stamps
/// X-Internal-Key + X-User-Role on every request.
///
/// <para>Three claims, each covered by its own test area:</para>
/// <list type="bullet">
///   <item><description>Every MCP tool is unreachable without credentials — both at the HTTP
///   layer (McpIdentityGateMiddleware answers 401 before the SDK runs) and as a side-effect
///   (no media row, no event row written). The test drives the full initialize → initialized →
///   tools/call sequence and asserts the FIRST request is already 401, so an MCP session cannot
///   be opened at all.</description></item>
///   <item><description>Every endpoint NOT in <see cref="PublicSurface.Entries"/> answers 404 to a
///   keyless caller. Enumerating <c>EndpointDataSource</c> covers new endpoints automatically,
///   and every (route × method) pair is probed (not just the first method on each route).</description></item>
///   <item><description>Every <see cref="PublicSurface.Entries"/> entry returns the EXACT
///   documented anonymous status (the table is written out explicitly in
///   <see cref="DocumentedAnonymousStatuses"/>), not "any 2xx–4xx".</description></item>
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
    public async Task McpTool_NoCredentials_FullSequenceAnswers401AndCreatesNoSession(string toolName)
    {
        // Drive the full initialize → notifications/initialized → tools/call sequence, the same
        // handshake an MCP client would do. The gate must short-circuit with 401 at the very
        // FIRST request — so an anonymous caller never gets to "notifications/initialized", never
        // gets to "tools/call", and never receives an Mcp-Session-Id (no MCP session exists).
        // Pinned: the FIRST request's status is 401, the response carries no Mcp-Session-Id,
        // and tbl_media / tbl_event row counts are unchanged.
        var beforeMedia = await CountMediaAsync();
        var beforeEvents = await CountEventAsync();

        var initResp = await McpPostAsync(_raw, new
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

        initResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            $"the MCP gate must answer 401 to a keyless request, even for initialize (tool under test: {toolName})");
        initResp.Headers.WwwAuthenticate.ToString().Should().Contain("Bearer",
            "RFC 7235: a 401 must include WWW-Authenticate so clients know what credential to send");
        initResp.Headers.Contains("Mcp-Session-Id").Should().BeFalse(
            "an anonymous caller must never receive an MCP session id");

        // The full sequence still fails — drive the next two requests so a regression that lets
        // initialize through but breaks the rest of the handshake would surface here too.
        var notifResp = await McpPostAsync(_raw, new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        });
        // Notifications don't carry an id, so the SDK's answer (when reached) is a JSON-RPC
        // success with no body, status 202. The gate answers 401 before the SDK runs.
        notifResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            $"the gate must reject notifications/initialized too (tool under test: {toolName})");

        var callResp = await McpPostAsync(_raw, new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new { name = toolName, arguments = new { } }
        });
        callResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            $"the gate must reject tools/call {toolName} too");

        var afterMedia = await CountMediaAsync();
        var afterEvents = await CountEventAsync();
        afterMedia.Should().Be(beforeMedia,
            $"the gate must short-circuit {toolName} before any media row can be written");
        afterEvents.Should().Be(beforeEvents,
            $"the gate must short-circuit {toolName} before any event row can be written");
    }

    [Fact]
    public async Task McpSaveMedia_NoCredentials_FullSequenceLeavesNothingBehind()
    {
        // The specific hole B2 closes: an anonymous caller driving the full MCP handshake and
        // then calling bee_save_media must NOT leave a media row OR an event row behind.
        // Drive initialize → notifications/initialized → tools/call with a real PNG body, so a
        // regression that let a 401 slip into a 200-with-row would be caught.
        var beforeMedia = await CountMediaAsync();
        var beforeEvents = await CountEventAsync();

        var initResp = await McpPostAsync(_raw, new
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
        initResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var notifResp = await McpPostAsync(_raw, new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        });
        notifResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var callResp = await McpPostAsync(_raw, new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new
            {
                name = "bee_save_media",
                arguments = new
                {
                    fileName = "anonymous.png",
                    contentBase64 = TinyPngBase64,
                    isAttachment = false
                }
            }
        });
        callResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var afterMedia = await CountMediaAsync();
        var afterEvents = await CountEventAsync();
        afterMedia.Should().Be(beforeMedia,
            "the gate must short-circuit bee_save_media before any media row can be written");
        afterEvents.Should().Be(beforeEvents,
            "the gate must short-circuit bee_save_media before any event row can be written");
    }

    [Fact]
    public async Task McpSaveMedia_BearerJunk_AlsoRejectedAndLeavesNothingBehind()
    {
        // Round-1 finding: my old gate accepted any Bearer value because it only checked for
        // the absence of the header, not its contents. "Bearer junk" must now be rejected too,
        // and the gate must short-circuit before the SDK can ever run bee_save_media.
        var beforeMedia = await CountMediaAsync();
        var beforeEvents = await CountEventAsync();

        using var junkClient = _factory.Server.CreateClient();
        junkClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "junk");

        var initResp = await McpPostAsync(junkClient, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "junk-bearer", version = "1.0" }
            }
        });
        initResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "'Bearer junk' is not a recognised credential shape and must not open an MCP session");

        var afterMedia = await CountMediaAsync();
        var afterEvents = await CountEventAsync();
        afterMedia.Should().Be(beforeMedia,
            "a Bearer junk header must not let bee_save_media reach the repository");
        afterEvents.Should().Be(beforeEvents,
            "a Bearer junk header must not let bee_save_media log a sync event");
    }

    [Fact]
    public async Task McpSaveMedia_InternalKeyWithoutIdentity_RepositoryRejectsAtSecondLayer()
    {
        // The internal key passes the HTTP gate (Web proxy / CLI / tray are trusted inside), but
        // a forged "internal key + no user" request reaches the repository with no MediaOwnerKey.
        // This is the second-layer assertion of B2: the MediaRepository itself must fail closed
        // when Scope.MediaOwnerKey is null on an unlinked upload.
        await _factory.Services.GetRequiredService<SessionService>().UnlockAsync(Password);

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
        var beforeMedia = await CountMediaAsync();
        var beforeEvents = await CountEventAsync();

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
                    contentBase64 = TinyPngBase64,
                    isAttachment = false
                }
            }
        }, sessionId: sessionId);

        // The MCP SDK returns 200 with a tool-error body when the tool's own exception is caught;
        // either way, no media row is created.
        var body = await call.Content.ReadAsStringAsync();
        body.Should().Contain("error", "the tool must surface the authorization failure, not a success");

        var afterMedia = await CountMediaAsync();
        var afterEvents = await CountEventAsync();
        afterMedia.Should().Be(beforeMedia,
            "the repository must not create a media row for a caller with no MediaOwnerKey, " +
            "regardless of whether the HTTP gate let the request through");
        afterEvents.Should().Be(beforeEvents,
            "the repository must not log a media_create event when no row is created");
    }

    [Fact]
    public async Task McpSaveMedia_BmbrtRemoteToken_ResolvedAndUnresolved_AreBothRejected()
    {
        // bmbrt_ tokens are scoped to the cross-instance remote-folder endpoints, not MCP.
        // Classify from the header value, not from the AgentAuthMiddleware resolution marker —
        // a valid bmbrt_ (resolves into CallerIdentity) AND an unknown/expired one (never
        // resolves) must BOTH be rejected, with the same shape, so an anonymous caller cannot
        // open an MCP session by holding a stolen token.
        var validToken = await IssueRemoteTokenAsync(_raw, "admin", Password);
        validToken.Should().NotBeNullOrEmpty();

        var beforeMedia = await CountMediaAsync();
        var beforeEvents = await CountEventAsync();

        async Task AssertRejectedAsync(HttpClient client, string label)
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
                    clientInfo = new { name = label, version = "1.0" }
                }
            });
            initResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                $"{label}: a bmbrt_ token must not be a valid credential on /mcp");
            initResp.Headers.Contains("Mcp-Session-Id").Should().BeFalse(
                $"{label}: the gate must reject before any MCP session is created");
        }

        // Resolved bmbrt_ — AgentAuthMiddleware sets CallerIdentity + IsRemoteToken for this one.
        using (var resolvedClient = _factory.Server.CreateClient())
        {
            resolvedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", validToken);
            await AssertRejectedAsync(resolvedClient, "resolved-bmbrt");
        }

        // Unresolved bmbrt_ — never reaches the IsRemoteToken branch, so the gate must catch it
        // from the bearer prefix alone.
        using (var unknownClient = _factory.Server.CreateClient())
        {
            unknownClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", "bmbrt_unknown_0000000000000000000000000000000000000000");
            await AssertRejectedAsync(unknownClient, "unknown-bmbrt");
        }

        // Drive the full sequence for one of them too, to confirm no MCP session opens and no
        // event/media row is written even when a forged bmbrt_ tries the full handshake.
        using (var callClient = _factory.Server.CreateClient())
        {
            callClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", validToken);
            await McpPostAsync(callClient, new
            {
                jsonrpc = "2.0",
                method = "notifications/initialized"
            });
            await McpPostAsync(callClient, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new
                {
                    name = "bee_save_media",
                    arguments = new
                    {
                        fileName = "bmbrt.png",
                        contentBase64 = TinyPngBase64,
                        isAttachment = false
                    }
                }
            });
        }

        var afterMedia = await CountMediaAsync();
        var afterEvents = await CountEventAsync();
        afterMedia.Should().Be(beforeMedia,
            "bmbrt_ tokens must not let bee_save_media reach the repository, resolved or not");
        afterEvents.Should().Be(beforeEvents,
            "bmbrt_ tokens must not let bee_save_media log a sync event, resolved or not");
    }

    // ───── B2: Non-PublicSurface endpoints ───────────────────────────────────

    [Fact]
    public async Task NonPublicEndpoints_EveryRouteEveryMethod_Answers404WithoutTheKey()
    {
        // Enumerate EndpointDataSource and probe every (route × method) pair, not just the first
        // method per route. Compare against method-aware PublicSurface.Allows so an entry that
        // permits only GET does not accidentally make POSTs public. Routes outside the prefix
        // set must still answer 404, so /mcp, /health, and anything else the API maps is
        // covered automatically.
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var allRoutes = dataSource.Endpoints.OfType<RouteEndpoint>().ToList();
        var probes = new List<(string Method, string Pattern, string Url)>();

        foreach (var ep in allRoutes)
        {
            var pattern = ep.RoutePattern.RawText;
            if (pattern == null) continue;
            var methods = ep.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? new List<string> { "GET" };
            var url = SubstituteRouteParams(pattern);
            foreach (var m in methods)
                probes.Add((m, pattern, url));
        }

        probes.Should().NotBeEmpty(
            "the test is meaningless if EndpointDataSource produced nothing to probe");

        var failures = new List<string>();
        foreach (var (method, pattern, url) in probes)
        {
            // Method-aware: a route is public only if some entry covers THIS method. This is
            // stricter than checking the pattern alone: /api/version is public for GET only, so
            // a POST to it must answer 404, not the GET-200.
            var isPublic = PublicSurface.Entries.Any(entry =>
                PatternAgrees(entry.Pattern, pattern) && MatchesVerb(entry.Method, method));
            // /mcp subtree entries use "/mcp/**" — PublicSurface.Matches already handles that
            // for "ANY" via the trailing **, but Method is null on those entries (any verb),
            // so MatchesVerb returns true for every verb. Same as PublicSurface.Allows. Good.

            if (isPublic) continue;

            using var client = _factory.Server.CreateClient();
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
            "every non-PublicSurface (route × method) pair must answer 404 to a keyless caller. " +
            "Failures:\n" + string.Join("\n", failures.Select(f => "  - " + f)));
    }

    // ───── B2: PublicSurface entries have their exact documented answer ─────

    /// <summary>
    /// The exact anonymous-call answer per PublicSurface entry. Built from a one-off probe (see
    /// PublicSurfaceProbeTests) and pinned here as a deliberate contract: a future change that
    /// shifts the answer (e.g. locking /api/snapshots/restore/progress behind auth) must update
    /// this table AND the brief that justifies the change.
    /// </summary>
    private static readonly (string Method, string Pattern, HttpStatusCode Status)[] DocumentedAnonymousStatuses =
    {
        ("ANY",  "/health",                                HttpStatusCode.OK),
        ("GET",  "/api/version",                           HttpStatusCode.OK),
        ("ANY",  "/mcp",                                   HttpStatusCode.Unauthorized),
        ("ANY",  "/mcp/**",                                HttpStatusCode.Unauthorized),
        ("GET",  "/api/sync/identity",                     HttpStatusCode.OK),
        ("GET",  "/api/sync/sentinel",                     HttpStatusCode.OK),
        ("POST", "/api/sync/challenge",                    HttpStatusCode.OK),
        ("POST", "/api/sync/authenticate",                 HttpStatusCode.BadRequest),
        ("ANY",  "/api/sync/events",                       HttpStatusCode.Unauthorized),
        ("GET",  "/api/sync/snapshot/for-join",            HttpStatusCode.Unauthorized),
        ("POST", "/api/sync/report-position",              HttpStatusCode.BadRequest),
        ("POST", "/api/sync/blobs",                        HttpStatusCode.Unauthorized),
        ("POST", "/api/sync/blobs/check",                  HttpStatusCode.Unauthorized),
        ("POST", "/api/sync/blobs/get",                    HttpStatusCode.Unauthorized),
        ("POST", "/api/sync/probe-relay",                  HttpStatusCode.Unauthorized),
        ("POST", "/api/join",                              HttpStatusCode.Unauthorized),
        ("GET",  "/api/snapshots/restore/{eventId}/file",  HttpStatusCode.Unauthorized),
        ("GET",  "/api/snapshots/restore/progress",        HttpStatusCode.OK),
        ("GET",  "/api/dek-rotation/progress",             HttpStatusCode.OK),
        ("POST", "/api/auth/remote-token",                 HttpStatusCode.Unauthorized),
        ("GET",  "/api/folders/accessible",                HttpStatusCode.Unauthorized),
        ("GET",  "/api/folders/by-path/snapshot",          HttpStatusCode.BadRequest),
    };

    [Theory]
    [MemberData(nameof(PublicSurfaceEntryData))]
    public async Task PublicSurfaceEntry_NoKey_AnswersExactDocumentedStatus(string method, string pattern, HttpStatusCode expected)
    {
        using var client = _factory.Server.CreateClient();
        var url = SubstituteRouteParams(pattern);

        // Build a request that matches what a sensible anonymous caller would send:
        // - POSTs get a body that the handler actually reads (so the answer is the documented one,
        //   not a 400 because the body was empty);
        // - GETs with required query parameters get them.
        var verb = method == "ANY" ? "GET" : method;
        var request = new HttpRequestMessage(new HttpMethod(verb), url);

        if (verb == "POST")
        {
            request.Content = pattern switch
            {
                "/api/join" => JsonContent.Create(new
                {
                    masterPassword = "wrong-password-by-design",
                    nodeId = Guid.NewGuid(),
                    displayName = "probe",
                    ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32])
                }),
                "/api/auth/remote-token" => JsonContent.Create(new
                {
                    username = "probe",
                    password = "wrong-password-by-design"
                }),
                _ => JsonContent.Create(new { })
            };
        }
        else if (pattern == "/api/folders/by-path/snapshot")
        {
            url += "?path=/probe";
        }
        else if (pattern == "/api/snapshots/restore/progress")
        {
            url += "?eventId=" + Guid.Empty;
        }

        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(request);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"PublicSurface entry {method} {pattern} threw before producing its documented answer: {ex.Message}");
        }

        resp.StatusCode.Should().Be(expected,
            $"{method} {pattern} must answer {expected} for a keyless caller with a sensible request. " +
            $"If the documented answer changed, update DocumentedAnonymousStatuses AND document why in the change.");
    }

    public static IEnumerable<object[]> PublicSurfaceEntryData()
    {
        foreach (var (method, pattern, status) in DocumentedAnonymousStatuses)
            yield return new object[] { method, pattern, status };
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

    /// <summary>
    /// Valid 1x1 PNG (base64). Small enough to stay below the 20 MB upload cap and to round-trip
    /// through any test transport, large enough that "is this a real image" is unambiguous if a
    /// future test asserts on the body.
    /// </summary>
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

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

    /// <summary>
    /// True when <paramref name="published"/> (a PublicSurface pattern with {param} / ** segments)
    /// matches <paramref name="mapped"/> (an EndpointDataSource raw text). Same shape as
    /// PublicSurfaceTests.PatternsAgree so the two tests cannot drift on what "matches" means.
    /// </summary>
    private static bool PatternAgrees(string published, string mapped)
    {
        var publishedSegments = published.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var mappedSegments = mapped.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < publishedSegments.Length; i++)
        {
            if (publishedSegments[i] == "**") return mappedSegments.Length >= i;
            if (i >= mappedSegments.Length) return false;
            var isPublishedParam = publishedSegments[i].StartsWith('{');
            var isMappedParam = mappedSegments[i].StartsWith('{');
            if (isPublishedParam || isMappedParam)
            {
                if (isPublishedParam != isMappedParam) return false;
                continue;
            }
            if (!string.Equals(publishedSegments[i], mappedSegments[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return mappedSegments.Length == publishedSegments.Length;
    }

    private static bool MatchesVerb(string? entryMethod, string requestMethod) =>
        entryMethod == null || string.Equals(entryMethod, requestMethod, StringComparison.OrdinalIgnoreCase);

    private async Task<int> CountMediaAsync()
    {
        // Reach into the SQLite store directly — there is no public repository service for the
        // count, and the assertion is "no row was created" rather than "the API said no".
        using var scope = _factory.Services.CreateScope();
        var connFactory = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Storage.Sqlite.DbConnectionFactory>();
        using var conn = connFactory.CreateConnection();
        return await Dapper.SqlMapper.ExecuteScalarAsync<int>(
            conn, "SELECT COUNT(*) FROM tbl_media WHERE status = 'A'");
    }

    private async Task<int> CountEventAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var connFactory = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Storage.Sqlite.DbConnectionFactory>();
        using var conn = connFactory.CreateConnection();
        return await Dapper.SqlMapper.ExecuteScalarAsync<int>(conn, "SELECT COUNT(*) FROM tbl_event");
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
