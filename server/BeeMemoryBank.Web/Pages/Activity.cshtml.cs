using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages;

[Authorize]
public class ActivityModel(ApiClient api) : PageModel
{
    private const int PageSize = 50;

    public List<ActivityItemDto> Items { get; private set; } = [];
    public int CurrentPage { get; private set; }
    public int TotalPages { get; private set; }
    public int Total { get; private set; }

    public async Task OnGetAsync([Microsoft.AspNetCore.Mvc.FromQuery] int page = 1)
    {
        CurrentPage = Math.Max(1, page);
        var offset = (CurrentPage - 1) * PageSize;
        var result = await api.GetActivityAsync(PageSize, offset);
        if (result != null)
        {
            Items = result.Items;
            Total = result.Total;
            TotalPages = (int)Math.Ceiling(Total / (double)PageSize);
        }
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
