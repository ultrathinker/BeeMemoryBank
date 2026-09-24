using System.Text.Json;
using BeeMemoryBank.Web.Models;
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
    // Existing article: its current file attachments (uploaded straight onto the article).
    public List<MediaDto> Attachments { get; private set; } = [];
    // New article: files uploaded before the article exists, as the JSON the page posts back in
    // the hidden "pendingAttachments" field ([{id,fileName,fileSize}]). Round-tripped so a failed
    // create re-renders the same list instead of silently dropping the uploads.
    public string PendingAttachmentsJson { get; private set; } = "[]";

    private record PendingAttachment(Guid Id, string FileName, long FileSize);
    private static readonly JsonSerializerOptions PendingJsonOpts = new(JsonSerializerDefaults.Web);

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
                // that was unlocked recently by this user (server-side cache → no re-prompt).
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
            if (article is { Protected: false })
            {
                var media = await api.ListMediaAsync(id.Value) ?? [];
                Attachments = media.Where(m => m.Kind == "attachment").ToList();
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
        string? passphrase = null, string? hint = null, string? pendingAttachments = null)
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
            var pending = ParsePending(pendingAttachments);
            PendingAttachmentsJson = JsonSerializer.Serialize(pending, PendingJsonOpts);
            var (article, status, error) = await api.CreateArticleWithErrorAsync(
                title, treePath ?? "/", body,
                string.IsNullOrWhiteSpace(passphrase) ? null : passphrase,
                string.IsNullOrWhiteSpace(hint) ? null : hint,
                pending.Count > 0 ? pending.Select(p => p.Id).ToList() : null);
            if (article != null)
            {
                await api.SetArticleConceptTagsAsync(article.Id, ctList);
                return Redirect($"/Article/View?id={article.Id}");
            }
            ErrorMessage = FriendlyError(status, error, "create");
            return Page();
        }
    }

    private static List<PendingAttachment> ParsePending(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<PendingAttachment>>(json, PendingJsonOpts) ?? [])
                .Where(p => p.Id != Guid.Empty)
                .DistinctBy(p => p.Id)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
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
