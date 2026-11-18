using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

/// <summary>
/// The tree list both tree screens show: the folders and articles directly in a path, grouped
/// the way the mock-up groups them ("Folders (N)", "Articles (N)"). The folder meta is the count
/// of items directly inside; the article meta is the date and a plain preview of the body (a
/// protected article says it is protected instead of showing the ciphertext).
/// </summary>
internal static class TreeContent
{
    public static async Task<List<TreeGroup>> BuildGroupsAsync(IServiceProvider services, string currentPath)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var folderRepo = sp.GetRequiredService<IFolderRepository>();
        var articleRepo = sp.GetRequiredService<IArticleRepository>();
        var articleSvc = sp.GetRequiredService<ArticleService>();

        var folders = await folderRepo.GetChildrenAsync(currentPath == "/" ? null : currentPath);
        var allArticles = await articleRepo.ListAsync();
        var allFolders = await folderRepo.GetAllActiveAsync();

        var directArticles = allArticles
            .Where(a => ContentFormat.NormalizePath(a.TreePath) == currentPath)
            .OrderBy(a => a.Title, BeeMemoryBank.Core.UnderscoreFirstComparer.Instance)
            .ToList();

        var folderItems = new List<TreeListItem>();
        foreach (var folder in folders.OrderBy(f => f.Name, BeeMemoryBank.Core.UnderscoreFirstComparer.Instance))
        {
            var path = ContentFormat.NormalizePath(folder.Path);
            var count = allFolders.Count(f => ContentFormat.IsDirectChildFolder(path, ContentFormat.NormalizePath(f.Path)))
                      + allArticles.Count(a => ContentFormat.NormalizePath(a.TreePath) == path);
            folderItems.Add(new TreeListItem(folder.Name, true, folder.Path, null, null, false, ContentFormat.ItemCount(count)));
        }

        var articleItems = new List<TreeListItem>();
        foreach (var article in directArticles)
        {
            string preview;
            if (article.Protected)
            {
                preview = AppStrings.Get("ArticleProtectedMeta");
            }
            else
            {
                var body = await articleSvc.GetContentAsync(article.Id);
                preview = ContentFormat.PlainPreview(body);
                if (preview.Length == 0) preview = AppStrings.Get("EmptyArticlePreview");
            }
            var meta = article.UpdatedAt is { } at
                ? $"{ContentFormat.ShortDate(at)} · {preview}"
                : preview;
            articleItems.Add(new TreeListItem(article.Title, false, null, article.Id, article.UpdatedAt, article.Protected, meta));
        }

        var groups = new List<TreeGroup>();
        if (folderItems.Count > 0)
            groups.Add(new TreeGroup(AppStrings.Format("TreeFoldersFormat", folderItems.Count), folderItems));
        if (articleItems.Count > 0)
            groups.Add(new TreeGroup(AppStrings.Format("TreeArticlesFormat", articleItems.Count), articleItems));
        return groups;
    }
}
