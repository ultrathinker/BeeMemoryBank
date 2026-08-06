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
                // Protected article: never fetch the body server-side. The page renders a lock card
                // and the body is fetched only after the user enters the passphrase (stateless —
                // re-locks on every reload/navigation).
                Content = null;
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
