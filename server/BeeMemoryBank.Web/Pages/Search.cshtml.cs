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

    /// <summary>
    /// True when hybrid/keyword/semantic search was unavailable for this request and the page fell
    /// back to the old linear content scan instead. The view uses this to disclose the fallback
    /// rather than silently showing results under a mode label that isn't actually what ran.
    /// </summary>
    public bool IsFallback { get; private set; }

    public SearchResponseDto? Results { get; private set; }

    public async Task OnGetAsync(string? q, string? mode = "hybrid")
    {
        // Normalize case: ValidModes matches case-insensitively (so a hand-edited URL like
        // ?mode=KEYWORD is still accepted), but Mode's value is later used as a dictionary key and
        // an <sl-option> value match in the view, both of which are case-sensitive against the
        // lowercase "hybrid"/"keyword"/"semantic" set — an unnormalized "KEYWORD" would silently
        // mismatch both and the page would show the wrong mode label/selection.
        Mode = mode is not null && ValidModes.Contains(mode) ? mode.ToLowerInvariant() : "hybrid";

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
            // linear content scan rather than surfacing a broken page. IsFallback lets the view
            // disclose this: without it, a user who picked "meaning only" and got zero results
            // would have no way to tell "no conceptual matches exist" apart from "semantic search
            // never actually ran for this request" -- silently swapping in a plain substring scan
            // under a mode label that no longer describes what happened is misleading.
            IsFallback = true;
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
