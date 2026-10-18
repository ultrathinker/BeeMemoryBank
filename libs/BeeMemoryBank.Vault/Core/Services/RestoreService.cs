using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Restore logic for soft-deleted articles and folders.
/// Design decision: restore creates a NEW copy at the root with a [RESTORED] prefix
/// — it does not flip the 'deleted' flag on the original. The original stays in the
/// trash (Superadmin can hard-delete it separately). The copy flows through the
/// normal create paths, so sync to other nodes and all side effects (embeddings,
/// event log, audit) happen for free.
///
/// Stopping (BMB-311 fix round 3): both restores look at the caller's token before their first write and not after it. An article restore is that one write; a folder
/// restore that has created its root runs to the end of the (cheap) subtree, so a stop never leaves half a restored tree.
/// </summary>
public class RestoreService(
    IArticleRepository articleRepo,
    IFolderRepository folderRepo,
    ArticleService articleService,
    FolderService folderService,
    ConceptTagService conceptTagService,
    SessionService session)
{
    private const string RestoredPrefix = "[RESTORED] ";

    /// <summary>Test seam: called once the restored folder's root exists (a test stops the restore from here, to show that the subtree is still written whole).</summary>
    internal Action? AfterRootCreated { get; set; }

    public record RestoreArticleResult(Guid NewArticleId, string NewTitle);
    public record RestoreFolderResult(string NewFolderPath, int RestoredSubfolderCount);

    /// <summary>
    /// Decrypts the soft-deleted article's body, then creates a brand-new article
    /// in root with title "[RESTORED] {title}", copying concept tags. Comments and
    /// version history are not copied.
    /// </summary>
    public async Task<RestoreArticleResult> RestoreArticleAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!session.IsUnlocked)
            throw new InvalidOperationException("Session must be unlocked to restore articles (body needs to be decrypted).");

        var article = await articleRepo.GetByIdAsync(id, includeDeleted: true)
            ?? throw new KeyNotFoundException($"Article {id} not found.");
        if (article.Status != "D")
            throw new InvalidOperationException("Only soft-deleted articles can be restored.");

        string plaintext;
        try
        {
            plaintext = await articleService.GetContentAsync(id);
        }
        catch (KeyNotFoundException)
        {
            throw new InvalidOperationException("Cannot restore: article body is missing (ciphertext was already purged).");
        }
        var tags = await conceptTagService.GetByArticleIdAsync(id);

        var newTitle = RestoredPrefix + article.Title;
        // The last point at which the restore can stop: from here it is one write.
        ct.ThrowIfCancellationRequested();
        var newArticle = await articleService.CreateAsync(newTitle, "/", tags, plaintext);

        return new RestoreArticleResult(newArticle.Id, newTitle);
    }

    /// <summary>
    /// Creates a new folder at root with name "[RESTORED] {name}" (with " (2)", " (3)"...
    /// suffix if the name is taken) and — if the folder was cascade-deleted with subfolders
    /// — recreates the cascaded subfolder tree under it, preserving relative structure.
    /// Articles are not restored: soft-deleting a folder doesn't soft-delete its articles,
    /// it orphans them (folder_id = NULL, tree_path = '/'). Those orphans remain active in
    /// the root and the user can move them into the restored folder if they wish.
    /// </summary>
    public async Task<RestoreFolderResult> RestoreFolderAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var folder = await folderRepo.GetByIdAsync(id, includeDeleted: true)
            ?? throw new KeyNotFoundException($"Folder {id} not found.");
        if (folder.Status != "D")
            throw new InvalidOperationException("Only soft-deleted folders can be restored.");

        var baseName = RestoredPrefix + folder.Name;
        // Retry loop defends against a race where another restore picked the same suffix
        // between our uniqueness check and CreateAsync (which throws on path collision).
        string newRootPath = "";
        for (int attempt = 0; attempt < 5; attempt++)
        {
            // Before the first write only: once the root exists the subtree is written to its end.
            if (attempt == 0) ct.ThrowIfCancellationRequested();
            var candidate = await GenerateUniqueRootNameAsync(baseName);
            var candidatePath = "/" + candidate;
            try
            {
                await folderService.CreateAsync(candidatePath);
                newRootPath = candidatePath;
                AfterRootCreated?.Invoke();
                break;
            }
            catch (InvalidOperationException) when (attempt < 4)
            {
                // Raced: another restore grabbed this name — re-compute with a fresh query.
            }
        }
        if (string.IsNullOrEmpty(newRootPath))
            throw new InvalidOperationException("Could not allocate a unique restore folder name after multiple attempts.");

        int restoredSubCount = 0;
        if (folder.CascadeDeleteOpId.HasValue)
        {
            var subfolders = await folderRepo.ListSoftDeletedByCascadeOpIdAsync(
                folder.CascadeDeleteOpId.Value, folder.Path);

            var oldPathPrefix = folder.Path.TrimEnd('/');
            foreach (var sub in subfolders)
            {
                var relative = sub.Path[oldPathPrefix.Length..]; // starts with '/'
                var newSubPath = newRootPath + relative;
                try
                {
                    await folderService.CreateAsync(newSubPath);
                    restoredSubCount++;
                }
                catch (InvalidOperationException)
                {
                    // Already exists (auto-created as ancestor of a previously processed sibling).
                    // CreateAsync throws if the exact path exists; safe to ignore.
                }
            }
        }

        return new RestoreFolderResult(newRootPath, restoredSubCount);
    }

    private async Task<string> GenerateUniqueRootNameAsync(string baseName)
    {
        var path = "/" + baseName;
        if (await folderRepo.GetByPathAsync(path) == null)
            return baseName;

        for (int i = 2; i <= 99; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (await folderRepo.GetByPathAsync("/" + candidate) == null)
                return candidate;
        }
        // Fall back to a timestamp — extremely unlikely we ever reach here.
        return $"{baseName} ({DateTime.UtcNow:yyyy-MM-dd HH-mm-ss})";
    }
}
