using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

public class FolderService(
    IFolderRepository folderRepo,
    IArticleRepository articleRepo,
    INodeIdentityRepository nodeRepo,
    ILamportClock clock,
    IEventLogger eventLogger,
    FolderAccessService folderAccessService)
{
    public async Task<Folder> CreateAsync(string path)
    {
        path = NormalizePath(path);

        var existing = await folderRepo.GetByPathAsync(path);
        if (existing != null)
            throw new InvalidOperationException($"Folder already exists at path '{path}'.");

        var identity = await nodeRepo.GetAsync();
        var lamportTs = clock.Tick();
        var now = DateTime.UtcNow;
        var parentPath = GetParentPath(path);

        // Ensure parent exists first
        if (parentPath != null)
            await folderRepo.EnsureExistsAsync(parentPath, identity?.NodeId);

        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            Path = path,
            Name = GetLastSegment(path),
            ParentPath = parentPath,
            Status = "A",
            LamportTs = lamportTs,
            SourceNodeId = identity?.NodeId,
            CreatedAt = now,
            UpdatedAt = now
        };

        await folderRepo.CreateAsync(folder);
        await eventLogger.LogFolderCreateAsync(folder);
        return folder;
    }

    public async Task RenameAsync(Guid folderId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            throw new ArgumentException("Folder name cannot be empty.");
        if (newName.Contains('/') || newName.Contains('\\') || newName == ".." || newName == ".")
            throw new ArgumentException("Folder name contains invalid characters.");
        if (newName.Length > 255)
            throw new ArgumentException("Folder name is too long (max 255 characters).");

        var folder = await folderRepo.GetByIdAsync(folderId)
            ?? throw new KeyNotFoundException($"Folder {folderId} not found");

        var oldPath = folder.Path;
        var newParentPath = folder.ParentPath;
        var newPath = (newParentPath != null ? newParentPath : "") + "/" + newName;
        newPath = "/" + newPath.Trim('/');

        var descendantIds = await folderRepo.ListIdsByPathPrefixAsync(oldPath);

        var lamportTs = clock.Tick();
        var updatedAt = DateTime.UtcNow;
        var identity = await nodeRepo.GetAsync();

        // 1. Rename in DB (atomic: folder + sub-folders + articles)
        await folderRepo.RenamePathAsync(oldPath, newPath, folderId, lamportTs, identity?.NodeId, updatedAt);

        await eventLogger.LogFolderRenameAsync(folderId, oldPath, newPath, newName, newParentPath, lamportTs, updatedAt);

        // ACL cache holds resolved path strings; path changes here must invalidate dependent user caches.
        var allFolderIds = descendantIds.Prepend(folderId);
        await folderAccessService.InvalidateCacheForFoldersAsync(allFolderIds);
    }

    public async Task MoveAsync(Guid folderId, string newParentPath)
    {
        if (string.IsNullOrWhiteSpace(newParentPath) || !newParentPath.StartsWith('/'))
            throw new ArgumentException("Path must start with '/'.");

        var folder = await folderRepo.GetByIdAsync(folderId)
            ?? throw new KeyNotFoundException($"Folder {folderId} not found");

        var oldPath = folder.Path;
        var folderName = GetLastSegment(oldPath);
        var newPath = newParentPath.TrimEnd('/') + "/" + folderName;

        if (newPath == oldPath) return;
        if (newPath.StartsWith(oldPath + "/"))
            throw new ArgumentException("Cannot move a folder into itself.");

        var existing = await folderRepo.GetByPathAsync(newPath);
        if (existing != null)
            throw new InvalidOperationException($"A folder named '{folderName}' already exists at '{newParentPath}'.");

        var descendantIds = await folderRepo.ListIdsByPathPrefixAsync(oldPath);

        var lamportTs = clock.Tick();
        var updatedAt = DateTime.UtcNow;
        var identity = await nodeRepo.GetAsync();

        await folderRepo.RenamePathAsync(oldPath, newPath, folderId, lamportTs, identity?.NodeId, updatedAt);
        await eventLogger.LogFolderRenameAsync(folderId, oldPath, newPath, folderName, newParentPath, lamportTs, updatedAt);

        // ACL cache holds resolved path strings; path changes here must invalidate dependent user caches.
        var allFolderIds = descendantIds.Prepend(folderId);
        await folderAccessService.InvalidateCacheForFoldersAsync(allFolderIds);
    }

    public async Task DeleteAsync(Guid folderId)
    {
        var folder = await folderRepo.GetByIdAsync(folderId)
            ?? throw new KeyNotFoundException($"Folder {folderId} not found");

        var deletedAt = DateTime.UtcNow;
        // Shared op id tags this folder and its cascade-deleted subfolders, so
        // Restore can later recreate exactly the subtree that went down together.
        var cascadeOpId = Guid.NewGuid();

        var subfolderIds = await folderRepo.ListIdsByPathPrefixAsync(folder.Path);
        foreach (var subId in subfolderIds)
            await articleRepo.ClearFolderIdAsync(subId);

        await folderRepo.SoftDeleteByPathPrefixAsync(folder.Path, deletedAt, cascadeOpId);

        await folderRepo.SoftDeleteAsync(folderId, deletedAt, cascadeOpId);
        await articleRepo.ClearFolderIdAsync(folderId);
        await eventLogger.LogFolderDeleteAsync(folderId, folder.Path, deletedAt);
    }

    // Single source of truth in TreePathCanonicalizer — rejects "." / ".."
    // / control chars / double slashes so a User scoped to /Public can no
    // longer create "/Public/../Admin/Whatever" (literal-string namespace
    // pollution) and a peer can no longer push such paths via sync.
    private static string NormalizePath(string path) =>
        TreePathCanonicalizer.Canonicalize(path);

    private static string? GetParentPath(string path)
    {
        if (path == "/") return null;
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx <= 0 ? null : trimmed[..idx];
    }

    private static string GetLastSegment(string path)
    {
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx < 0 ? trimmed.TrimStart('/') : trimmed[(idx + 1)..];
    }
}
