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
    FolderAccessService folderAccessService,
    CallerScopeHolder scopeHolder)
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
        // '/' and '\' are rejected here (not left to Canonicalize below) because newName must
        // stay a SINGLE path segment -- Canonicalize would happily accept "a/b" as two legal
        // segments and silently change what folder gets created/renamed instead of rejecting it.
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
        // M7: route the assembled path through TreePathCanonicalizer — the single source of truth
        // for tree-path normalization — instead of a hand-rolled Trim('/') join. The checks above
        // reject '/' and '\' in newName but not control characters, and a hand-rolled join can't
        // catch those. A non-canonical path that slipped through used to survive locally while
        // EventApplier.cs rejects non-canonical paths from peers outright: the rename would appear
        // to succeed here and every peer would silently discard the resulting FolderRename event,
        // permanently diverging the mesh with nothing but a warning in a log to show for it.
        var newPath = TreePathCanonicalizer.Canonicalize((newParentPath ?? "") + "/" + newName);

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

        // M7: canonicalize the caller-supplied parent path through TreePathCanonicalizer.
        // Previously only the leading '/' was checked, so "/Work/../.." or "//Archive" survived
        // straight into tbl_folder.path via the TrimEnd('/') join below. Beyond storing garbage,
        // a non-canonical path is REJECTED by peers (EventApplier.cs), so the move would succeed
        // locally and every peer would silently drop the FolderRename event — permanent mesh
        // divergence. Canonicalize also collapses "//" so the deny-prefix matcher (which compares
        // raw strings) can't be evaded by an extra slash.
        newParentPath = TreePathCanonicalizer.Canonicalize(newParentPath);

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
        // M7: OrdinalIgnoreCase to agree with RenamePathAsync's descendant rewrite, which matches
        // via SQLite's default case-insensitive LIKE. A culture-sensitive/case-sensitive StartsWith
        // here let a caller move a folder into a differently-cased alias of its own descendant
        // (e.g. oldPath "/Work" into newParentPath "/WORK/Sub") straight past this guard — the SQL
        // below would then match and rewrite "/Work"'s own row as a descendant of itself, corrupting
        // the tree, precisely the self-nesting this check exists to prevent.
        if (newPath.StartsWith(oldPath + "/", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Runs every guard <see cref="DeleteAsync"/> enforces, without changing anything: system
    /// folder, remote mirror, remote descendants, and the descendant write-ACL walk.
    ///
    /// <para>
    /// Exists because the REST delete endpoint deletes the folder's ARTICLES before it calls
    /// <see cref="DeleteAsync"/>. Any guard that only fires inside <see cref="DeleteAsync"/> is
    /// therefore reached too late: the caller gets a correct 403, but their articles are already
    /// gone — a denied request that still destroyed data. That trap already had a comment in
    /// FolderEndpoints for the system/remote cases; the H1 descendant-ACL check re-opened it,
    /// because the endpoint's own descendant pre-check only ever covered callers with no allow
    /// rows. Validate through this method BEFORE destroying anything.
    /// </para>
    /// </summary>
    public async Task EnsureDeletableAsync(Guid folderId)
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

        // The authoritative descendant write-ACL check — the same one SoftDeleteByPathPrefixAsync
        // runs, so the two can't drift. Superadmin/System scope is skipped inside the repository.
        await folderRepo.ThrowIfAnyDescendantWriteDeniedAsync(folder.Path);
    }

    public async Task DeleteAsync(Guid folderId)
    {
        var folder = await folderRepo.GetByIdAsync(folderId)
            ?? throw new KeyNotFoundException($"Folder {folderId} not found");

        // Re-validated here rather than assumed: DeleteAsync is also called directly (MCP tools,
        // other services), not only through the endpoint that pre-checks.
        await EnsureDeletableAsync(folderId);

        var deletedAt = DateTime.UtcNow;
        // Shared op id tags this folder and its cascade-deleted subfolders, so
        // Restore can later recreate exactly the subtree that went down together.
        var cascadeOpId = Guid.NewGuid();

        // H1: capture the descendant id list up front — read-only, no ACL check needed for a bare
        // id list — BEFORE SoftDeleteByPathPrefixAsync flips their status to 'D' (it only returns
        // status='A' rows, so calling it after would silently return an empty list and skip the
        // ClearFolderIdAsync loop below entirely).
        var subfolderIds = await folderRepo.ListIdsByPathPrefixAsync(folder.Path);

        // SoftDeleteByPathPrefixAsync now walks every descendant folder under folder.Path and
        // throws if this caller is denied or read-only on ANY of them, not just on folder.Path
        // itself — a caller can be authorized on the top of a subtree (allow=/, deny=/Work/Secret)
        // while a descendant is individually denied. Running it BEFORE the loop below means a
        // denied descendant aborts the whole cascade instead of the loop already having relocated
        // its articles to '/' via ClearFolderIdAsync — which carries no ACL check of its own (by
        // design: it's also called from sync/background code) and must never be reachable for a
        // denied path from this user-facing method.
        await folderRepo.SoftDeleteByPathPrefixAsync(folder.Path, deletedAt, cascadeOpId);

        foreach (var subId in subfolderIds)
            await articleRepo.ClearFolderIdAsync(subId);

        await folderRepo.SoftDeleteAsync(folderId, deletedAt, cascadeOpId);
        await articleRepo.ClearFolderIdAsync(folderId);

        // Emit a delete event for EVERY folder this cascade took down, not just the one the caller
        // named. The local cascade above is a bulk UPDATE that writes no events, and
        // EventApplier.ApplyFolderDeleteAsync only ever acts on the single folder id inside the
        // event it is given — so logging only the top folder meant a peer deleted `/Work` and left
        // `/Work/Reports` alive, with its articles still attached, forever. Nothing detects or
        // repairs that: the mesh just silently disagrees about the tree from then on.
        //
        // One event per folder rather than one "delete this subtree" event, so each folder keeps
        // its own Lamport comparison on the receiving side — a peer that has a genuinely newer
        // version of one subfolder still wins for that subfolder instead of being flattened by a
        // coarse subtree delete. ListSoftDeletedByCascadeOpIdAsync returns exactly the rows this
        // op just marked (id and path), so the events describe what actually happened.
        foreach (var cascaded in await folderRepo.ListSoftDeletedByCascadeOpIdAsync(cascadeOpId, folder.Path))
        {
            if (cascaded.Id == folderId) continue; // logged below as the named folder
            await eventLogger.LogFolderDeleteAsync(cascaded.Id, cascaded.Path, deletedAt);
        }

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
        // L7: GetAllActiveAsync ACL-filters its result (FilterFolders) for the ambient scope. A
        // remote-mirror descendant hidden from THIS caller by a deny rule would then be invisible
        // to the FirstOrDefault below, letting a rename/move/delete silently corrupt or orphan a
        // mirror subscription the caller merely cannot see — not one they were ever authorized to
        // touch. This check protects data integrity (a mirror's stored MountPath must track
        // reality), not the caller's own read access, so it has to run against the TRUE,
        // unfiltered folder set. Same scope-swap pattern as FolderRepository.EnsureExistsCoreAsync's
        // ancestor-stub lookup.
        var previousScope = scopeHolder.Scope;
        scopeHolder.Scope = SystemCallerScope.Instance;
        List<Folder> all;
        try
        {
            all = await folderRepo.GetAllActiveAsync();
        }
        finally
        {
            scopeHolder.Scope = previousScope;
        }

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
