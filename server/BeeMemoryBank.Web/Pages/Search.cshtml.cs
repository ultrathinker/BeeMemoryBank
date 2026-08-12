using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

[Authorize]
public class SearchModel(ApiClient api) : PageModel
{
    public string Query { get; private set; } = "";
    public bool ContentSearch { get; private set; }
    public SearchResponseDto? Results { get; private set; }

    public async Task OnGetAsync(string? q, bool content = false)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return;
        }

        Query = q;
        ContentSearch = content;

        if (!content)
        {
            Results = await api.SearchAsync(q, content: false);
            return;
        }

        // WP-16: content search now runs through hybrid (BM25 keyword + chunk-based semantic)
        // ranking by default instead of the older linear body scan. Folders still come from the
        // plain endpoint (hybrid search only ranks articles). Falls back to the pre-WP-16 content
        // search if hybrid search is unavailable for this vault (e.g. semantic search was never
        // initialized) rather than surfacing a broken page.
        var folderResults = await api.SearchAsync(q, content: false);
        var hybridArticles = await api.SearchHybridArticlesAsync(q, mode: "hybrid");

        if (hybridArticles is null)
        {
            Results = await api.SearchAsync(q, content: true);
            return;
        }

        var articles = new List<ArticleDto>(hybridArticles);
        var seenIds = new HashSet<Guid>(articles.Select(a => a.Id));
        foreach (var a in folderResults?.Articles ?? [])
        {
            if (seenIds.Add(a.Id))
            {
                articles.Add(a);
            }
        }

        Results = new SearchResponseDto(folderResults?.Folders ?? [], articles);
    }
}
