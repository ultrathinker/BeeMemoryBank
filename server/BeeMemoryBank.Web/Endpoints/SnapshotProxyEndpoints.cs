using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace BeeMemoryBank.Web.Endpoints;

/// <summary>
/// The two proxy routes here stay explicit because they carry Web-side logic the table cannot
/// express. Snapshots, compact, activity list, comments, session status/settings reads,
/// embeddings admin and sync status are ProxyRouteTable entries served by the forwarder in
/// MiscProxyEndpoints.
/// </summary>
public static class SnapshotProxyEndpoints
{
    public static void MapSnapshotProxyEndpoints(this WebApplication app)
    {
        // Per-article activity: the proxy URL has the id as a PATH segment, the API only knows it
        // as a QUERY parameter (/api/activity?articleId=...) — a URL remap, not a prefix forward.
        app.MapGet("/api-proxy/activity/article/{articleId:guid}", async (Guid articleId, ApiClient api, int limit = 50) =>
        {
            var result = await api.GetActivityByArticleAsync(articleId, limit);
            return result != null ? Results.Ok(result) : Results.StatusCode(502);
        }).RequireAuthorization();

        // Session settings write: applies the new values to the LIVE WebSessionSettingsService
        // singleton and drops the cached cookie options — without this a save needs a process
        // restart before the new expiry takes effect (the lazy-load middleware runs once).
        app.MapPut("/api-proxy/session/settings", async (
            HttpContext ctx, ApiClient api,
            WebSessionSettingsService liveSettings,
            IOptionsMonitorCache<CookieAuthenticationOptions> optionsCache) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync<SessionSettingsDto>();
            if (body == null) return Results.BadRequest();

            var (ok, error) = await api.SetSessionSettingsAsync(body.ExpireHours, body.SlidingExpiration);
            if (!ok) return Results.Json(new { error }, statusCode: 400);

            liveSettings.ExpireHours = body.ExpireHours;
            liveSettings.SlidingExpiration = body.SlidingExpiration;
            liveSettings.Loaded = true;
            optionsCache.TryRemove("BeeWebCookie");

            return Results.Ok(new { expireHours = body.ExpireHours, slidingExpiration = body.SlidingExpiration });
        }).RequireAuthorization(policy => policy.RequireRole(UserRoles.Superadmin));
    }
}
