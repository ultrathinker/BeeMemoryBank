using BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BeeMemoryBank.Web.Pages.Article;

[Authorize]
public class EditModel(ApiClient api) : PageModel
{
    public Guid? ArticleId { get; private set; }
    public string TreePath { get; private set; } = "/";
    public string Title { get; private set; } = "";
    public new string Content { get; private set; } = "";
    public string Tags { get; private set; } = "";
    public DateTime? LastModified { get; private set; }
    public bool IsNew => ArticleId == null;

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
                Tags = string.Join(", ", article.Tags);
                LastModified = article.UpdatedAt;
                var c = await api.GetArticleContentAsync(id.Value);
                Content = c?.Content ?? "";
            }
        }
        else
        {
            TreePath = treePath ?? "/";
        }
    }

    public async Task<IActionResult> OnPostAsync(
        Guid? id, string? treePath, string title, string? content, string? tags)
    {
        var tagList = string.IsNullOrWhiteSpace(tags)
            ? new List<string>()
            : tags.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        var body = content ?? "";

        if (id.HasValue)
        {
            await api.UpdateArticleAsync(id.Value, title, treePath, tagList, body);
            return Redirect($"/Article/View?id={id.Value}");
        }
        else
        {
            var article = await api.CreateArticleAsync(title, treePath ?? "/", tagList, body);
            if (article == null) return Page();
            return Redirect($"/Article/View?id={article.Id}");
        }
    }
}
