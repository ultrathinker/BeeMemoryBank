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

    // ─── Embeddings self-toggle (2026-08-12 fix) ────────────────────────────────
    // Whether THIS node generates its own embeddings (tbl_node_identity.can_generate_embeddings).
    // Previously had no toggle at all after node init -- see SearchMetricsEndpoints.cs.

    public async Task<bool?> GetEmbeddingsEnabledAsync()
    {
        try
        {
            var result = await http.GetFromJsonAsync<EmbeddingsEnabledDto>("/api/admin/search/embeddings-enabled", JsonOpts);
            return result?.Enabled;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> SetEmbeddingsEnabledAsync(bool enabled)
    {
        var resp = await http.PutAsJsonAsync("/api/admin/search/embeddings-enabled", new { enabled }, JsonOpts);
        return resp.IsSuccessStatusCode;
    }
}

public sealed record EmbeddingsEnabledDto(bool Enabled);
