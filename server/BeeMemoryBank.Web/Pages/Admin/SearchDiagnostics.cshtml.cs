using System.Text.Json;
using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Pages.Admin;

/// <summary>
/// Search diagnostics (WP-18): latency / volume / index health. Shapes, counts and timings only - the API never returns query text or article content here
/// (see SearchMetricsEndpoints). The embeddings switch and its backfill button are read and saved by <c>admin.js</c>.
/// </summary>
public class SearchDiagnosticsModel(ApiClient api) : AdminPageModel(api)
{
    public JsonElement? SearchMetrics { get; private set; }

    public async Task OnGetAsync() => SearchMetrics = await Api.GetSearchMetricsAsync();
}
