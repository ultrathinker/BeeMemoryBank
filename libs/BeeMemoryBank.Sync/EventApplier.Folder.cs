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
            var existingNodeId = existing.SourceNodeId ?? Guid.Empty;
            if (!ConflictResolver.IncomingWins(existing.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
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

    private async Task ApplyFolderRenameAsync(SyncEvent evt)
    {
        var p = Deserialize<FolderRenamePayload>(evt.Payload);

        var folder = await folderRepo.GetByIdAsync(p.FolderId, includeDeleted: true);
        if (folder == null)
        {
            // Folder not known locally — ensure the old path exists so rename can proceed
            await folderRepo.EnsureExistsAsync(p.OldPath, evt.NodeId);
            folder = await folderRepo.GetByPathAsync(p.OldPath);
            if (folder == null) return; // cannot apply
        }

        // LWW check
        var existingNodeId = folder.SourceNodeId ?? Guid.Empty;
        if (!ConflictResolver.IncomingWins(folder.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
            return; // local wins, skip

        await folderRepo.RenamePathAsync(p.OldPath, p.NewPath, p.FolderId, evt.LamportTs, evt.NodeId, p.UpdatedAt);
    }

    private async Task ApplyFolderDeleteAsync(SyncEvent evt)
    {
        var p = Deserialize<FolderDeletePayload>(evt.Payload);

        var folder = await folderRepo.GetByIdAsync(p.FolderId, includeDeleted: true);
        if (folder == null) return;

        if (folder.Status == "D")
        {
            if (evt.LamportTs > folder.LamportTs)
            {
                folder.LamportTs = evt.LamportTs;
                folder.SourceNodeId = evt.NodeId;
                folder.UpdatedAt = p.DeletedAt;
                await folderRepo.UpdateAsync(folder);
            }
            return;
        }

        // LWW check: skip stale delete events (matches pattern in ApplyFolderRenameAsync)
        var existingNodeId = folder.SourceNodeId ?? Guid.Empty;
        if (!ConflictResolver.IncomingWins(folder.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
            return;

        await folderRepo.SoftDeleteAsync(p.FolderId, p.DeletedAt);
        await articleRepo.ClearFolderIdAsync(p.FolderId);
    }

    private async Task ApplyMediaCreateAsync(SyncEvent evt)
    {
        var p = Deserialize<MediaEventPayload>(evt.Payload);

        var existing = await mediaRepo.GetByIdAsync(p.MediaId, includeDeleted: true);
        if (existing != null)
        {
            var existingNodeId = existing.SourceNodeId ?? Guid.Empty;
            if (!ConflictResolver.IncomingWins(existing.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
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
            Kind = p.Kind
        };
        if (mediaOptions != null)
        {
            var mediaDir = mediaOptions.MediaDir;
            Directory.CreateDirectory(mediaDir);
            var filePath = Path.Combine(mediaDir, $"{p.MediaId}.enc");
            await File.WriteAllBytesAsync(filePath, Convert.FromBase64String(p.CiphertextB64));
        }

        await mediaRepo.CreateAsync(media);
    }

    private async Task ApplyMediaDeleteAsync(SyncEvent evt)
    {
        var p = Deserialize<MediaDeletePayload>(evt.Payload);
        var existing = await mediaRepo.GetByIdAsync(p.MediaId, includeDeleted: true);
        if (existing == null || existing.Status == "D") return;

        // LWW check: skip stale delete events
        var existingNodeId = existing.SourceNodeId ?? Guid.Empty;
        if (!ConflictResolver.IncomingWins(existing.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
            return;

        await mediaRepo.SoftDeleteAsync(p.MediaId);
    }
}
