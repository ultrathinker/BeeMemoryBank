using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Applies an event from a remote node to the local database.
/// Verifies Ed25519 signature, idempotency, protocol_version.
/// </summary>
public class EventApplier(
    IArticleRepository articleRepo,
    IArticleBodyRepository bodyRepo,
    IEventLogRepository eventLogRepo,
    IWhitelistRepository whitelistRepo,
    IConflictVersionRepository conflictRepo,
    ITombstoneRepository tombstoneRepo,
    IWhitelistRepository whitelistRepoWrite,
    ICommentRepository commentRepo,
    IFolderRepository folderRepo,
    ILamportClock clock,
    IMediaRepository mediaRepo,
    BeeMemoryBank.Core.Services.MediaStorageOptions? mediaOptions)
{
    // whitelistRepoWrite is the same whitelist, just separated for read/write intent clarity
    // In reality it's the same object from DI

    public async Task ApplyAsync(SyncEvent evt)
    {
        // Protocol version check
        if (evt.ProtocolVersion != 1)
            throw new NotSupportedException($"Unknown protocol version: {evt.ProtocolVersion}");

        // Signature verification.
        // includeDeleted: true — historical events from revoked nodes MUST remain verifiable.
        // Revocation means "no new events accepted from this node going forward"; it does NOT
        // retroactively invalidate the node's past authored content (articles, folders, comments).
        // Without this, a sync replay after a revoke would crash on the revoked node's historical
        // events, SyncClient would break the loop, and the chain of events from that point forward
        // would become unreachable — including events from nodes that are still active.
        var node = await whitelistRepo.GetByNodeIdAsync(evt.NodeId, includeDeleted: true)
            ?? throw new UnauthorizedAccessException($"Node {evt.NodeId} is not in the whitelist.");

        var sigPayload = EventSignature.BuildPayload(evt);
        if (!Ed25519Signer.Verify(node.Ed25519PublicKey, sigPayload, evt.Signature))
            throw new InvalidDataException($"Invalid Ed25519 signature for event {evt.EventId}.");

        // Atomic idempotency: INSERT OR IGNORE the event first.
        // If it already exists, rows==0 and we skip. This eliminates the TOCTOU race
        // between ExistsAsync and AppendAsync that could cause double-apply in multi-process deployments.
        // Note: if the process crashes after insert but before applying data changes below,
        // the event is marked as "processed" but data wasn't changed. The next sync cycle
        // with the remote node will re-send events and the duplicate will be skipped.
        // Recovery: manually trigger a full re-sync if data divergence is detected.
        if (!await eventLogRepo.AppendIfNotExistsAsync(evt)) return;

        // Update Lamport clock
        clock.Update(evt.LamportTs);

        // Apply to local DB
        switch (evt.EventType)
        {
            case EventTypes.ArticleCreate:
                await ApplyArticleCreateAsync(evt);
                break;
            case EventTypes.ArticleUpdate:
                await ApplyArticleUpdateAsync(evt);
                break;
            case EventTypes.ArticleDelete:
                await ApplyArticleDeleteAsync(evt);
                break;
            case EventTypes.WhitelistAdd:
                await ApplyWhitelistAddAsync(evt);
                break;
            case EventTypes.WhitelistRevoke:
                await ApplyWhitelistRevokeAsync(evt);
                break;
            case EventTypes.WhitelistUpdate:
                await ApplyWhitelistUpdateAsync(evt);
                break;
            case EventTypes.CommentCreate:
                await ApplyCommentCreateAsync(evt);
                break;
            case EventTypes.CommentDelete:
                await ApplyCommentDeleteAsync(evt);
                break;
            case EventTypes.FolderCreate:
                await ApplyFolderCreateAsync(evt);
                break;
            case EventTypes.FolderRename:
                await ApplyFolderRenameAsync(evt);
                break;
            case EventTypes.FolderDelete:
                await ApplyFolderDeleteAsync(evt);
                break;
            case EventTypes.MediaCreate:
                await ApplyMediaCreateAsync(evt);
                break;
            case EventTypes.MediaDelete:
                await ApplyMediaDeleteAsync(evt);
                break;
            default:
                // Skip unknown event types (forward compatibility)
                break;
        }
    }

    private async Task ApplyArticleCreateAsync(SyncEvent evt)
    {
        var p = Deserialize<ArticleEventPayload>(evt.Payload);

        // If article already exists — this is a duplicate create (rare case), apply as update
        var existing = await articleRepo.GetByIdAsync(evt.ArticleId!.Value, includeDeleted: true);
        if (existing != null)
        {
            await ApplyArticleUpdateAsync(evt);
            return;
        }

        var now = DateTime.UtcNow;
        var article = new Article
        {
            Id = evt.ArticleId!.Value,
            Title = p.Title,
            TreePath = p.TreePath,
            Tags = [.. p.Tags],
            Status = p.Status,
            LamportTs = evt.LamportTs,
            SourceNodeId = evt.NodeId,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt
        };

        await folderRepo.EnsureExistsAsync(p.TreePath, evt.NodeId);
        var folder = await folderRepo.GetByPathAsync(p.TreePath);
        article.FolderId = folder?.Id;

        await articleRepo.CreateAsync(article);

        var body = PayloadToBody(evt.ArticleId.Value, p);
        await bodyRepo.UpsertAsync(body);
    }

    private async Task ApplyArticleUpdateAsync(SyncEvent evt)
    {
        var p = Deserialize<ArticleEventPayload>(evt.Payload);

        // Check tombstone (article was irreversibly deleted)
        if (await tombstoneRepo.ExistsAsync(evt.ArticleId!.Value)) return;

        var existing = await articleRepo.GetByIdAsync(evt.ArticleId.Value, includeDeleted: true);
        if (existing == null)
        {
            // Article doesn't exist locally — create it
            await ApplyArticleCreateAsync(evt);
            return;
        }

        var existingNodeId = existing.SourceNodeId ?? Guid.Empty;
        if (ConflictResolver.IncomingWins(existing.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
        {
            // Incoming event wins — save current as conflict_version (with metadata for recovery)
            var existingBody = await bodyRepo.GetByArticleIdAsync(existing.Id);
            if (existingBody != null && existing.LamportTs > 0)
            {
                await conflictRepo.CreateAsync(new ConflictVersion
                {
                    Id = Guid.NewGuid(),
                    ArticleId = existing.Id,
                    SourceNodeId = existingNodeId,
                    LamportTs = existing.LamportTs,
                    Ciphertext = existingBody.Ciphertext,
                    IV = existingBody.IV,
                    EncryptedDek = existingBody.EncryptedDek,
                    DekIV = existingBody.DekIV,
                    MetadataJson = JsonSerializer.Serialize(new { existing.Title, existing.Tags, existing.TreePath }),
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddDays(7)
                });
            }

            existing.Title = p.Title;
            existing.TreePath = p.TreePath;
            existing.Tags = [.. p.Tags];
            existing.Status = p.Status;
            existing.LamportTs = evt.LamportTs;
            existing.SourceNodeId = evt.NodeId;
            existing.UpdatedAt = p.UpdatedAt;

            await folderRepo.EnsureExistsAsync(p.TreePath, evt.NodeId);
            var folder = await folderRepo.GetByPathAsync(p.TreePath);
            existing.FolderId = folder?.Id;

            await articleRepo.UpdateAsync(existing);

            var body = PayloadToBody(evt.ArticleId.Value, p);
            await bodyRepo.UpsertAsync(body);
        }
        else
        {
            // Existing wins — save incoming as conflict_version (with metadata for recovery)
            var incomingBody = PayloadToBody(evt.ArticleId.Value, p);
            await conflictRepo.CreateAsync(new ConflictVersion
            {
                Id = Guid.NewGuid(),
                ArticleId = evt.ArticleId.Value,
                SourceNodeId = evt.NodeId,
                LamportTs = evt.LamportTs,
                Ciphertext = incomingBody.Ciphertext,
                IV = incomingBody.IV,
                EncryptedDek = incomingBody.EncryptedDek,
                DekIV = incomingBody.DekIV,
                MetadataJson = JsonSerializer.Serialize(new { p.Title, p.Tags, p.TreePath }),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(7)
            });
        }
    }

    private async Task ApplyArticleDeleteAsync(SyncEvent evt)
    {
        var p = Deserialize<ArticleDeletePayload>(evt.Payload);

        var existing = await articleRepo.GetByIdAsync(evt.ArticleId!.Value, includeDeleted: true);
        if (existing == null || existing.Status != "A") return;

        // LWW check: only delete + tombstone if incoming event wins over existing state.
        // A stale delete that loses LWW must NOT create a tombstone — otherwise it would
        // block recreation of an article ID that was never actually deleted (60-day TTL).
        var existingNodeId = existing.SourceNodeId ?? Guid.Empty;
        if (!ConflictResolver.IncomingWins(existing.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
            return;

        await articleRepo.SoftDeleteAsync(evt.ArticleId.Value);
        await tombstoneRepo.CreateAsync(new Tombstone
        {
            ArticleId = evt.ArticleId.Value,
            CreatedAt = p.DeletedAt,
            ExpiresAt = p.DeletedAt.AddDays(60)
        });
    }

    private async Task ApplyWhitelistAddAsync(SyncEvent evt)
    {
        var p = Deserialize<WhitelistAddPayload>(evt.Payload);
        var existing = await whitelistRepo.GetByNodeIdAsync(p.NodeId, includeDeleted: true);
        if (existing != null)
        {
            // If previously revoked, re-activate — but NEVER replace the Ed25519 public key.
            // The key is bound to NodeId at first registration. Replacing it via a stale/replayed
            // WhitelistAdd event would allow node impersonation with a compromised old key.
            if (existing.Status == "R")
            {
                existing.DisplayName = p.DisplayName;
                existing.ApiAddress = p.ApiAddress;
                existing.CanGenerateEmbeddings = p.CanGenerateEmbeddings;
                existing.Status = "A";
                existing.UpdatedAt = DateTime.UtcNow;
                await whitelistRepoWrite.UpdateAsync(existing);
            }
            return;
        }

        var now = DateTime.UtcNow;
        await whitelistRepoWrite.CreateAsync(new WhitelistEntry
        {
            NodeId = p.NodeId,
            DisplayName = p.DisplayName,
            Ed25519PublicKey = Convert.FromBase64String(p.PublicKeyB64),
            ApiAddress = p.ApiAddress,
            CanGenerateEmbeddings = p.CanGenerateEmbeddings,
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private async Task ApplyWhitelistRevokeAsync(SyncEvent evt)
    {
        var p = Deserialize<WhitelistRevokePayload>(evt.Payload);
        var existing = await whitelistRepo.GetByNodeIdAsync(p.NodeId, includeDeleted: true);
        if (existing == null || existing.Status != "A") return;
        await whitelistRepoWrite.RevokeAsync(p.NodeId);
    }

    private async Task ApplyWhitelistUpdateAsync(SyncEvent evt)
    {
        var p = Deserialize<WhitelistUpdatePayload>(evt.Payload);
        var existing = await whitelistRepo.GetByNodeIdAsync(p.NodeId, includeDeleted: true);
        if (existing == null || existing.Status != "A") return;

        if (p.ApiAddress != null) existing.ApiAddress = p.ApiAddress;
        if (p.DisplayName != null) existing.DisplayName = p.DisplayName;
        existing.UpdatedAt = DateTime.UtcNow;

        await whitelistRepoWrite.UpdateAsync(existing);
    }

    private async Task ApplyCommentCreateAsync(SyncEvent evt)
    {
        var p = Deserialize<CommentEventPayload>(evt.Payload);
        if (await commentRepo.ExistsByCommentIdAsync(p.CommentId)) return; // idempotency

        await commentRepo.CreateFromSyncAsync(new Comment
        {
            CommentId = p.CommentId,
            ArticleId = p.ArticleId,
            Text = p.Encrypted ? "" : p.Text,
            SourceNodeId = evt.NodeId,
            CreatedAt = p.CreatedAt,
            Ciphertext = p.CiphertextB64 != null ? Convert.FromBase64String(p.CiphertextB64) : null,
            IV = p.IvB64 != null ? Convert.FromBase64String(p.IvB64) : null,
            Encrypted = p.Encrypted
        });
    }

    // AUDIT: No LWW check here and hard DELETE instead of soft-delete.
    // Comment model has no LamportTs field, so proper LWW requires a schema migration
    // (add lamport_ts + source_node_id to tbl_comment + soft-delete status column).
    // Acceptable risk: comments are lightweight, delete-create conflicts are rare in practice.
    private async Task ApplyCommentDeleteAsync(SyncEvent evt)
    {
        var p = Deserialize<CommentDeletePayload>(evt.Payload);
        await commentRepo.DeleteByCommentIdAsync(p.CommentId);
    }

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
        if (folder == null || folder.Status == "D") return; // already deleted or unknown

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
        if (existing != null) return;

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
            CreatedAt = p.CreatedAt
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

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json)
        ?? throw new InvalidDataException($"Failed to deserialize payload as {typeof(T).Name}");

    private static EncryptedArticleBody PayloadToBody(Guid articleId, ArticleEventPayload p) =>
        new()
        {
            ArticleId = articleId,
            Ciphertext = Convert.FromBase64String(p.CiphertextB64),
            IV = Convert.FromBase64String(p.IvB64),
            EncryptedDek = Convert.FromBase64String(p.EncryptedDekB64),
            DekIV = Convert.FromBase64String(p.DekIvB64)
        };
}
