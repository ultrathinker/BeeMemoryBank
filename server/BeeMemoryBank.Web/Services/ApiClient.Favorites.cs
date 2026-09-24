using System.Net.Http.Json;
using BeeMemoryBank.Web.Models;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
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
}
