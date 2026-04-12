using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages.Article;

[Authorize]
public class HistoryModel(ApiClient api) : PageModel
{
    public ArticleDto? Article { get; private set; }
    public List<ActivityItemDto> Events { get; private set; } = [];
    public List<ArticleVersionDto> Versions { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Article = await api.GetArticleAsync(id);
        if (Article == null)
            return RedirectToPage("/Tree");

        var result = await api.GetActivityByArticleAsync(id, 100);
        Events = result?.Items ?? [];
        Versions = await api.GetArticleVersionsAsync(id) ?? [];
        return Page();
    }

    public static string GetEventVariant(string eventType) => eventType switch
    {
        "article_created" => "success",
        "article_updated" => "primary",
        "article_deleted" => "danger",
        "article_moved"   => "warning",
        _ => "neutral"
    };
}
