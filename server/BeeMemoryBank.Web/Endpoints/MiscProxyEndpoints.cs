using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Endpoints;

public static class MiscProxyEndpoints
{
    public static void MapMiscProxyEndpoints(this WebApplication app)
    {
        // ── Branding (read: any signed-in user; write: superadmin) ──

        app.MapGet("/api-proxy/branding", async (ApiClient api) =>
        {
            var branding = await api.GetBrandingAsync();
            return Results.Ok(branding ?? new BrandingDto(Branding.DefaultName, false, Branding.DefaultName));
        }).RequireAuthorization();

        app.MapPut("/api-proxy/branding", async (HttpContext ctx, ApiClient api, BrandingService branding) =>
        {
            var req = await ReadBrandingJsonAsync<BrandingProxyRequest>(ctx);
            var (ok, status, error, result) = await api.SetBrandingAsync(req?.Name);
            if (!ok) return Results.Json(new { error }, statusCode: status);

            // Push the new value into the header cache immediately — without this the admin saves
            // and then stares at the old name until the TTL expires, and reports it as a bug.
            if (result != null) branding.Set(result.Name);
            return Results.Ok(result);
        }).RequireAuthorization(policy => policy.RequireRole(UserRoles.Superadmin));

        // Remote accounts and /maintenance are table entries now — see ProxyRouteTable
        // ("remote-accounts", "maintenance"); the API surface they forward to is unchanged.

        // Backfill Orphan Media Links proxy — disabled. Auto-link on save handles new uploads.
        // Uncomment together with the UI in Admin.cshtml and the API endpoint if ever needed.
        // app.MapPost("/api-proxy/admin/backfill-media-links", async (ApiClient api) =>
        // {
        //     var (ok, body, status) = await api.PostRawAsync("admin/backfill-media-links", "");
        //     return Results.Content(body ?? "", "application/json", null, status);
        // }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        // ─── Catch-all forwarder (registered LAST so explicit routes win) ─────────────
        // Serves /api-proxy/{**path} requests that no explicit hand-written route matched, using
        // the deny-by-default ProxyRouteTable: unknown prefix → 404, prefix listed but method not
        // → 405, listed but the caller lacks the entry's role → 403. Everything else is forwarded
        // through ApiClient (InternalKeyHandler injects X-Internal-Key / X-User-* identity
        // headers) as a verbatim stream pass-through: status, body, and a small allow-list of
        // response headers. Hop-by-hop and Set-Cookie are never copied.
        app.MapMethods("/api-proxy/{**path}", new[] { "GET", "POST", "PUT", "DELETE", "PATCH" },
            async (string path, HttpContext ctx, ApiClient api) =>
        {
            var (outcome, entry, rule, matchedPrefix) = ProxyRouteTable.Match(path, ctx.Request.Method);
            switch (outcome)
            {
                // Deny-by-default: an unknown prefix must never become a blind forward.
                case ProxyRouteTable.MatchOutcome.UnknownPrefix:
                    return Results.NotFound();

                case ProxyRouteTable.MatchOutcome.MethodNotAllowed:
                    // Advertise what the entry does accept, like a real 405 should.
                    ctx.Response.Headers.Allow = entry!.AllowHeader;
                    return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
            }

            // Role gate — the per-route RequireRole("superadmin") semantics that a single
            // catch-all registration cannot attach per method. The API enforces roles too; this
            // is the same Web-layer gate the hand-written routes carried (defense in depth, and
            // it keeps the round-trip local for the obvious case).
            if (rule!.RequiredRole != null && !ctx.User.IsInRole(rule.RequiredRole))
                return Results.Json(new { error = "Forbidden — superadmin only" }, statusCode: 403);

            var upstreamPath = ProxyRouteTable.BuildUpstreamPath(path, matchedPrefix!, entry!)
                                + ctx.Request.QueryString.Value;
            using var upstreamReq = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), upstreamPath);

            // Stream the request body through — binary-safe, no buffering, no string round-trip.
            // Multipart (media/import uploads) works because the raw bytes AND the Content-Type
            // (with its boundary) are forwarded untouched.
            if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                upstreamReq.Content = new StreamContent(ctx.Request.Body);
                // Raw copy, deliberately NOT via MediaTypeHeaderValue: the constructor rejects
                // parameters (a multipart boundary), which would 500 every browser upload.
                if (!string.IsNullOrEmpty(ctx.Request.ContentType))
                    upstreamReq.Content.Headers.TryAddWithoutValidation(
                        "Content-Type", ctx.Request.ContentType);
                // Preserve the declared length: Request.Body is non-seekable, so StreamContent
                // alone would fall back to chunked encoding for no reason.
                if (ctx.Request.ContentLength is > 0)
                    upstreamReq.Content.Headers.ContentLength = ctx.Request.ContentLength;
            }

            HttpResponseMessage upstream;
            try
            {
                upstream = await api.SendForwardAsync(upstreamReq);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                return Results.Json(new { error = "Upstream API is unavailable" }, statusCode: 502);
            }

            using (upstream)
            {
                var response = ctx.Response;
                response.StatusCode = (int)upstream.StatusCode;

                // Untrusted-user-content headers (CSP sandbox + nosniff) for entries that serve
                // bytes a browser can open directly as a document. Must happen before the first
                // body write, and it deliberately REPLACES the site CSP the global security
                // middleware stamped on the response.
                if (entry!.Flags.HasFlag(ProxyRouteFlags.UserContent))
                    BeeMemoryBank.Hosting.AspNetCore.UserContentResponseHeaders.ApplyTo(response);

                // Response headers: a small allow-list, never a blind copy. Hop-by-hop headers
                // (Connection, Transfer-Encoding, ...) are managed by Kestrel; Set-Cookie must
                // never cross from the API's session world into the browser. Content-Type is set
                // below via the content stream. Content-Length is recomputed by the framework
                // when the length is knowable; copying the upstream value is safe for byte-exact
                // bodies and skipped for chunked ones. Content headers travel on
                // upstream.Content.Headers (Content-Disposition, Last-Modified), the rest on
                // upstream.Headers — both collections are filtered.
                if (entry!.Flags.HasFlag(ProxyRouteFlags.StripContentDisposition))
                    upstream.Content.Headers.ContentDisposition = null;

                foreach (var header in upstream.Headers.Concat(upstream.Content.Headers))
                {
                    if (!CopyableResponseHeaders.Contains(header.Key)) continue;
                    response.Headers[header.Key] = header.Value.ToArray();
                }

                var upstreamContent = upstream.Content;
                if (upstreamContent.Headers.ContentLength is { } length && upstream.Headers.TransferEncoding.Count == 0)
                    response.ContentLength = length;

                response.ContentType = upstreamContent.Headers.ContentType?.ToString() ?? "application/octet-stream";
                await upstreamContent.CopyToAsync(response.Body, ctx.RequestAborted);
            }

            // The body and status were written directly above; this no-op result only satisfies
            // the handler's return type. (Results.Empty's ExecuteAsync writes nothing and does
            // not touch the already-set status.)
            return Results.Empty;
        }).RequireAuthorization();
    }

    /// <summary>
    /// Response headers safe to relay from the API to the browser. Anything not listed (most
    /// importantly Set-Cookie, plus Location — the API's addresses are not reachable from the
    /// browser and must not leak a same-origin-looking link) is dropped by the forwarder.
    /// </summary>
    private static readonly HashSet<string> CopyableResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Disposition",
        "ETag",
        "Cache-Control",
        "Last-Modified",
        "Accept-Ranges",
    };

    /// <summary>
    /// A malformed body is a client error, not a crash: without this a stray request would throw
    /// JsonException out of the endpoint and surface as a 500.
    /// </summary>
    private static async Task<T?> ReadBrandingJsonAsync<T>(HttpContext ctx) where T : class
    {
        try { return await ctx.Request.ReadFromJsonAsync<T>(); }
        catch { return null; }
    }

    private sealed record BrandingProxyRequest(string? Name);
}
