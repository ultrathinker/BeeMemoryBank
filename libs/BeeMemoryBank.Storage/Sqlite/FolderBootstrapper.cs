using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Storage.Sqlite;

/// <summary>
/// Idempotent bootstrap: ensures all articles have folder_id populated.
/// Runs on every startup. Skips articles that already have folder_id set.
/// Does NOT emit sync events — this is a local migration, not a user action.
/// </summary>
public class FolderBootstrapper(
    IFolderRepository folderRepo,
    IArticleRepository articleRepo,
    INodeIdentityRepository nodeRepo)
{
    public async Task RunIfNeededAsync()
    {
        var articlesToFix = await articleRepo.GetArticlesWithNullFolderIdAsync();
        if (articlesToFix.Count == 0) return;

        var identity = await nodeRepo.GetAsync();

        var processedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, treePath) in articlesToFix)
        {
            var p = NormalizePath(treePath);
            if (p != "/" && processedPaths.Add(p))
            {
                await folderRepo.EnsureExistsAsync(p, identity?.NodeId);
            }
        }

        foreach (var (id, treePath) in articlesToFix)
        {
            var normalizedPath = NormalizePath(treePath);
            if (normalizedPath == "/") continue;
            var folder = await folderRepo.GetByPathAsync(normalizedPath);
            if (folder != null)
                await articleRepo.SetFolderIdAsync(id, folder.Id);
        }
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        return "/" + path.Trim('/');
    }
}
