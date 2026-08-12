using System.Text.Json;

namespace BeeMemoryBank.Web.Services;

public partial class ApiClient
{
    // ─── Search metrics (WP-18) ─────────────────────────────────────────────────
    // Fetched by the Admin page's "Search" diagnostics section. Returns null on any transport
    // error / non-2xx (locked session, non-superadmin, API down) so the page degrades to a quiet
    // "unavailable" instead of a 500. The body is shapes/counts/timings only -- never query text
    // or content (enforced on the API side, see SearchMetricsEndpoints).

    public async Task<JsonElement?> GetSearchMetricsAsync()
    {
        try
        {
            return await http.GetFromJsonAsync<JsonElement>("/api/admin/search/metrics", JsonOpts);
        }
        catch
        {
            return null;
        }
    }
}
