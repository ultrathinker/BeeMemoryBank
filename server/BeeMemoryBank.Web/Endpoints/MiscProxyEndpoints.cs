using System.Text;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Endpoints;

public static class MiscProxyEndpoints
{
    public static void MapMiscProxyEndpoints(this WebApplication app)
    {
        // Remote Accounts proxy ────────────────────────────────────────────────
        app.MapGet("/api-proxy/remote-accounts", async (ApiClient api) =>
        {
            var list = await api.ListRemoteAccountsAsync();
            return list != null ? Results.Ok(list) : Results.StatusCode(502);
        }).RequireAuthorization();

        app.MapPost("/api-proxy/remote-accounts", async (HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<CreateRemoteAccountProxyRequest>();
            if (req == null || string.IsNullOrWhiteSpace(req.BaseUrl) || string.IsNullOrWhiteSpace(req.Username))
                return Results.BadRequest(new { error = "displayName, baseUrl, username, password required" });
            var (ok, status, body, error) = await api.CreateRemoteAccountAsync(req.DisplayName, req.BaseUrl, req.Username, req.Password);
            if (ok) return Results.Ok(body);
            return Results.Json(new { error = error ?? "Failed" }, statusCode: status);
        }).RequireAuthorization();

        app.MapDelete("/api-proxy/remote-accounts/{id:guid}", async (Guid id, ApiClient api) =>
        {
            await api.DeleteRemoteAccountAsync(id);
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapGet("/api-proxy/remote-accounts/{id:guid}/accessible", async (Guid id, ApiClient api) =>
        {
            var (ok, status, body, error) = await api.ListAccessibleRemoteFoldersAsync(id);
            if (ok) return Results.Ok(body);
            return Results.Json(new { error = error ?? "Failed" }, statusCode: status);
        }).RequireAuthorization();

        app.MapGet("/api-proxy/remote-accounts/{id:guid}/subscriptions", async (Guid id, ApiClient api) =>
        {
            var list = await api.ListRemoteSubscriptionsAsync(id);
            return list != null ? Results.Ok(list) : Results.StatusCode(502);
        }).RequireAuthorization();

        app.MapPost("/api-proxy/remote-accounts/subscriptions", async (HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<AddRemoteSubscriptionProxyRequest>();
            if (req == null || string.IsNullOrWhiteSpace(req.MountPath))
                return Results.BadRequest(new { error = "mountPath required" });
            var (ok, status, body, error) = await api.AddRemoteSubscriptionAsync(req.RemoteAccountId, req.RemoteFolderId, req.RemoteFolderPath, req.MountPath);
            if (ok) return Results.Ok(body);
            return Results.Json(new { error = error ?? "Failed" }, statusCode: status);
        }).RequireAuthorization();

        app.MapDelete("/api-proxy/remote-accounts/subscriptions/{id:guid}", async (Guid id, ApiClient api) =>
        {
            await api.DeleteRemoteSubscriptionAsync(id);
            return Results.NoContent();
        }).RequireAuthorization();

        // POST /api-proxy/init/reset — INTENTIONALLY ANONYMOUS (no RequireAuthorization).
        // Purpose: lockout / forgotten-password recovery. When an admin is locked out of the
        // vault (forgotten master password) they cannot authenticate, so this route must be
        // reachable from the locked /Login screen to wipe-and-rejoin the node.
        // The REAL security control is API-side: POST /api/init/reset requires the master
        // password in the body (SessionService.UnlockAsync) and refuses if it is wrong. The
        // Web layer adds nothing here — it only forwards the master password. Keeping this
        // route anonymous is deliberate; do not add RequireAuthorization without breaking
        // the only recovery path for a forgotten master password.
        app.MapPost("/api-proxy/init/reset", async (HttpContext ctx, ApiClient api) =>
        {
            var req = await ctx.Request.ReadFromJsonAsync<ResetProxyRequest>();
            if (req == null || string.IsNullOrWhiteSpace(req.MasterPassword))
                return Results.BadRequest(new { error = "masterPassword required" });
            var (ok, err) = await api.ResetNodeAsync(req.MasterPassword);
            return ok ? Results.Ok() : Results.BadRequest(err);
        });

        app.MapGet("/api-proxy/maintenance", async (ApiClient api) =>
        {
            var unlocked = await api.IsUnlockedAsync();
            return Results.Ok(new { isUnlocked = unlocked });
        }).RequireAuthorization();

        // Backfill Orphan Media Links proxy — disabled. Auto-link on save handles new uploads.
        // Uncomment together with the UI in Admin.cshtml and the API endpoint if ever needed.
        // app.MapPost("/api-proxy/admin/backfill-media-links", async (ApiClient api) =>
        // {
        //     var (ok, body, status) = await api.PostRawAsync("admin/backfill-media-links", "");
        //     return Results.Content(body ?? "", "application/json", null, status);
        // }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        // ─── W1 catch-all forwarder (registered LAST so explicit routes win) ──────────
        // Routes /api-proxy/{**path} requests that are NOT matched by an explicit hand-written route
        // through the deny-by-default ProxyRouteTable. Identity headers (X-Internal-Key / X-User-*) are
        // injected automatically by InternalKeyHandler, so the forwarder only expresses ROLE gating and
        // STREAMING. Pilot: only the concept-tags GET family is in the table. Unknown prefix → 404.
        app.MapMethods("/api-proxy/{**path}", new[] { "GET", "POST", "PUT", "DELETE", "PATCH" },
            async (string path, HttpContext ctx, ApiClient api) =>
        {
            var entry = ProxyRouteTable.Match(path, out var matchedPrefix);
            if (entry is null)
                return Results.NotFound(); // deny-by-default: unknown prefix → 404, never a blind forward

            // Role gate — preserves the per-path RequireAuthorization("superadmin") semantics that a
            // single catch-all registration cannot otherwise attach per route.
            if (entry.RequiredRole == "superadmin" && !ctx.User.IsInRole("superadmin"))
                return Results.Json(new { error = "Forbidden — superadmin only" }, statusCode: 403);

            var upstreamPath = ProxyRouteTable.BuildUpstreamPath(path, matchedPrefix, entry)
                                + ctx.Request.QueryString.Value;
            var method = new HttpMethod(ctx.Request.Method);
            var upstreamReq = new HttpRequestMessage(method, upstreamPath);

            // Forward a body when present (POST/PUT/PATCH). The pilot is GET-only; the general path is
            // kept so the table can grow into mutation routes in a supervised follow-up.
            if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                upstreamReq.Content = new StreamContent(ctx.Request.Body);
                if (!string.IsNullOrEmpty(ctx.Request.ContentType))
                    upstreamReq.Content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue(ctx.Request.ContentType);
            }

            HttpResponseMessage upstream;
            try
            {
                upstream = await api.SendForwardAsync(upstreamReq);
            }
            catch
            {
                return Results.StatusCode(502); // API unreachable
            }

            using (upstream)
            {
                // Status + content-type + body passthrough — this is the W2 fix, for free.
                var body = await upstream.Content.ReadAsStringAsync();
                var contentType = upstream.Content.Headers.ContentType?.MediaType ?? "application/json";
                return Results.Content(body, contentType, Encoding.UTF8, (int)upstream.StatusCode);
            }
        }).RequireAuthorization();
    }
}
