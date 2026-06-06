using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages.Article;

[Authorize]
public class EditModel(ApiClient api) : PageModel
{
    public Guid? ArticleId { get; private set; }
    public string TreePath { get; set; } = "/";
    public string Title { get; set; } = "";
    public new string Content { get; set; } = "";
    public string ConceptTagsRaw { get; set; } = "";
    public DateTime? LastModified { get; private set; }
    public bool IsNew => ArticleId == null;
    public bool IsProtected { get; private set; }
    // Protected AND not unlockable from the recent-unlock cache → show the passphrase gate.
    public bool IsLocked { get; private set; }
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid? id, string? treePath)
    {
        if (id.HasValue)
        {
            var article = await api.GetArticleAsync(id.Value);
            if (article != null)
            {
                ArticleId = article.Id;
                TreePath = article.TreePath;
                Title = article.Title;
                LastModified = article.UpdatedAt;
                IsProtected = article.Protected;

                // edit-content returns plaintext for a non-protected article OR for a protected one
                // that was unlocked in the last ~60s by this user (server-side cache → no re-prompt).
                var ec = await api.GetEditContentAsync(id.Value);
                if (ec is { Unlocked: true })
                    Content = ec.Content ?? "";
                else if (!article.Protected)
                {
                    // Non-protected but the helper failed for some reason — fall back to the normal path.
                    var c = await api.GetArticleContentAsync(id.Value);
                    Content = c?.Content ?? "";
                }
                // Protected + not unlocked → leave Content empty and show the passphrase gate.
                IsLocked = article.Protected && (ec is null || !ec.Unlocked);

                // Read-only ACL: forward to View page with a one-time flash.
                var perms = await api.GetFolderPermissionsAsync(article.TreePath);
                if (perms != null && perms.IsReadOnly)
                {
                    TempData["FlashMessage"] = "This article is in a read-only folder for your user.";
                    return Redirect($"/Article/View?id={id.Value}");
                }
            }
            var ct = await api.GetArticleConceptTagsAsync(id.Value);
            ConceptTagsRaw = ct != null ? string.Join(", ", ct) : "";
        }
        else
        {
            TreePath = treePath ?? "/";
            // Block creating a new article inside a read-only folder.
            var perms = await api.GetFolderPermissionsAsync(TreePath);
            if (perms != null && perms.IsReadOnly)
            {
                TempData["FlashMessage"] = $"Folder {TreePath} is read-only for your user.";
                return Redirect($"/Folder?path={Uri.EscapeDataString(TreePath)}");
            }
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        Guid? id, string? treePath, string title, string? content, string? conceptTags,
        string? passphrase = null, string? hint = null)
    {
        var body = content ?? "";

        ArticleId = id;
        TreePath = treePath ?? "/";
        Title = title ?? "";
        Content = body;
        ConceptTagsRaw = conceptTags ?? "";

        var ctList = string.IsNullOrWhiteSpace(conceptTags)
            ? new List<string>()
            : conceptTags.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

        if (id.HasValue)
        {
            var (updated, status, error) = await api.UpdateArticleWithErrorAsync(
                id.Value, title, treePath, body);
            if (updated != null)
            {
                await api.SetArticleConceptTagsAsync(id.Value, ctList);
                return Redirect($"/Article/View?id={id.Value}");
            }
            ErrorMessage = FriendlyError(status, error, "save");
            return Page();
        }
        else
        {
            var (article, status, error) = await api.CreateArticleWithErrorAsync(
                title, treePath ?? "/", body,
                string.IsNullOrWhiteSpace(passphrase) ? null : passphrase,
                string.IsNullOrWhiteSpace(hint) ? null : hint);
            if (article != null)
            {
                await api.SetArticleConceptTagsAsync(article.Id, ctList);
                return Redirect($"/Article/View?id={article.Id}");
            }
            ErrorMessage = FriendlyError(status, error, "create");
            return Page();
        }
    }

    private static string FriendlyError(int status, string? error, string verb)
    {
        if (status == 403)
            return error ?? $"You don't have permission to {verb} this article.";
        if (status == 401)
            return "Your session has expired. Please log in again.";
        if (!string.IsNullOrWhiteSpace(error)) return error!;
        return $"Failed to {verb} article (HTTP {status}).";
    }
}
