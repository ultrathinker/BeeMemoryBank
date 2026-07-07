using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

public partial class EventApplier
{
    private async Task ApplyWhitelistAddAsync(SyncEvent evt)
    {
        var p = Deserialize<WhitelistAddPayload>(evt.Payload);

        // Skip self — a node must never be in its own whitelist.
        // This can happen when another node adds *us* as their trusted node and
        // syncs that whitelist_add event back to us. We already know we exist
        // (tbl_node_identity); having a row in tbl_whitelist about ourselves
        // is confusing in the UI and corrupts sync position bookkeeping.
        var localIdentity = await nodeIdentityRepo.GetAsync();
        if (localIdentity != null && p.NodeId == localIdentity.NodeId) return;

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
                existing.IsSuperadmin = p.IsSuperadmin;
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
            IsSuperadmin = p.IsSuperadmin,
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private async Task ApplyWhitelistRevokeAsync(SyncEvent evt)
    {
        var p = Deserialize<WhitelistRevokePayload>(evt.Payload);

        // Never revoke self via a remote event — we can't revoke ourselves.
        var localIdentity = await nodeIdentityRepo.GetAsync();
        if (localIdentity != null && p.NodeId == localIdentity.NodeId) return;

        var existing = await whitelistRepo.GetByNodeIdAsync(p.NodeId, includeDeleted: true);
        if (existing == null || existing.Status != "A") return;
        await whitelistRepoWrite.RevokeAsync(p.NodeId);
    }

    private async Task ApplyWhitelistUpdateAsync(SyncEvent evt)
    {
        var p = Deserialize<WhitelistUpdatePayload>(evt.Payload);

        // Never update self via a remote event.
        var localIdentity = await nodeIdentityRepo.GetAsync();
        if (localIdentity != null && p.NodeId == localIdentity.NodeId) return;

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

        var existing = await commentRepo.GetByCommentIdAsync(p.CommentId);

        // Downgrade-prevention: refuse a brand-new unencrypted comment under an encrypted
        // article body. Skip this gate for existing-row paths (LWW resurrect / update),
        // which preserve the originally-encrypted payload anyway.
        if (existing == null)
        {
            var parentBody = await bodyRepo.GetByArticleIdAsync(p.ArticleId);
            if (parentBody != null && !p.Encrypted)
            {
                logger.LogWarning("Rejecting unencrypted comment for encrypted article {ArticleId} (downgrade attempt)", p.ArticleId);
                return;
            }
        }

        if (existing?.DeletedAt != null)
        {
            // Soft-deleted row (tombstone equivalent): check LWW
            // If delete's lamport >= create's lamport, dead stays dead.
            if ((existing.DeleteLamportTs ?? 0) >= evt.LamportTs)
                return;

            // Create wins: resurrect the row with real data
            await commentRepo.ResurrectFromSyncAsync(p.CommentId, new Comment
            {
                CommentId = p.CommentId,
                ArticleId = p.ArticleId,
                Text = p.Encrypted ? "" : p.Text,
                SourceNodeId = evt.NodeId,
                CreatedAt = p.CreatedAt,
                Ciphertext = p.CiphertextB64 != null ? Convert.FromBase64String(p.CiphertextB64) : null,
                IV = p.IvB64 != null ? Convert.FromBase64String(p.IvB64) : null,
                Encrypted = p.Encrypted,
                LamportTs = evt.LamportTs
            });
            return;
        }

        if (existing != null)
        {
            // Alive row: LWW
            var existingNodeId = existing.SourceNodeId ?? Guid.Empty;
            if (!ConflictResolver.IncomingWins(existing.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
                return;
            // LWW-wins must update CONTENT too — old code only bumped lamport, leaving stale
            // text/ciphertext attached to a newer timestamp. Future comparisons would see
            // the stale content as "newer". (Wave 2 audit kilo-1 #3.)
            await commentRepo.ResurrectFromSyncAsync(p.CommentId, new Comment
            {
                CommentId = p.CommentId,
                ArticleId = p.ArticleId,
                Text = p.Encrypted ? "" : p.Text,
                SourceNodeId = evt.NodeId,
                CreatedAt = p.CreatedAt,
                Ciphertext = p.CiphertextB64 != null ? Convert.FromBase64String(p.CiphertextB64) : null,
                IV = p.IvB64 != null ? Convert.FromBase64String(p.IvB64) : null,
                Encrypted = p.Encrypted,
                LamportTs = evt.LamportTs
            });
            return;
        }

        // No row: create
        await commentRepo.CreateFromSyncAsync(new Comment
        {
            CommentId = p.CommentId,
            ArticleId = p.ArticleId,
            Text = p.Encrypted ? "" : p.Text,
            SourceNodeId = evt.NodeId,
            CreatedAt = p.CreatedAt,
            Ciphertext = p.CiphertextB64 != null ? Convert.FromBase64String(p.CiphertextB64) : null,
            IV = p.IvB64 != null ? Convert.FromBase64String(p.IvB64) : null,
            Encrypted = p.Encrypted,
            LamportTs = evt.LamportTs
        });
    }

    private async Task ApplyCommentDeleteAsync(SyncEvent evt)
    {
        var p = Deserialize<CommentDeletePayload>(evt.Payload);
        var existing = await commentRepo.GetByCommentIdAsync(p.CommentId);

        if (existing == null)
        {
            // Comment not on this node yet — insert placeholder ghost row so future
            // CommentCreate events can be blocked if delete has higher lamport.
            await commentRepo.SoftDeletePlaceholderAsync(p.CommentId, evt.LamportTs, evt.NodeId);
            return;
        }

        if (existing.DeletedAt != null)
        {
            // Already soft-deleted — keep the higher lamport
            if (evt.LamportTs > (existing.DeleteLamportTs ?? 0))
                await commentRepo.SoftDeleteAsync(p.CommentId, evt.LamportTs, evt.NodeId);
            return;
        }

        // Alive comment: LWW
        var existingNodeId = existing.SourceNodeId ?? Guid.Empty;
        if (!ConflictResolver.IncomingWins(existing.LamportTs, existingNodeId, evt.LamportTs, evt.NodeId))
            return; // Delete loses to existing create

        // Delete wins: soft-delete
        await commentRepo.SoftDeleteAsync(p.CommentId, evt.LamportTs, evt.NodeId);
    }
}
