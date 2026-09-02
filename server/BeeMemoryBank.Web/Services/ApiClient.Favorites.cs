using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Web.Models;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    // Write methods return the upstream status code rather than a bare bool: a 404 (article gone
    // or outside the caller's folder scope) and a 400 (bad direction) are answers the browser
    // should see as such. Collapsing them into 502 would blame the server for a client-side race.
    // A transport failure — API restarting, socket refused — is the one case that really is 502.

    // ─── Favorites ────────────────────────────────────────────────────────────

    public async Task<FavoriteListDto?> GetFavoritesAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<FavoriteListDto>("/api/favorites", JsonOpts);
        }
        catch
        {
            // The sidebar renders on every page; a favorites hiccup must never take the tree
            // down with it. Null tells the caller "unknown", and the block simply stays hidden.
            return null;
        }
    }

    public Task<(bool Ok, int Status)> AddFavoriteAsync(Guid articleId) =>
        SendFavoriteAsync(HttpMethod.Post, $"/api/favorites/{articleId}");

    public Task<(bool Ok, int Status)> RemoveFavoriteAsync(Guid articleId) =>
        SendFavoriteAsync(HttpMethod.Delete, $"/api/favorites/{articleId}");

    public Task<(bool Ok, int Status)> MoveFavoriteAsync(Guid articleId, string direction) =>
        SendFavoriteAsync(HttpMethod.Post, $"/api/favorites/{articleId}/move", new { direction });

    public Task<(bool Ok, int Status)> ResetFavoriteOrderAsync() =>
        SendFavoriteAsync(HttpMethod.Post, "/api/favorites/reset-order");

    private async Task<(bool Ok, int Status)> SendFavoriteAsync(HttpMethod method, string url, object? body = null)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (body != null) request.Content = Body(body);
            var resp = await http.SendAsync(request);
            return (resp.IsSuccessStatusCode, (int)resp.StatusCode);
        }
        catch
        {
            return (false, (int)HttpStatusCode.BadGateway);
        }
    }
}
