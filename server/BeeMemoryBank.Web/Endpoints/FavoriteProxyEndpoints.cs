using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Endpoints;

/// <summary>
/// The favorites LIST route survived the catch-all migration: it degrades an API failure to an
/// empty list instead of an error, so the sidebar block just stays hidden while the tree below it
/// still renders. The mutations (add / remove / move / reset-order) were pure passthroughs and
/// moved into ProxyRouteTable ("favorites") — the forwarder in MiscProxyEndpoints serves them,
/// and unlike these old handlers it passes the API's error bodies through so the UI can show why.
/// </summary>
public static class FavoriteProxyEndpoints
{
    public static void MapFavoriteProxyEndpoints(this WebApplication app)
    {
        app.MapGet("/api-proxy/favorites", async (ApiClient api) =>
        {
            var list = await api.GetFavoritesAsync();
            // Unknown (API hiccup) degrades to "no favorites" rather than an error banner:
            // the sidebar block simply stays hidden and the tree below it still renders.
            return Results.Ok(list ?? new FavoriteListDto([], false));
        }).RequireAuthorization();
    }
}
