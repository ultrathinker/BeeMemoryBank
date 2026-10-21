using System.Text.Json;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace BeeMemoryBank.Web.Pages.Admin;

public class TagGraphModel(ApiClient api) : AdminPageModel(api)
{
    public JsonElement? GraphEdgeStats { get; private set; }

    /// <summary>The report of the last "Verify and rebuild if needed" (JSON), shown once, next to the button that asked for it.</summary>
    public string? GraphRebuildReport { get; private set; }

    public async Task OnGetAsync()
    {
        GraphRebuildReport = TempData["GraphRebuildReport"] as string;
        GraphEdgeStats = await Api.GetConceptTagEdgeStatsAsync();
    }

    public async Task<IActionResult> OnPostRebuildGraphAsync()
    {
        var report = await Api.RebuildConceptTagEdgesAsync();
        TempData["GraphRebuildReport"] = report?.ToString();
        return Back();
    }
}
