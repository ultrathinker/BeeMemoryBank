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
        if (!string.IsNullOrWhiteSpace(q))
        {
            Query = q;
            ContentSearch = content;
            Results = await api.SearchAsync(q, content: content);
        }
    }
}
