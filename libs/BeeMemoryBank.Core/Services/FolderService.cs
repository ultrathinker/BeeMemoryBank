using BeeMemoryBank.Core;
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

        if (SystemFolders.IsReservedSystemPath(path))
            throw new InvalidOperationException(
                $"Folder name '{path}' is reserved for the system and cannot be created by users.");

        return await CreateInternalAsync(path, isSystem: false);
    }

    // Lazily create a reserved system folder (e.g. /_Drafts) the first time
    // backend code needs to write into it (failed offline-save, conflict draft,
    // restored from Hard Delete). Idempotent: returns the existing folder if it
    // already exists, upgrading is_system=1 on a legacy row created before the
    // protection landed.
    public async Task<Folder> EnsureSystemFolderAsync(string path)
    {
        path = NormalizePath(path);
        if (!SystemFolders.IsReservedSystemPath(path))
            throw new InvalidOperationException(
                $"Path '{path}' is not a registered system folder name.");

        var existing = await folderRepo.GetByPathAsync(path);
        if (existing != null)
        {
            if (!existing.IsSystem)
            {
                existing.IsSystem = true;
                await folderRepo.UpdateAsync(existing);
            }
            return existing;
        }
        return await CreateInternalAsync(path, isSystem: true);
    }

    private async Task<Folder> CreateInternalAsync(string path, bool isSystem)
    {
        var existing = await folderRepo.GetByPathAsync(path);
        if (existing != null)
            throw new InvalidOperationException($"Folder already exists at path '{path}'.");

        var identity = await nodeRepo.GetAsync();
        var lamportTs = clock.Tick();
        var now = DateTime.UtcNow;
        var parentPath = GetParentPath(path);

        // Authorize the folder being created BEFORE anything is persisted. folderRepo.CreateAsync
        // below applies the same check, but only after the ancestors have already been written —
        // and nothing rolls those back, so a denied caller could still litter a restricted subtree
        // with folder names (plaintext metadata, visible to everyone).
        folderRepo.ThrowIfWriteDenied(path);

        // Ancestors, explicitly unchecked. EnsureExistsAsync checks whatever it is given as the
        // leaf, and what we hand it is the PARENT — so an allow-list user creating the very folder
        // their allow entry names (/Work/Project) would be refused because /Work lies outside their
        // scope. The real leaf is authorized on the line above instead.
        if (parentPath != null)
            await folderRepo.EnsureAncestorsExistAsync(parentPath, identity?.NodeId);

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
            UpdatedAt = now,
            IsSystem = isSystem
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

        if (folder.IsSystem)
            throw new InvalidOperationException(
                $"System folder '{folder.Path}' cannot be renamed.");

        if (folder.RemoteSubscriptionId.HasValue)
            throw new InvalidOperationException(
                $"Folder '{folder.Path}' is a remote mirror and cannot be renamed.");

        // Block when descendants include any remote mirror mount-point — the
        // subscription's stored MountPath would no longer match the new tree
        // location and the next poll would resurrect the old path / dupe.
        // Caught by gemini round-3.
        await EnsureNoRemoteDescendantsAsync(folder.Path, "renamed");

        var oldPath = folder.Path;
        var newParentPath = folder.ParentPath;
        var newPath = (newParentPath != null ? newParentPath : "") + "/" + newName;
        newPath = "/" + newPath.Trim('/');

        // Defence-in-depth: forbid renaming to a reserved system path (otherwise
        // a user could create `/MyFolder`, fill it, then rename → `/_Drafts` and
        // have the next EnsureSystemFolderAsync upgrade it to is_system=1.
        if (SystemFolders.IsReservedSystemPath(newPath))
            throw new InvalidOperationException(
                $"Cannot rename to reserved system path '{newPath}'.");

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

        if (folder.IsSystem)
            throw new InvalidOperationException(
                $"System folder '{folder.Path}' cannot be moved.");

        if (folder.RemoteSubscriptionId.HasValue)
            throw new InvalidOperationException(
                $"Folder '{folder.Path}' is a remote mirror and cannot be moved.");

        await EnsureNoRemoteDescendantsAsync(folder.Path, "moved");

        var oldPath = folder.Path;
        var folderName = GetLastSegment(oldPath);
        var newPath = newParentPath.TrimEnd('/') + "/" + folderName;

        if (SystemFolders.IsReservedSystemPath(newPath))
            throw new InvalidOperationException(
                $"Cannot move to reserved system path '{newPath}'.");

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

        if (folder.IsSystem)
            throw new InvalidOperationException(
                $"System folder '{folder.Path}' cannot be deleted.");

        if (folder.RemoteSubscriptionId.HasValue)
            throw new InvalidOperationException(
                $"Folder '{folder.Path}' is a remote mirror. Detach the subscription on the Remote Accounts page instead.");

        await EnsureNoRemoteDescendantsAsync(folder.Path, "deleted");

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

    // Walks descendants of `path` and refuses the operation if any are flagged
    // as remote mirror roots. Detaching a subscription via the Remote Accounts
    // page is the only sanctioned way to remove a mirror.
    private async Task EnsureNoRemoteDescendantsAsync(string path, string verb)
    {
        var all = await folderRepo.GetAllActiveAsync();
        var prefix = path.TrimEnd('/') + "/";
        var blocker = all.FirstOrDefault(f =>
            f.RemoteSubscriptionId.HasValue
            && f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (blocker != null)
        {
            throw new InvalidOperationException(
                $"Folder '{path}' cannot be {verb} because it contains a remote-mirror subtree at '{blocker.Path}'. Detach it first.");
        }
    }

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
