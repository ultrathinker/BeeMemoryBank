using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Endpoints;

public static class FavoriteProxyEndpoints
{
    public static void MapFavoriteProxyEndpoints(this WebApplication app)
    {
        // ── Favorites (per signed-in user; the API scopes everything by X-User-Id) ──

        app.MapGet("/api-proxy/favorites", async (ApiClient api) =>
        {
            var list = await api.GetFavoritesAsync();
            // Unknown (API hiccup) degrades to "no favorites" rather than an error banner:
            // the sidebar block simply stays hidden and the tree below it still renders.
            return Results.Ok(list ?? new FavoriteListDto([], false));
        }).RequireAuthorization();

        app.MapPost("/api-proxy/favorites/{id:guid}", async (Guid id, ApiClient api) =>
        {
            var (ok, status) = await api.AddFavoriteAsync(id);
            return ok ? Results.NoContent() : Results.StatusCode(status);
        }).RequireAuthorization();

        app.MapDelete("/api-proxy/favorites/{id:guid}", async (Guid id, ApiClient api) =>
        {
            var (ok, status) = await api.RemoveFavoriteAsync(id);
            return ok ? Results.NoContent() : Results.StatusCode(status);
        }).RequireAuthorization();

        app.MapPost("/api-proxy/favorites/{id:guid}/move", async (Guid id, HttpContext ctx, ApiClient api) =>
        {
            var req = await ReadJsonAsync<FavoriteMoveProxyRequest>(ctx);
            if (req == null || string.IsNullOrWhiteSpace(req.Direction))
                return Results.BadRequest(new { error = "direction is required" });

            var (ok, status) = await api.MoveFavoriteAsync(id, req.Direction);
            return ok ? Results.NoContent() : Results.StatusCode(status);
        }).RequireAuthorization();

        app.MapPost("/api-proxy/favorites/reset-order", async (ApiClient api) =>
        {
            var (ok, status) = await api.ResetFavoriteOrderAsync();
            return ok ? Results.NoContent() : Results.StatusCode(status);
        }).RequireAuthorization();
    }

    /// <summary>
    /// A malformed body is a client error, not a crash: without this a stray request would throw
    /// JsonException out of the endpoint and surface as a 500.
    /// </summary>
    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx) where T : class
    {
        try { return await ctx.Request.ReadFromJsonAsync<T>(); }
        catch { return null; }
    }

    private sealed record FavoriteMoveProxyRequest(string Direction);
}
