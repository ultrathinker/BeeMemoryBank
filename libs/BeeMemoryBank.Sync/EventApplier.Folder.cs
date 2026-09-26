using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

public partial class EventApplier
{
    private async Task ApplyFolderCreateAsync(SyncEvent evt)
    {
        var p = Deserialize<FolderCreatePayload>(evt.Payload);

        var existing = await folderRepo.GetByIdAsync(p.FolderId, includeDeleted: true);
        if (existing != null)
        {
            // Already exists — apply LWW: incoming wins if its timestamp is newer
            if (!ConflictResolver.IncomingWins(
                    RowVersion.Of(existing.LamportTs, existing.SourceNodeId),
                    new RowVersion(evt.LamportTs, evt.NodeId)))
                return; // local wins, skip

            existing.Path = p.Path;
            existing.Name = p.Name;
            existing.ParentPath = p.ParentPath;
            existing.Status = "A";
            existing.LamportTs = evt.LamportTs;
            existing.SourceNodeId = evt.NodeId;
            existing.UpdatedAt = p.UpdatedAt;
            existing.DeletedAt = null;
            await folderRepo.UpdateAsync(existing);
            return;
        }

        // Ensure parent path exists
        if (p.ParentPath != null)
            await folderRepo.EnsureExistsAsync(p.ParentPath, evt.NodeId);

        await folderRepo.CreateAsync(new Folder
        {
            Id = p.FolderId,
            Path = p.Path,
            Name = p.Name,
            ParentPath = p.ParentPath,
            Status = "A",
            LamportTs = evt.LamportTs,
            SourceNodeId = evt.NodeId,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt
        });
    }

    /// <summary>
    /// Finds the local folder an incoming rename or delete refers to: by the sender's id first,
    /// then by path. Folder ids are not shared across nodes for every folder — an ancestor that
    /// EnsureExistsAsync vivified (the parent of a synced folder_create, the folders above a new
    /// article) gets a fresh local id and no event of its own, so two nodes hold the same folder
    /// under different ids. Looking up by id alone made a rename or delete of such a folder a
    /// silent no-op on the peer, and the tree diverged for good.
    /// </summary>
    private async Task<Folder?> ResolveFolderAsync(Guid folderId, string path)
        => await folderRepo.GetByIdAsync(folderId, includeDeleted: true)
           ?? await folderRepo.GetByPathAsync(path);

    private async Task ApplyFolderRenameAsync(SyncEvent evt)
    {
        var p = Deserialize<FolderRenamePayload>(evt.Payload);

        var folder = await ResolveFolderAsync(p.FolderId, p.OldPath);
        if (folder == null)
        {
            // Folder not known locally — ensure the old path exists so rename can proceed
            await folderRepo.EnsureExistsAsync(p.OldPath, evt.NodeId);
            folder = await folderRepo.GetByPathAsync(p.OldPath);
            if (folder == null) return; // cannot apply
        }

        // LWW check
        if (!ConflictResolver.IncomingWins(
                RowVersion.Of(folder.LamportTs, folder.SourceNodeId),
                new RowVersion(evt.LamportTs, evt.NodeId)))
            return; // local wins, skip

        // folder.Id, not p.FolderId: when the folder was found by path above, the local row has a
        // different id, and RenamePathAsync updates the folder row itself by id — with the sender's
        // id it would move the subfolders and the articles' paths but leave the folder row behind.
        await folderRepo.RenamePathAsync(p.OldPath, p.NewPath, folder.Id, evt.LamportTs, evt.NodeId, p.UpdatedAt);

        // The folder-ACL cache stores resolved PATHS, not folder ids. FolderService does this for
        // local renames; a rename arriving over sync has to as well, or a rule on the old path
        // keeps being enforced against a path that no longer exists — permissive-stale, since the
        // folder is now reachable under its new name by someone the rule was meant to exclude.
        // RenamePathAsync moves the whole subtree, so descendants' rules go stale too; clearing
        // every entry is cheaper to get right than enumerating them.
        FolderAccessService.InvalidateAll();
    }

    private async Task ApplyFolderDeleteAsync(SyncEvent evt)
    {
        var p = Deserialize<FolderDeletePayload>(evt.Payload);

        var folder = await ResolveFolderAsync(p.FolderId, p.Path);
        if (folder == null) return;

        if (folder.Status == "D")
        {
            // Already deleted: advance the tombstone metadata only if this delete actually
            // supersedes the recorded one. Through the comparator, not a bare `>`, so two nodes
            // that deleted the same folder at the same Lamport tick agree on which delete the row
            // ends up attributed to — otherwise the row's source_node_id depends on arrival order,
            // and a later event comparing against it gets a different answer on each node.
            if (ConflictResolver.IncomingWins(
                    RowVersion.Of(folder.LamportTs, folder.SourceNodeId),
                    new RowVersion(evt.LamportTs, evt.NodeId)))
            {
                folder.LamportTs = evt.LamportTs;
                folder.SourceNodeId = evt.NodeId;
                folder.UpdatedAt = p.DeletedAt;
                await folderRepo.UpdateAsync(folder);
            }
            return;
        }

        // LWW check: skip stale delete events (matches pattern in ApplyFolderRenameAsync)
        if (!ConflictResolver.IncomingWins(
                RowVersion.Of(folder.LamportTs, folder.SourceNodeId),
                new RowVersion(evt.LamportTs, evt.NodeId)))
            return;

        // Detach articles BEFORE marking the folder deleted, not after. ClearFolderIdUnscopedAsync is a
        // plain idempotent UPDATE ("WHERE folder_id = @folderId"), safe to call more than once. In
        // the reverse order a crash in between leaves the folder permanently Status='D' with its
        // articles still pointing at the now-invisible folder id: the `folder.Status == "D"` branch
        // above returns early on every retry (only bumping lamport if higher) and never reaches
        // ClearFolderIdUnscopedAsync again, so the orphaned folder_id never self-heals. Detaching first means a crash before the soft-delete just leaves the folder
        // status still 'A', so the retry re-runs this whole method and completes it.
        await articleRepo.ClearFolderIdUnscopedAsync(folder.Id);
        await folderRepo.SoftDeleteAsync(folder.Id, p.DeletedAt);
        // Same reason as the article path: the already-deleted branch at the top of this method
        // compares against this row's version, so it has to be the delete's, not the last rename's.
        await folderRepo.SetDeleteVersionAsync(folder.Id, new RowVersion(evt.LamportTs, evt.NodeId));
        await folderAccess.InvalidateCacheForFolderAsync(folder.Id);
    }

    private async Task ApplyMediaCreateAsync(SyncEvent evt)
    {
        var p = Deserialize<MediaEventPayload>(evt.Payload);

        var existing = await mediaRepo.GetByIdAsync(p.MediaId, includeDeleted: true);
        if (existing != null)
        {
            if (!ConflictResolver.IncomingWins(
                    RowVersion.Of(existing.LamportTs, existing.SourceNodeId),
                    new RowVersion(evt.LamportTs, evt.NodeId)))
                return;
            // Media ciphertext is immutable; only advance LWW metadata so later
            // delete events with stale timestamps are rejected correctly.
            await mediaRepo.UpdateLamportTsAsync(p.MediaId, evt.LamportTs, evt.NodeId);
            return;
        }

        var media = new Media
        {
            Id = p.MediaId,
            ArticleId = p.ArticleId,
            FileName = p.FileName,
            ContentType = p.ContentType,
            FileSize = p.FileSize,
            EncryptedDek = Convert.FromBase64String(p.EncryptedDekB64),
            DekIV = Convert.FromBase64String(p.DekIvB64),
            IV = Convert.FromBase64String(p.IvB64),
            Status = "A",
            LamportTs = evt.LamportTs,
            SourceNodeId = evt.NodeId,
            CreatedAt = p.CreatedAt,
            Kind = p.Kind,
            // Stamped from the blob store just below, once the bytes are guaranteed to be in it.
            CiphertextSha256 = p.CiphertextSha256
        };

        // Media ciphertext lives in the content-addressed blob store — no .enc file is written.
        // Ensure the blob is present and stamp its hash onto the row:
        //   • protocol-2 event: the transport already shipped the blob, so ResolveCiphertextAsync
        //     reads it back and StoreAsync is an idempotent no-op returning the same hash;
        //   • protocol-1 (inline-ciphertext) event: this is where those bytes first enter the blob
        //     store, and StoreAsync gives the row the hash the event did not carry.
        // Resolve BEFORE the insert so a not-yet-arrived protocol-2 blob fails the whole apply (the
        // event is retried once the bytes land) rather than leaving a row that points at nothing.
        var ciphertext = await ResolveCiphertextAsync(p.CiphertextB64, p.CiphertextSha256);
        media.CiphertextSha256 = await blobRepo.StoreAsync(ciphertext);

        await mediaRepo.CreateAsync(media);
    }

    private async Task ApplyMediaDeleteAsync(SyncEvent evt)
    {
        var p = Deserialize<MediaDeletePayload>(evt.Payload);
        var existing = await mediaRepo.GetByIdAsync(p.MediaId, includeDeleted: true);
        if (existing == null || existing.Status == "D") return;

        // LWW check: skip stale delete events
        if (!ConflictResolver.IncomingWins(
                RowVersion.Of(existing.LamportTs, existing.SourceNodeId),
                new RowVersion(evt.LamportTs, evt.NodeId)))
            return;

        await mediaRepo.SoftDeleteAsync(p.MediaId);
    }
}
