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
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(Guid? id, string? treePath)
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
                var c = await api.GetArticleContentAsync(id.Value);
                Content = c?.Content ?? "";
            }
            var ct = await api.GetArticleConceptTagsAsync(id.Value);
            ConceptTagsRaw = ct != null ? string.Join(", ", ct) : "";
        }
        else
        {
            TreePath = treePath ?? "/";
        }
    }

    public async Task<IActionResult> OnPostAsync(
        Guid? id, string? treePath, string title, string? content, string? conceptTags)
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
                title, treePath ?? "/", body);
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
