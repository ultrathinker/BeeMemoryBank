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
    public List<RelatedArticleDto> RelatedArticles { get; private set; } = [];
    public List<string> ConceptTags { get; private set; } = [];
    public List<MediaDto> Attachments { get; private set; } = [];
    public bool IsReadOnly { get; private set; }
    // True when a protected article's content below came from the recent-unlock cache rather than
    // a passphrase the user just typed on this page load — lets the page open straight to the
    // unlocked view instead of the passphrase gate.
    public bool IsUnlockedFromCache { get; private set; }

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
            if (Article.Protected)
            {
                // Protected article: same cache-aware helper the Edit page uses. If this caller
                // verified the passphrase recently (within ProtectedUnlockCache.Ttl), the page opens
                // straight to the unlocked view instead of the passphrase gate. On a cache miss it
                // falls back to the gate, same as before.
                var ec = await api.GetEditContentAsync(id);
                if (ec is { Unlocked: true })
                {
                    Content = ec.Content;
                    IsUnlockedFromCache = true;
                }
                else
                {
                    Content = null;
                }
            }
            else
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
            }
            Comments = await api.GetCommentsAsync(id) ?? [];
            ConceptTags = await api.GetArticleConceptTagsAsync(id) ?? [];
            RelatedArticles = await api.GetRelatedArticlesAsync(id) ?? [];
            if (!Article.Protected)
            {
                var media = await api.ListMediaAsync(id) ?? [];
                Attachments = media.Where(m => m.Kind == "attachment").ToList();
            }

            var perms = await api.GetFolderPermissionsAsync(Article.TreePath);
            IsReadOnly = perms?.IsReadOnly == true;
        }
        return Page();
    }

    public static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return unitIndex == 0 ? $"{size:0} {units[unitIndex]}" : $"{size:0.#} {units[unitIndex]}";
    }
}
