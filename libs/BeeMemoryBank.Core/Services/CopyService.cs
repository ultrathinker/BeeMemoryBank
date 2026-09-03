using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Generic "Copy To" service: deep-copies any article or folder into a new
/// location with re-encryption. The new copy is a brand-new article (fresh
/// per-article DEK, new GUID, new media GUIDs) — no metadata link to the
/// source is retained.
///
/// Failure semantics: a best-effort compensating cleanup is attempted if any
/// step fails partway. Articles and folders created during the call are
/// tracked, and on exception their soft-delete is invoked under the system
/// scope. Cleanup itself never throws — it surfaces the original error.
/// </summary>
public class CopyService(
    ArticleService articleService,
    FolderService folderService,
    MediaService mediaService,
    IArticleRepository articleRepo,
    IFolderRepository folderRepo,
    ConceptTagService conceptTagService,
    CallerScopeHolder scopeHolder)
{
    private static readonly Regex MediaRefRegex =
        new(@"/api/media/(?<id>[0-9a-fA-F-]{36})", RegexOptions.Compiled);

    /// <summary>
    /// Copy a single article into <paramref name="targetFolderPath"/>.
    /// If the target already contains an article with the same title, the new
    /// copy is renamed with a " (N)" suffix. Media references in the body are
    /// re-mapped to fresh per-copy media records so the original article can
    /// be deleted without breaking the copy. Returns the new article ID.
    /// </summary>
    public async Task<Guid> CopyArticleAsync(Guid sourceArticleId, string targetFolderPath)
    {
        var createdArticles = new List<Guid>();
        var createdFolders = new List<Guid>();
        var createdMedia = new List<Guid>();

        try
        {
            return await CopyArticleInternalAsync(
                sourceArticleId, targetFolderPath, createdArticles, createdFolders, createdMedia);
        }
        catch
        {
            await RollbackAsync(createdArticles, createdFolders, createdMedia);
            throw;
        }
    }

    /// <summary>
    /// Recursively copy a folder and all its descendants (sub-folders + articles)
    /// into <paramref name="targetParentPath"/>. Source structure is preserved
    /// underneath a (possibly renamed) root. On any failure during traversal,
    /// the partial copy is rolled back via soft-delete (best-effort).
    /// </summary>
    public async Task<Guid> CopyFolderAsync(Guid sourceFolderId, string targetParentPath)
    {
        var createdArticles = new List<Guid>();
        var createdFolders = new List<Guid>();
        var createdMedia = new List<Guid>();

        try
        {
            return await CopyFolderInternalAsync(
                sourceFolderId, targetParentPath, createdArticles, createdFolders, createdMedia);
        }
        catch
        {
            await RollbackAsync(createdArticles, createdFolders, createdMedia);
            throw;
        }
    }

    private async Task<Guid> CopyArticleInternalAsync(
        Guid sourceArticleId,
        string targetFolderPath,
        List<Guid> createdArticles,
        List<Guid> createdFolders,
        List<Guid> createdMedia)
    {
        var source = await articleRepo.GetByIdAsync(sourceArticleId)
            ?? throw new KeyNotFoundException($"Article {sourceArticleId} not found.");

        targetFolderPath = TreePathCanonicalizer.Canonicalize(targetFolderPath);

        var sourceContent = await articleService.GetContentAsync(sourceArticleId);
        var tags = await conceptTagService.GetByArticleIdAsync(sourceArticleId);
        var sourceMedia = await mediaService.GetByArticleIdAsync(sourceArticleId);

        var newTitle = await ResolveTitleCollision(source.Title, targetFolderPath);

        // First, create the article with the original content. The new article
        // ID is needed to link the freshly-copied media records.
        var created = await articleService.CreateAsync(newTitle, targetFolderPath, tags.ToList(), sourceContent);
        createdArticles.Add(created.Id);

        // CreateAsync already derived the `protected` flag from the copied BMBENC1 body; carry the
        // reminder hint across too so the copy's lock screen looks identical to the original.
        if (source.Protected && source.ProtectionHint != null)
            await articleService.UpdateAsync(created.Id, protectionHint: source.ProtectionHint, updateHint: true);

        // No media referenced — done.
        if (sourceMedia.Count == 0)
            return created.Id;

        // Copy each media record to the new article and build oldId→newId map.
        var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var oldMedia in sourceMedia)
        {
            var fetched = await mediaService.GetContentAsync(oldMedia.Id);
            if (fetched is null)
            {
                // Refuse to ship a copy with stale references — rollback covers
                // the partial work (kilo round-3). Silent skip used to leave the
                // copy pointing at the original article's media, which then
                // broke if the original was later deleted.
                throw new InvalidOperationException(
                    $"Cannot copy: media {oldMedia.Id} on source article {sourceArticleId} could not be read (decryption or orphan).");
            }
            var newMedia = await mediaService.CreateAsync(
                fetched.Value.fileName, fetched.Value.contentType, fetched.Value.data, created.Id,
                isAttachment: oldMedia.Kind == "attachment");
            createdMedia.Add(newMedia.Id);
            idMap[oldMedia.Id.ToString("D")] = newMedia.Id.ToString("D");
        }

        // Rewrite body so it references the new media GUIDs. Skip the update
        // if nothing referenced the copied media (text-only mentions).
        var newContent = MediaRefRegex.Replace(sourceContent, m =>
        {
            var oldId = m.Groups["id"].Value;
            return idMap.TryGetValue(oldId, out var newId)
                ? $"/api/media/{newId}"
                : m.Value;
        });

        if (!ReferenceEquals(newContent, sourceContent) && newContent != sourceContent)
            await articleService.UpdateAsync(created.Id, plaintext: newContent);

        return created.Id;
    }

    private async Task<Guid> CopyFolderInternalAsync(
        Guid sourceFolderId,
        string targetParentPath,
        List<Guid> createdArticles,
        List<Guid> createdFolders,
        List<Guid> createdMedia)
    {
        var source = await folderRepo.GetByIdAsync(sourceFolderId)
            ?? throw new KeyNotFoundException($"Folder {sourceFolderId} not found.");

        targetParentPath = TreePathCanonicalizer.Canonicalize(targetParentPath);

        var srcPrefix = source.Path.TrimEnd('/') + "/";
        if (targetParentPath == source.Path
            || targetParentPath.StartsWith(srcPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cannot copy a folder into itself or one of its descendants.");
        }

        var newRootName = await ResolveFolderNameCollision(source.Name, targetParentPath);
        var newRootPath = JoinPath(targetParentPath, newRootName);

        var newRoot = await folderService.CreateAsync(newRootPath);
        createdFolders.Add(newRoot.Id);

        var allFolders = await folderRepo.GetAllActiveAsync();
        var subFolders = allFolders
            .Where(f => f.Path.StartsWith(srcPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Path.Length)
            .ToList();

        foreach (var subfolder in subFolders)
        {
            var suffix = subfolder.Path[source.Path.Length..]; // includes leading '/'
            var mapped = newRootPath + suffix;
            var created = await folderService.CreateAsync(mapped);
            createdFolders.Add(created.Id);
        }

        // Filter at SQL layer (LIKE prefix) instead of loading the entire vault
        // into memory just to copy a subtree — gemini round-3 OOM finding.
        var subtreeArticles = await articleRepo.ListAsync(source.Path);

        foreach (var article in subtreeArticles)
        {
            var sourcePath = article.TreePath ?? source.Path;
            var suffix = sourcePath.Length > source.Path.Length
                ? sourcePath[source.Path.Length..]
                : "";
            var targetPath = newRootPath + suffix;
            await CopyArticleInternalAsync(article.Id, targetPath, createdArticles, createdFolders, createdMedia);
        }

        return newRoot.Id;
    }

    private async Task RollbackAsync(List<Guid> articles, List<Guid> folders, List<Guid> media)
    {
        // Elevate to the system scope so the cleanup is not blocked by ACL
        // (these IDs were created by us in this call, so we own them logically).
        using var _ = scopeHolder.ElevateToSystem();

        foreach (var mediaId in media)
        {
            try { await mediaService.DeleteAsync(mediaId); }
            catch { /* best effort — already cleaned up, or other failure */ }
        }
        foreach (var articleId in articles)
        {
            try { await articleService.DeleteAsync(articleId); }
            catch { }
        }
        // Delete folders deepest-first so children clear before parents.
        foreach (var folderId in folders.AsEnumerable().Reverse())
        {
            try { await folderService.DeleteAsync(folderId); }
            catch { }
        }
    }

    private async Task<string> ResolveTitleCollision(string title, string targetFolderPath)
    {
        var existing = (await articleRepo.ListAsync(targetFolderPath))
            .Where(a => string.Equals(a.TreePath, targetFolderPath, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Title)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains(title)) return title;
        for (var i = 1; i < 1000; i++)
        {
            var candidate = $"{title} ({i})";
            if (!existing.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("Too many name collisions in target folder.");
    }

    private async Task<string> ResolveFolderNameCollision(string folderName, string targetParentPath)
    {
        var siblings = await folderRepo.GetChildrenAsync(targetParentPath == "/" ? null : targetParentPath);
        var taken = siblings.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(folderName)) return folderName;
        for (var i = 1; i < 1000; i++)
        {
            var candidate = $"{folderName} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("Too many name collisions at target parent.");
    }

    private static string JoinPath(string parent, string name)
    {
        if (parent == "/") return "/" + name;
        return parent.TrimEnd('/') + "/" + name;
    }
}
