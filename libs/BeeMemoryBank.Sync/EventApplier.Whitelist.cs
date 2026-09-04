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

        var incoming = new RowVersion(evt.LamportTs, evt.NodeId);

        var existing = await whitelistRepo.GetByNodeIdAsync(p.NodeId, includeDeleted: true);
        if (existing != null)
        {
            // The row is only touched by an add that actually supersedes what produced it.
            //
            // This is the gate that mattered: the re-activation below used to run on ANY add for a
            // revoked peer, with no regard for when that add was issued. An admin revokes a
            // compromised node; a peer that was offline at the time still holds the older
            // whitelist_add for it and delivers it on catch-up; the revoked node is back in the
            // mesh, and the revoking admin sees it active again with nothing to distinguish "my
            // revoke was undone" from "my revoke never applied".
            //
            // Deliberately plain LWW rather than a special "revoke always wins" rule: re-adding a
            // peer you previously revoked is a real workflow the UI offers, so revoke has to be
            // undoable — by a NEWER add. That is exactly what this comparison allows and what the
            // old code could not distinguish.
            // A revoked row that predates versioning cannot be compared, and must not lose by
            // default. Rows revoked before migration 021 sit at Lamport 0, so ANY incoming add
            // outranks them arithmetically — which would let a stale add from a peer that never
            // heard about the revocation put a revoked, possibly compromised, node back into the
            // mesh. Nothing in the row says whether the revoke was newer, so the safe reading of
            // "unknown" is that it was.
            //
            // This is not a dead end for the admin who genuinely wants the node back: adding a peer
            // locally writes the row directly with a fresh version, and that path is untouched. Only
            // a REMOTE add against an unversioned revocation is refused — which is exactly the
            // shape wanted, since a local admin action is a decision and an arriving old event is
            // not.
            if (existing.Status == "R" && existing.LamportTs == 0)
            {
                logger.LogWarning(
                    "WhitelistAdd for {NodeId} refused: the local row is revoked and predates row versioning, "
                    + "so the revoke cannot be compared against this event. Re-add the node locally if it should return.",
                    p.NodeId);
                return;
            }

            if (!ConflictResolver.IncomingWins(existing.Version, incoming))
            {
                logger.LogInformation(
                    "WhitelistAdd for {NodeId} dropped: row version ({RowTs}, {RowNode}) wins over event ({EventTs}, {EventNode})",
                    p.NodeId, existing.LamportTs, existing.SourceNodeId, evt.LamportTs, evt.NodeId);
                return;
            }

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
                existing.LamportTs = incoming.LamportTs;
                existing.SourceNodeId = incoming.SourceNodeId;
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
            UpdatedAt = now,
            LamportTs = incoming.LamportTs,
            SourceNodeId = incoming.SourceNodeId
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

        // A revoke older than the row's current version loses, same as everywhere else — an add
        // that came after this revoke was issued is the newer decision, and re-revoking on its
        // arrival would undo it. The row is then stamped with the revoke's own version so the
        // stale-add gate above compares against the revoke, not the write before it.
        var incoming = new RowVersion(evt.LamportTs, evt.NodeId);
        if (!ConflictResolver.IncomingWins(existing.Version, incoming))
        {
            logger.LogInformation(
                "WhitelistRevoke for {NodeId} dropped: row version ({RowTs}, {RowNode}) wins over event ({EventTs}, {EventNode})",
                p.NodeId, existing.LamportTs, existing.SourceNodeId, evt.LamportTs, evt.NodeId);
            return;
        }

        await whitelistRepoWrite.RevokeAsync(p.NodeId, incoming);
    }

    private async Task ApplyWhitelistUpdateAsync(SyncEvent evt)
    {
        var p = Deserialize<WhitelistUpdatePayload>(evt.Payload);

        // Never update self via a remote event.
        var localIdentity = await nodeIdentityRepo.GetAsync();
        if (localIdentity != null && p.NodeId == localIdentity.NodeId) return;

        var existing = await whitelistRepo.GetByNodeIdAsync(p.NodeId, includeDeleted: true);
        if (existing == null || existing.Status != "A") return;

        // Without this, two admins renaming the same peer (or moving its address) resolved to
        // whichever event happened to arrive last, and the nodes then disagreed about a row nothing
        // ever recompares.
        var incoming = new RowVersion(evt.LamportTs, evt.NodeId);
        if (!ConflictResolver.IncomingWins(existing.Version, incoming))
        {
            logger.LogInformation(
                "WhitelistUpdate for {NodeId} dropped: row version ({RowTs}, {RowNode}) wins over event ({EventTs}, {EventNode})",
                p.NodeId, existing.LamportTs, existing.SourceNodeId, evt.LamportTs, evt.NodeId);
            return;
        }

        if (p.ApiAddress != null) existing.ApiAddress = p.ApiAddress;
        if (p.DisplayName != null) existing.DisplayName = p.DisplayName;

        if (p.IsSuperadmin is { } isSuperadmin)
        {
            // A node may not promote itself. In practice the superadmin gate in ApplyAsync gets
            // there first — a demoted peer is no longer superadmin here, so its whitelist_update is
            // refused outright before this code runs, and a peer that IS still superadmin promoting
            // itself is a no-op. So this branch is unreachable today, and it is written down rather
            // than left out because the gate and this rule protect different things: the gate asks
            // "may this node change cluster state at all", and demotion is precisely the operation
            // that answers no. If the gate is ever relaxed — an "ordinary peers may rename
            // themselves" feature is the obvious way — regaining authority must not come with it.
            //
            // Demoting itself stays allowed: giving up your own privileges needs no protection, and
            // refusing it would block the legitimate "this node is stepping down" case.
            if (evt.NodeId == p.NodeId && isSuperadmin && !existing.IsSuperadmin)
            {
                logger.LogWarning(
                    "Ignoring self-promotion: node {NodeId} sent a whitelist_update raising its own is_superadmin",
                    evt.NodeId);
            }
            else
            {
                existing.IsSuperadmin = isSuperadmin;
            }
        }

        existing.UpdatedAt = DateTime.UtcNow;
        existing.LamportTs = incoming.LamportTs;
        existing.SourceNodeId = incoming.SourceNodeId;

        await whitelistRepoWrite.UpdateAsync(existing);
    }

    private async Task ApplyMasterPasswordChangedAsync(SyncEvent evt)
    {
        var p = Deserialize<MasterPasswordChangedPayload>(evt.Payload);

        // A notice older than this node's own last password change describes a gap that is
        // already closed: the peer changed at T1, this node was offline or was changed by hand at
        // T2 > T1, and the event only arrives now. Raising the banner for it would tell the admin
        // to redo something they have already done.
        //
        // The opposite order — a peer changing AFTER this node did — is deliberately NOT filtered.
        // Nothing in the event says whether the peer moved to the same password or a different
        // one, so a timestamp cannot tell "the operator is working through the mesh" from "someone
        // changed the password on a machine I do not control". The banner is raised and the admin
        // dismisses it (POST /api/keys/password-notice/dismiss); see migration 019 for why a
        // password verifier in the payload is not the answer.
        var localChangedAt = await nodeIdentityRepo.GetMasterPasswordChangedLocallyAtAsync();
        if (localChangedAt.HasValue && p.ChangedAt <= localChangedAt.Value)
        {
            logger.LogInformation(
                "Ignoring master_password_changed from {NodeName} at {ChangedAt}: this node changed its own master password later, at {LocalChangedAt}.",
                p.NodeName, p.ChangedAt, localChangedAt.Value);
            return;
        }

        // Nothing to rewrap: the event carries no key material by design (see the payload). All we
        // can do — and all we should do — is remember that this node is now out of step, so the
        // admin UI can say so and an admin can enter the new password here too.
        await nodeIdentityRepo.SetMasterPasswordNoticeAsync(p.ChangedAt, p.NodeName);

        logger.LogWarning(
            "Master password was changed on node {NodeName} at {ChangedAt}. This node still accepts the OLD password, including at its own /api/join, until an admin changes it here.",
            p.NodeName, p.ChangedAt);
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
            if (!ConflictResolver.IncomingWins(
                    RowVersion.Of(existing.LamportTs, existing.SourceNodeId),
                    new RowVersion(evt.LamportTs, evt.NodeId)))
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
            // Already soft-deleted — keep the winning delete, not merely the higher lamport.
            // tbl_comment stores delete_node_id alongside delete_lamport_ts precisely so this
            // comparison can be made the same way as every other one; it just was not using it.
            if (ConflictResolver.IncomingWins(
                    RowVersion.Of(existing.DeleteLamportTs ?? 0, existing.DeleteNodeId),
                    new RowVersion(evt.LamportTs, evt.NodeId)))
            {
                await commentRepo.SoftDeleteAsync(p.CommentId, evt.LamportTs, evt.NodeId);
            }
            return;
        }

        // Alive comment: LWW
        if (!ConflictResolver.IncomingWins(
                RowVersion.Of(existing.LamportTs, existing.SourceNodeId),
                new RowVersion(evt.LamportTs, evt.NodeId)))
            return; // Delete loses to existing create

        // Delete wins: soft-delete
        await commentRepo.SoftDeleteAsync(p.CommentId, evt.LamportTs, evt.NodeId);
    }
}
