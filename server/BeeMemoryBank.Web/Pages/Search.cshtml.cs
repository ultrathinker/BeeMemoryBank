using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

[Authorize]
public class SearchModel(ApiClient api) : PageModel
{
    private static readonly HashSet<string> ValidModes = new(StringComparer.OrdinalIgnoreCase) { "hybrid", "keyword", "semantic" };

    public string Query { get; private set; } = "";
    public string Mode { get; private set; } = "hybrid";
    public SearchResponseDto? Results { get; private set; }

    public async Task OnGetAsync(string? q, string? mode = "hybrid")
    {
        Mode = mode is not null && ValidModes.Contains(mode) ? mode : "hybrid";

        if (string.IsNullOrWhiteSpace(q))
        {
            return;
        }

        Query = q;

        // Article body content is always searched now -- the mode selector controls HOW (exact
        // match, meaning, or both via RRF), not WHETHER. This search is fast enough (BM25 + the
        // in-memory chunk cache) that there is no longer a reason to make it opt-in the way the
        // old linear body scan was.
        var folderResults = await api.SearchAsync(q, content: false);
        var articles = await api.SearchHybridArticlesAsync(q, mode: Mode);

        if (articles is null)
        {
            // Hybrid/keyword/semantic search unavailable for this vault or session (e.g. locked,
            // or semantic search was never initialized on this node) -- fall back to the old
            // linear content scan rather than surfacing a broken page.
            Results = await api.SearchAsync(q, content: true);
            return;
        }

        var merged = new List<ArticleDto>(articles);
        var seenIds = new HashSet<Guid>(merged.Select(a => a.Id));
        foreach (var a in folderResults?.Articles ?? [])
        {
            if (seenIds.Add(a.Id))
            {
                merged.Add(a);
            }
        }

        Results = new SearchResponseDto(folderResults?.Folders ?? [], merged);
    }
}
