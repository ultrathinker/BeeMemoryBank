using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

public partial class EventApplier
{
    private async Task ApplyHardDeleteAsync(SyncEvent evt)
    {
        var p = Deserialize<HardDeleteEventPayload>(evt.Payload);
        await hardDeleteService.ApplyRemoteAsync(p, evt.LamportTs, evt.NodeId, CancellationToken.None);
    }

    private async Task ApplyArticleCreateAsync(SyncEvent evt)
    {
        if (evt.ArticleId is null)
        {
            logger.LogWarning("Event {EventId} of type {EventType} missing required ArticleId, skipping", evt.EventId, evt.EventType);
            return;
        }
        var p = Deserialize<ArticleEventPayload>(evt.Payload);

        // Tombstone gate: article was deleted before; LWW vs delete's lamport.
        // Wave 2 audit: claude-A #2 (zombie article from out-of-order CREATE-after-DELETE).
        var tombstone = await tombstoneRepo.GetByEntityIdAsync(evt.ArticleId.Value);
        if (tombstone != null && tombstone.LamportTs >= evt.LamportTs)
        {
            logger.LogInformation("ArticleCreate {ArticleId} dropped: tombstone lamport={Tombstone} >= event lamport={Event}",
                evt.ArticleId, tombstone.LamportTs, evt.LamportTs);
            return;
        }

        // If article already exists — this is a duplicate create (rare case), apply as update
        var existing = await articleRepo.GetByIdAsync(evt.ArticleId.Value, includeDeleted: true);
        if (existing != null)
        {
            await ApplyArticleUpdateAsync(evt);
            return;
        }

        var now = DateTime.UtcNow;
        var article = new Article
        {
            Id = evt.ArticleId.Value,
            Title = p.Title,
            TreePath = p.TreePath,
            Status = p.Status,
            LamportTs = evt.LamportTs,
            SourceNodeId = evt.NodeId,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            // Old senders omit `protected` (null) — they cannot create protected articles, so false.
            Protected = p.Protected ?? false,
            ProtectionHint = p.ProtectionHint
        };

        await folderRepo.EnsureExistsAsync(p.TreePath, evt.NodeId);
        var folder = await folderRepo.GetByPathAsync(p.TreePath);
        article.FolderId = folder?.Id;

        await articleRepo.CreateAsync(article);

        var body = PayloadToBody(evt.ArticleId.Value, p);
        await bodyRepo.UpsertAsync(body);

        await conceptTagService.SetForArticleAsync(evt.ArticleId.Value, [.. p.ConceptTags ?? []]);
    }

    private async Task ApplyArticleUpdateAsync(SyncEvent evt)
    {
        if (evt.ArticleId is null)
        {
            logger.LogWarning("Event {EventId} of type {EventType} missing required ArticleId, skipping", evt.EventId, evt.EventType);
            return;
        }
        var p = Deserialize<ArticleEventPayload>(evt.Payload);

        var tombstone = await tombstoneRepo.GetByEntityIdAsync(evt.ArticleId.Value);
        if (tombstone != null && tombstone.LamportTs >= evt.LamportTs)
        {
            logger.LogInformation("ArticleUpdate {ArticleId} dropped: tombstone lamport={T} >= event lamport={E}",
                evt.ArticleId, tombstone.LamportTs, evt.LamportTs);
            return;
        }

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
                    MetadataJson = JsonSerializer.Serialize(new { existing.Title, existing.TreePath }),
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddDays(7)
                });
            }

            existing.Title = p.Title;
            existing.TreePath = p.TreePath;
            existing.Status = p.Status;
            existing.LamportTs = evt.LamportTs;
            existing.SourceNodeId = evt.NodeId;
            existing.UpdatedAt = p.UpdatedAt;
            // Only touch the lock flag when the sender actually knows about it (HasValue). A
            // pre-2026-06 node omits it (null) → keep the existing flag so a title-only edit from an
            // old peer can't strip protection off a body that is still a BMBENC1 ciphertext.
            if (p.Protected.HasValue)
            {
                existing.Protected = p.Protected.Value;
                existing.ProtectionHint = p.ProtectionHint;
            }

            await folderRepo.EnsureExistsAsync(p.TreePath, evt.NodeId);
            var folder = await folderRepo.GetByPathAsync(p.TreePath);
            existing.FolderId = folder?.Id;

            await articleRepo.UpdateAsync(existing);

            var body = PayloadToBody(evt.ArticleId.Value, p);
            await bodyRepo.UpsertAsync(body);

            await conceptTagService.SetForArticleAsync(evt.ArticleId.Value, [.. p.ConceptTags ?? []]);
        }
        else
        {
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
                MetadataJson = JsonSerializer.Serialize(new { p.Title, p.TreePath }),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(7)
            });
        }
    }

    private async Task ApplyArticleDeleteAsync(SyncEvent evt)
    {
        if (evt.ArticleId is null)
        {
            logger.LogWarning("Event {EventId} of type {EventType} missing required ArticleId, skipping", evt.EventId, evt.EventType);
            return;
        }
        var p = Deserialize<ArticleDeletePayload>(evt.Payload);

        var existing = await articleRepo.GetByIdAsync(evt.ArticleId.Value, includeDeleted: true);
        if (existing == null)
        {
            // Out-of-order: DELETE arrived before CREATE. Without recording a tombstone here,
            // a later CREATE would resurrect the article unconditionally — the delete would
            // be permanently lost. Mirror the comment SoftDeletePlaceholderAsync pattern by
            // writing a tombstone with the delete's lamport so a late CREATE goes through
            // the LWW gate at the top of ApplyArticleCreateAsync.
            // Wave 2 audit: claude-A #1, kilo-1 #2.
            await tombstoneRepo.CreateAsync(new Tombstone
            {
                ArticleId = evt.ArticleId.Value,
                CreatedAt = p.DeletedAt,
                ExpiresAt = p.DeletedAt.AddDays(60),
                LamportTs = evt.LamportTs,
                SourceNodeId = evt.NodeId
            });
            return;
        }
        if (existing.Status != "A") return;

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
            ExpiresAt = p.DeletedAt.AddDays(60),
            LamportTs = evt.LamportTs,
            SourceNodeId = evt.NodeId
        });
    }
}
