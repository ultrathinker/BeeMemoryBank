using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

public class EndpointAuthGuardrailTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();

    private static readonly HashSet<string> PublicRoutes =
    [
        "/health",
        "/api/version",
        "/api/init/status",
        "/api/session/status",
        "/api/session/unlock",
        "/api/session/login",
        "/api/sync/identity",
        "/api/sync/sentinel",
        "/api/sync/challenge",
        "/api/sync/authenticate",
        "/api/join",
        "/api/snapshots/restore/progress",
    ];

    private static readonly HashSet<string> SyncTokenRoutes =
    [
        "/api/sync/events",
        "/api/sync/report-position",
    ];

    public async Task InitializeAsync()
    {
        await _factory.InitializeNodeAsync();
    }

    public Task DisposeAsync()
    {
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Every_ApiGetEndpoint_RejectsUnauthenticatedRequests()
    {
        using var client = _factory.Server.CreateClient();

        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var routeEndpoints = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => IsMethod(e, "GET"))
            .Where(e =>
            {
                var p = e.RoutePattern.RawText;
                return p != null && (p.StartsWith("/api/") || p == "/health");
            })
            .ToList();

        var failures = new List<string>();

        foreach (var endpoint in routeEndpoints)
        {
            var pattern = endpoint.RoutePattern.RawText!;
            var url = SubstituteRouteParams(pattern);

            if (PublicRoutes.Contains(pattern))
                continue;

            if (SyncTokenRoutes.Contains(pattern))
                continue;

            try
            {
                var response = await client.GetAsync(url);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    failures.Add($"GET {url} returned 200 OK without authentication. " +
                                 "Add InternalKeyValidator.Validate() or add to PublicRoutes.");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"GET {url} threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        failures.Should().BeEmpty(
            "Every /api/* GET endpoint must reject unauthenticated requests. " +
            "If a new public endpoint is intentionally unauthenticated, add it to PublicRoutes.");
    }

    [Fact]
    public async Task Every_ApiMutationEndpoint_RejectsUnauthenticatedRequests()
    {
        using var client = _factory.Server.CreateClient();

        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var mutationEndpoints = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e =>
            {
                var methods = e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
                return methods != null && methods.Any(m => m is "POST" or "PUT" or "DELETE" or "PATCH");
            })
            .Where(e =>
            {
                var p = e.RoutePattern.RawText;
                return p != null && p.StartsWith("/api/");
            })
            .ToList();

        var failures = new List<string>();

        foreach (var endpoint in mutationEndpoints)
        {
            var pattern = endpoint.RoutePattern.RawText!;
            var url = SubstituteRouteParams(pattern);
            var method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.First(m => m is "POST" or "PUT" or "DELETE" or "PATCH");

            if (PublicRoutes.Contains(pattern))
                continue;

            if (SyncTokenRoutes.Contains(pattern))
                continue;

            try
            {
                HttpResponseMessage response;
                if (method == "DELETE")
                {
                    var req = new HttpRequestMessage(HttpMethod.Delete, url);
                    response = await client.SendAsync(req);
                }
                else if (method == "PATCH")
                {
                    var req = new HttpRequestMessage(new HttpMethod("PATCH"), url)
                    {
                        Content = JsonContent.Create(new { path = "/test" })
                    };
                    response = await client.SendAsync(req);
                }
                else
                {
                    var body = BuildMinimalBody(pattern);
                    var req = new HttpRequestMessage(new HttpMethod(method), url)
                    {
                        Content = JsonContent.Create(body)
                    };
                    response = await client.SendAsync(req);
                }

                if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
                {
                    failures.Add($"{method} {url} returned {response.StatusCode} without authentication. " +
                                 "Add InternalKeyValidator.Validate() or add to PublicRoutes.");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{method} {url} threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        failures.Should().BeEmpty(
            "Every /api/* mutation endpoint must reject unauthenticated requests. " +
            "If a new public endpoint is intentionally unauthenticated, add it to PublicRoutes.");
    }

    // ── Role guardrail ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every mutating route a caller on the built-in <c>user</c> role IS allowed to reach.
    /// Adding a mutating endpoint without deciding its role requirement turns
    /// <see cref="Every_MutatingEndpoint_IsSuperadminGated_UnlessExplicitlyAllowed"/> red; the fix
    /// is to attach <c>.RequireSuperadmin()</c> at the registration site, or — if a regular user
    /// really may call it — to add it here with a reason.
    ///
    /// Keyed by "METHOD path" so a route that is user-writable under one verb and admin-only under
    /// another (none today, but the shape allows it) stays expressible.
    /// </summary>
    private static readonly HashSet<string> UserAllowedMutations =
    [
        // Content. This is the product: any signed-in user creates, edits, moves, protects and
        // deletes articles and folders. What they may touch is decided per-folder by the ACL
        // inside the handler, not by their role.
        "POST /api/articles/",
        "PUT /api/articles/{id:guid}",
        "DELETE /api/articles/{id:guid}",
        "POST /api/articles/{id:guid}/move",
        "POST /api/articles/{id:guid}/copy",
        "POST /api/articles/{id:guid}/protect",
        "POST /api/articles/{id:guid}/unprotect",
        "POST /api/articles/{id:guid}/change-passphrase",
        "POST /api/articles/{id:guid}/unlock",
        "POST /api/articles/{id:guid}/relock",
        "PUT /api/articles/{id:guid}/concept-tags",
        "POST /api/folders/",
        "PATCH /api/folders/",
        "POST /api/folders/move",
        "DELETE /api/folders/",
        "POST /api/folders/{id:guid}/copy",

        // Media, import and export follow the same article/folder ACL.
        "POST /api/media/",
        "DELETE /api/media/{id:guid}",
        "POST /api/import/bee",
        "POST /api/import/obsidian",
        "POST /api/downloads/prepare",

        // Personal annotations on content the user can already see.
        "POST /api/comments",
        "DELETE /api/comments/{id:int}",
        "POST /api/favorites/{articleId:guid}",
        "DELETE /api/favorites/{articleId:guid}",
        "POST /api/favorites/{articleId:guid}/move",
        "POST /api/favorites/reset-order",

        // Search is POST only because the query travels in the body.
        "POST /api/search/semantic",
        "POST /api/search/hybrid",

        // Self-service. The subject is always the caller (forwarded X-User-Id), never a chosen
        // target — which is exactly why these cannot be expressed as an endpoint filter.
        "POST /api/users/me/change-password",

        // A user provisions and revokes their OWN MCP agents; the handler compares the caller to
        // agent.OwnerUserId. ?all=true on the listing is the superadmin-only part, and that is a
        // GET, outside this test.
        "POST /api/agents/",
        "DELETE /api/agents/{id:int}",

        // Chat: sending, confirming a tool call, tidying one's own conversations, and waiving
        // one's OWN write confirmation. Key/model/node-wide settings are superadmin and are NOT
        // here. Reachability is additionally gated per-user by ChatAccessEndpointFilter.
        "POST /api/chat/stream",
        "POST /api/chat/stream/{conversationId:guid}/confirm",
        "PATCH /api/chat/conversations/{id:guid}",
        "DELETE /api/chat/conversations/{id:guid}",
        "DELETE /api/chat/home-pinned",
        "PATCH /api/chat/settings/auto-approve",
    ];

    /// <summary>
    /// Mutating routes whose authorization is not role-based at all, so "403 for role=user" is the
    /// wrong question to ask of them. Each is authenticated some other way; listed separately from
    /// <see cref="UserAllowedMutations"/> so the two reasons never get conflated.
    /// </summary>
    private static readonly HashSet<string> NonRoleGatedMutations =
    [
        // Node bootstrap: these run BEFORE any user exists, and refuse once the node is
        // initialized. There is no role to check yet.
        "POST /api/init/standalone",
        "POST /api/init/join",

        // Credential exchange — username + password in the body is the authentication.
        "POST /api/auth/remote-token",

        // Peer-to-peer sync. Authenticated by the Ed25519 challenge/response bearer token issued
        // by /api/sync/authenticate; a browser user never calls these.
        "POST /api/sync/blobs",
        "POST /api/sync/blobs/check",
        "POST /api/sync/blobs/get",
        "POST /api/sync/probe-relay",
    ];

    /// <summary>
    /// Calls every mutating endpoint under /api/ (plus /node/, which is the same kind of surface
    /// under a different prefix) as an authenticated caller holding the built-in <c>user</c> role,
    /// and requires 403. Two assertions per route, because either one alone can pass while the
    /// endpoint is in fact unguarded:
    ///
    /// <list type="bullet">
    ///   <item>the route carries <see cref="RequiresSuperadmin"/> metadata — i.e. the gate was
    ///   spelled <c>.RequireSuperadmin()</c> and not re-invented as another inline header
    ///   compare;</item>
    ///   <item>a real request actually comes back 403 — i.e. the filter is reached. Minimal-API
    ///   endpoint filters run AFTER parameter binding, so a route whose body will not bind answers
    ///   400 before the gate ever runs; that shows up here as a failure rather than a silent
    ///   pass.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Every_MutatingEndpoint_IsSuperadminGated_UnlessExplicitlyAllowed()
    {
        // A real, active, non-superadmin user: filters that resolve the caller (chat access, and
        // anything reading CallerIdentity.UserId) answer 401 rather than 403 for an id that does
        // not exist, which would look like a failure for the wrong reason.
        int userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            userId = await userRepo.CreateAsync(new User
            {
                Username = "guardrail-regular-user",
                DisplayName = "Guardrail Regular User",
                Role = UserRoles.User,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
        }

        // base.CreateClient(), not the factory helper: that one stamps X-User-Role: superadmin.
        using var client = _factory.Server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        client.DefaultRequestHeaders.Add("X-User-Role", UserRoles.User);
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());

        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var failures = new List<string>();

        foreach (var endpoint in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = endpoint.RoutePattern.RawText;
            if (pattern == null || !(pattern.StartsWith("/api/") || pattern.StartsWith("/node/")))
                continue;

            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            var method = methods?.FirstOrDefault(m => m is "POST" or "PUT" or "PATCH" or "DELETE");
            if (method == null)
                continue;

            var key = $"{method} {pattern}";
            if (UserAllowedMutations.Contains(key) || NonRoleGatedMutations.Contains(key))
                continue;
            if (PublicRoutes.Contains(pattern) || SyncTokenRoutes.Contains(pattern))
                continue;

            if (endpoint.Metadata.GetMetadata<RequiresSuperadmin>() is null)
            {
                failures.Add($"{key} has no .RequireSuperadmin() on it. A new mutating endpoint " +
                             "must declare its role requirement: add .RequireSuperadmin() at the " +
                             "registration site, or add the route to UserAllowedMutations (or " +
                             "NonRoleGatedMutations) with a comment saying why a regular user may " +
                             "call it.");
            }

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(BuildProbe(method, pattern));
            }
            catch (Exception ex)
            {
                failures.Add($"{key} threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
                continue;

            var reason = response.StatusCode switch
            {
                HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed =>
                    "the request never reached the handler — fix the URL/verb this test builds for it",
                HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType =>
                    "the request failed model binding, which happens BEFORE endpoint filters run — " +
                    "give the route a bindable body in BuildProbe so the gate is actually exercised",
                _ => "a caller on the 'user' role must not get past the superadmin gate",
            };
            failures.Add($"{key} returned {(int)response.StatusCode} {response.StatusCode} for a " +
                         $"caller on the '{UserRoles.User}' role, expected 403 — {reason}.");
        }

        failures.Should().BeEmpty(
            "every mutating endpoint must decide, at its registration site, whether a regular user " +
            "may call it. Gate it with .RequireSuperadmin(), or list it in UserAllowedMutations.");
    }

    // ── Role guardrail, read side ─────────────────────────────────────────────────────────

    /// <summary>
    /// Every GET a caller on the built-in <c>user</c> role IS allowed to reach. Same contract as
    /// <see cref="UserAllowedMutations"/>: a new GET that is neither gated nor listed here turns
    /// <see cref="Every_ReadEndpoint_IsSuperadminGated_UnlessExplicitlyAllowed"/> red.
    ///
    /// Reads need the list far more than mutations do — most of this API exists to be read by
    /// ordinary users — so the value of the test is not the size of the allow-list but that adding
    /// to it is a deliberate act. <c>GET /api/users</c> and the three <c>GET /api/whitelist</c>
    /// routes sat in this category silently until someone went looking.
    /// </summary>
    private static readonly HashSet<string> UserAllowedReads =
    [
        // Content and the tree it lives in. Which articles and folders come back is decided
        // per-folder by the ACL inside the handler, not by the caller's role.
        "GET /api/articles/",
        "GET /api/articles/{id:guid}",
        "GET /api/articles/{id:guid}/content",
        "GET /api/articles/{id:guid}/edit-content",
        "GET /api/articles/{id:guid}/versions",
        "GET /api/articles/{id:guid}/versions/{versionNumber:int}",
        "GET /api/articles/{id:guid}/concept-tags",
        "GET /api/articles/{id:guid}/related",
        "GET /api/articles/{articleId:guid}/media",
        "GET /api/media/{id:guid}",
        "GET /api/tree",
        "GET /api/tree/children",
        "GET /api/folders/search",
        "GET /api/folders/download",

        // "What am I allowed to do here?" — the UI asks this to decide which buttons to draw. The
        // answer is about the caller themselves, and refusing it would just make the UI guess.
        "GET /api/access/readonly-paths",
        "GET /api/access/folder-permissions",

        // Search and the concept-tag graph, both ACL-filtered per caller.
        "GET /api/search",
        "GET /api/concept-tags",
        "GET /api/concept-tags/graph",
        "GET /api/concept-tags/graph/home",
        "GET /api/concept-tags/graph/search",
        "GET /api/concept-tags/graph/neighbors",
        "GET /api/concept-tags/{name}/articles",

        // Personal annotations and history over content the caller can already see.
        "GET /api/comments",
        "GET /api/favorites/",
        "GET /api/activity",

        // A one-shot token minted by POST /api/downloads/prepare, which is itself user-allowed.
        // The token is the authorization; a role check here would add nothing.
        "GET /api/downloads/{token}",

        // Conditional, so not expressible as a filter: everyone lists their OWN agents, and only
        // a superadmin widens it with ?all=true (checked inside the handler).
        "GET /api/agents/",

        // Self-service. The subject is the caller's own forwarded X-User-Id, never a chosen
        // target. /me/stamp in particular is revalidated on every Web request for every signed-in
        // user — gating it would sign out everyone who is not a superadmin.
        "GET /api/users/me/stamp",
        "GET /api/chat/settings/auto-approve",

        // Chat, for a user who has chat access: their own conversations, messages and attachments
        // (ownership is checked in the handler), plus the two read-only bits the composer renders.
        // Reachability is additionally gated per-user by ChatAccessEndpointFilter — which is why
        // several of these answer 403 here anyway, for a reason that is not the role gate.
        "GET /api/chat/access",
        "GET /api/chat/conversations",
        "GET /api/chat/conversations/{id:guid}/messages",
        "GET /api/chat/attachments/{id:guid}",
        "GET /api/chat/home-pinned",
        "GET /api/chat/settings/effective-text-model",
        // Enabled models only, for the per-conversation picker — the sibling /models/all, which
        // includes disabled entries and is the admin catalogue, IS superadmin.
        "GET /api/chat/models",

        // Node-wide facts the browser needs before it knows who the visitor is: the product name
        // in the header, whether the vault is unlocked, whether the node is set up at all.
        "GET /api/branding/",
        "GET /api/session/status",
        "GET /api/init/status",
        "GET /api/version",

        // Cookie lifetime and sliding-expiration flag. Cannot be gated: the Web layer reads it
        // from a middleware that runs on the FIRST request the process sees, to configure
        // CookieAuthenticationOptions — that request is often an anonymous visitor on /Login, who
        // has no role header at all, and gating it would silently pin every node to the default
        // 48h. The values are a policy setting, not a secret; writing them is superadmin.
        "GET /api/session/settings",

        // Polled by the sync UI to decide whether to refresh; returns a count, no topology.
        "GET /api/sync/ping",
    ];

    /// <summary>
    /// Reads whose authorization is not role-based, so "403 for role=user" is the wrong question.
    /// Kept apart from <see cref="UserAllowedReads"/> for the same reason
    /// <see cref="NonRoleGatedMutations"/> is: the two reasons must not get conflated.
    /// </summary>
    private static readonly HashSet<string> NonRoleGatedReads =
    [
        // Peer-to-peer sync. Anonymous by necessity (a peer has no internal key and no user), or
        // authenticated by the Ed25519-handshake bearer token.
        "GET /api/sync/identity",
        "GET /api/sync/sentinel",
        "GET /api/sync/events",
        "GET /api/sync/snapshot/for-join",
        "GET /api/snapshots/restore/{eventId}/file",

        // Anonymous on purpose so the locked splash screen can poll it; the handler strips
        // everything but a coarse status for callers without the internal key.
        "GET /api/snapshots/restore/progress",

        // Read-only mirrors served to ANOTHER person's node, authenticated by a bmbrt_ remote
        // token issued to it. A remote token is never superadmin, so these can never be gated on
        // the role — see RemoteAuthEndpoints.
        "GET /api/folders/accessible",
        "GET /api/folders/by-path/snapshot",
        "GET /api/articles/{id:guid}/version",
    ];

    /// <summary>
    /// The read counterpart of <see cref="Every_MutatingEndpoint_IsSuperadminGated_UnlessExplicitlyAllowed"/>:
    /// calls every GET under /api/ (plus /node/) as a real, active caller on the built-in
    /// <c>user</c> role and requires 403 unless the route is listed above.
    ///
    /// <para>Both halves of the mutation test are kept — metadata AND a live 403 — and for reads
    /// the pairing matters MORE, not less. A GET has several honest ways to answer 403 that have
    /// nothing to do with the caller's role: a locked session, a folder ACL denial, the chat-access
    /// filter. Asserting only the status code would let a completely ungated listing pass because
    /// the vault happened to be locked. Asserting only the metadata would let a route that declares
    /// the gate but never reaches it pass. Together they say: the gate is declared at the
    /// registration site, and it is the thing that answered.</para>
    ///
    /// <para>The one real asymmetry with the mutation test is the failure mode: a GET binds route
    /// values and query strings rather than a body, so the 400-before-the-filter trap shows up as a
    /// missing required query parameter instead of an unbindable body. It is reported the same
    /// way — named, never skipped.</para>
    /// </summary>
    [Fact]
    public async Task Every_ReadEndpoint_IsSuperadminGated_UnlessExplicitlyAllowed()
    {
        int userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            userId = await userRepo.CreateAsync(new User
            {
                Username = "guardrail-regular-reader",
                DisplayName = "Guardrail Regular Reader",
                Role = UserRoles.User,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
        }

        using var client = _factory.Server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        client.DefaultRequestHeaders.Add("X-User-Role", UserRoles.User);
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());

        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var failures = new List<string>();

        foreach (var endpoint in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = endpoint.RoutePattern.RawText;
            if (pattern == null || !(pattern.StartsWith("/api/") || pattern.StartsWith("/node/")))
                continue;

            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (methods?.Contains("GET") != true)
                continue;

            var key = $"GET {pattern}";
            if (UserAllowedReads.Contains(key) || NonRoleGatedReads.Contains(key))
                continue;
            if (PublicRoutes.Contains(pattern) || SyncTokenRoutes.Contains(pattern))
                continue;

            if (endpoint.Metadata.GetMetadata<RequiresSuperadmin>() is null)
            {
                failures.Add($"{key} has no .RequireSuperadmin() on it. A new read endpoint must " +
                             "declare its role requirement: add .RequireSuperadmin() at the " +
                             "registration site, or add the route to UserAllowedReads (or " +
                             "NonRoleGatedReads) with a comment saying why a regular user may " +
                             "read it.");
            }

            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(BuildReadProbeUrl(pattern));
            }
            catch (Exception ex)
            {
                failures.Add($"{key} threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Forbidden)
                continue;

            var reason = response.StatusCode switch
            {
                HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed =>
                    "the request never reached the handler — fix the URL this test builds for it",
                HttpStatusCode.BadRequest =>
                    "the request failed model binding, which happens BEFORE endpoint filters run — " +
                    "give the route its required query parameters in BuildReadProbeUrl so the gate " +
                    "is actually exercised",
                _ => "a caller on the 'user' role must not get past the superadmin gate",
            };
            failures.Add($"{key} returned {(int)response.StatusCode} {response.StatusCode} for a " +
                         $"caller on the '{UserRoles.User}' role, expected 403 — {reason}.");
        }

        // The reason string carries the whole list on purpose: BeEmpty renders only the FIRST item
        // of the collection, and one run of this test routinely turns up several routes at once.
        failures.Should().BeEmpty(
            "every read endpoint must decide, at its registration site, whether a regular user may " +
            "call it. Gate it with .RequireSuperadmin(), or list it in UserAllowedReads. All " +
            $"{failures.Count} finding(s):{Environment.NewLine}" +
            string.Join(Environment.NewLine, failures.Select(f => "  - " + f)));
    }

    /// <summary>
    /// GET counterpart of <see cref="BuildProbe"/>. A GET binds route values and the query string,
    /// so the only way to trip binding is a required non-nullable <c>[FromQuery]</c> parameter —
    /// those routes are spelled out here so the superadmin filter, not the binder, is what answers.
    /// </summary>
    private static string BuildReadProbeUrl(string pattern)
    {
        var url = SubstituteRouteParams(pattern);
        return pattern switch
        {
            "/api/folders/download" or "/api/access/folder-permissions" => url + "?path=/",
            "/api/concept-tags/graph/neighbors" => url + "?tag=probe",
            _ => url,
        };
    }

    /// <summary>
    /// Builds a request that gets past minimal-API parameter binding, so the superadmin filter is
    /// the thing that answers. A bare <c>{}</c> deserializes into every request DTO in the API
    /// (nothing is [Required] at the binding layer); the handful of routes that bind something
    /// other than a JSON object are spelled out.
    /// </summary>
    private static HttpRequestMessage BuildProbe(string method, string pattern)
    {
        var url = SubstituteRouteParams(pattern);

        // [FromQuery] string eventId — a missing non-nullable query parameter is a 400.
        if (pattern == "/api/snapshots/restore/cancel")
            url += "?eventId=" + Guid.Empty;

        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method == "DELETE")
            return request;

        // [FromBody] bool, not a DTO — an object body will not bind.
        request.Content = pattern == "/api/sync/invisible"
            ? JsonContent.Create(false)
            : JsonContent.Create(new { });
        return request;
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

    private static object BuildMinimalBody(string pattern)
    {
        if (pattern.Contains("search"))
            return new { q = "test" };
        if (pattern.Contains("snapshots") && pattern.Contains("restore"))
            return new { fileName = "test" };
        if (pattern.Contains("snapshots"))
            return new { };
        if (pattern.Contains("keys") && pattern.Contains("change-password"))
            return new { currentPassword = "x", newPassword = "y" };
        if (pattern.Contains("keys") && pattern.Contains("add-recovery"))
            return new { password = "x" };
        if (pattern.Contains("change-password"))
            return new { currentPassword = "x", newPassword = "y" };
        if (pattern.Contains("folder") || pattern.Contains("move"))
            return new { path = "/test", destinationPath = "/test2" };
        if (pattern.Contains("articles"))
            return new { title = "t", treePath = "/", content = "" };
        if (pattern.Contains("comment"))
            return new { articleId = Guid.Empty, text = "t" };
        if (pattern.Contains("agent"))
            return new { name = "t" };
        if (pattern.Contains("user"))
            return new { username = "t", password = "t", displayName = "t" };
        return new { };
    }

    private static bool IsMethod(RouteEndpoint endpoint, string method)
    {
        return endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true;
    }
}
