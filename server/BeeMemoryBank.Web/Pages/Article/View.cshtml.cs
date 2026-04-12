using BeeMemoryBank.Web.Models;
using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages.Article;

[Authorize]
public class ViewModel(ApiClient api) : PageModel
{
    public ArticleDto? Article { get; private set; }
    public new string? Content { get; private set; }
    public List<CommentDto> Comments { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var isUnlocked = await api.IsUnlockedAsync();
        if (!isUnlocked)
        {
            // API session expired — sign out and redirect to Login
            await HttpContext.SignOutAsync("BeeWebCookie");
            return RedirectToPage("/Login", new { returnUrl = $"/Article/View?id={id}" });
        }

        Article = await api.GetArticleAsync(id);
        if (Article != null)
        {
            try
            {
                var c = await api.GetArticleContentAsync(id);
                Content = c?.Content;
            }
            catch
            {
                Content = null; // decryption failed (article from node with different DEK)
            }
            Comments = await api.GetCommentsAsync(id) ?? [];
        }
        return Page();
    }
}
