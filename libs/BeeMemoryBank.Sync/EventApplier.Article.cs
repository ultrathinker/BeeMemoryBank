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

    // Public entry points (ApplyArticleCreateAsync / ApplyArticleUpdateAsync / ApplyArticleDeleteAsync)
    // acquire ArticleWriteLock and then delegate to the …CoreAsync method that assumes the lock is
    // already held. This mirrors ArticleService's UpdateAsync/UpdateCoreAsync split, for the same
    // reason: ArticleWriteLock is NOT reentrant, and these three methods call each other (a
    // duplicate CREATE is applied as an UPDATE, a late UPDATE for an unknown article is applied as
    // a CREATE) — if the cross-call went through the locking entry point again, the second
    // AcquireAsync would block forever on a semaphore this same logical call already holds. Only
    // the Core methods may call each other; only the public methods may call ArticleWriteLock.
    //
    // The lock itself closes M9: without it, a local read-modify-write (bee_append/prepend/replace,
    // which read the current body then write it back under a freshly-ticked Lamport timestamp) could
    // interleave with a peer's update landing here, read the pre-peer-update body, and write it back
    // — silently discarding the peer's edit mesh-wide, with the peer's version surviving only 7 days
    // in tbl_conflict_version. Acquiring the same static, per-article-id lock ArticleService uses
    // serializes the two.

    private async Task ApplyArticleCreateAsync(SyncEvent evt)
    {
        if (evt.ArticleId is null)
        {
            logger.LogWarning("Event {EventId} of type {EventType} missing required ArticleId, skipping", evt.EventId, evt.EventType);
            return;
        }
        using var _ = await ArticleWriteLock.AcquireAsync(evt.ArticleId.Value);
        await ApplyArticleCreateCoreAsync(evt);
    }

    private async Task ApplyArticleCreateCoreAsync(SyncEvent evt)
    {
        var articleId = evt.ArticleId!.Value;
        var p = Deserialize<ArticleEventPayload>(evt.Payload);

        // Tombstone gate: article was deleted before; LWW vs delete's lamport.
        // Wave 2 audit: claude-A #2 (zombie article from out-of-order CREATE-after-DELETE).
        var tombstone = await tombstoneRepo.GetByEntityIdAsync(articleId);
        // Through the one comparator, with the tombstone's own node id — not a bare `>=`.
        //
        // The bare version dropped the event whenever the timestamps merely TIED, and a tie is not
        // the rare case: two nodes that were in sync and each write once produce the same Lamport
        // number every time. A delete on A and an edit on B, both at L=11, therefore resolved one
        // way here (tombstone always wins) and the other way in the delete path below (which does
        // use the tiebreak) — so the article ended alive on one node and gone on the other, for
        // half of all node-id pairs, deterministically. Neither node ever reconciles it: both
        // believe they applied the newest write.
        //
        // tbl_tombstone has carried source_node_id since the Wave 2 rerun; only this gate was not
        // reading it.
        if (tombstone != null &&
            !ConflictResolver.IncomingWins(
                RowVersion.Of(tombstone.LamportTs, tombstone.SourceNodeId),
                new RowVersion(evt.LamportTs, evt.NodeId)))
        {
            logger.LogInformation(
                "ArticleCreate {ArticleId} dropped: tombstone version ({TombstoneTs}, {TombstoneNode}) wins over event ({EventTs}, {EventNode})",
                articleId, tombstone.LamportTs, tombstone.SourceNodeId, evt.LamportTs, evt.NodeId);
            return;
        }

        // If article already exists — this is a duplicate create (rare case), apply as update
        var existing = await articleRepo.GetByIdAsync(articleId, includeDeleted: true);
        if (existing != null)
        {
            await ApplyArticleUpdateCoreAsync(evt);
            return;
        }

        var article = new Article
        {
            Id = articleId,
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

        // Folder auto-vivification stays outside the transaction below — same call as
        // ArticleService.CreateAsync makes, for the same reason (see its comment): an ancestor
        // folder vivified by an apply that later rolls back is an inert, harmless empty folder.
        await folderRepo.EnsureExistsAsync(p.TreePath, evt.NodeId);
        var folder = await folderRepo.GetByPathAsync(p.TreePath);
        article.FolderId = folder?.Id;

        var body = await PayloadToBodyAsync(articleId, p);
        var tags = (p.ConceptTags ?? []).ToList();

        // Precompute tag embeddings BEFORE opening the transaction: ONNX inference is CPU-bound and
        // ConceptTagService.SetForArticleAsync refuses to run it while a transaction is open (it
        // would hold the SQLite write lock for however long inference takes). Mirrors
        // ArticleService.CreateAsync's PrecomputeNewTagEmbeddingsAsync call.
        var precomputedEmbeddings = await conceptTagService.PrecomputeNewTagEmbeddingsAsync(tags);

        // H5: the article row, its encrypted body and its concept-tag links must land together or
        // not at all. Before this fix they went through three separate connections/transactions —
        // if the process crashed between the row write and the body write, the event was never
        // recorded (see ApplyAsync's ordering comment), so sync would redeliver it. But the redelivered
        // event would then see existing.LamportTs == evt.LamportTs and existing.SourceNodeId ==
        // evt.NodeId (the row DID commit before the crash), which ties ConflictResolver.IncomingWins
        // and loses — so the retry filed the real body into a 7-day conflict-version row instead of
        // ever completing the create, leaving GetContentAsync throwing forever. Wrapping the three
        // writes in one transaction means a crash anywhere in here rolls all of it back, so the
        // redelivered event finds no article row at all and creates cleanly. Mirrors
        // ArticleService.CreateAsync's transaction shape.
        using (var conn = connFactory.CreateConnection())
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                await articleRepo.CreateAsync(article, tx);
                await bodyRepo.UpsertAsync(body, tx);
                await conceptTagService.SetForArticleAsync(articleId, tags, precomputedEmbeddings, tx);
                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { /* SQLite may have already auto-rolled back; don't mask the real failure */ }
                throw;
            }
        }
    }

    private async Task ApplyArticleUpdateAsync(SyncEvent evt)
    {
        if (evt.ArticleId is null)
        {
            logger.LogWarning("Event {EventId} of type {EventType} missing required ArticleId, skipping", evt.EventId, evt.EventType);
            return;
        }
        using var _ = await ArticleWriteLock.AcquireAsync(evt.ArticleId.Value);
        await ApplyArticleUpdateCoreAsync(evt);
    }

    private async Task ApplyArticleUpdateCoreAsync(SyncEvent evt)
    {
        var articleId = evt.ArticleId!.Value;
        var p = Deserialize<ArticleEventPayload>(evt.Payload);

        var tombstone = await tombstoneRepo.GetByEntityIdAsync(articleId);
        // Same rule as the create gate above, and it has to be the same or the two disagree about
        // the same pair of events depending only on which one arrived.
        if (tombstone != null &&
            !ConflictResolver.IncomingWins(
                RowVersion.Of(tombstone.LamportTs, tombstone.SourceNodeId),
                new RowVersion(evt.LamportTs, evt.NodeId)))
        {
            logger.LogInformation(
                "ArticleUpdate {ArticleId} dropped: tombstone version ({TombstoneTs}, {TombstoneNode}) wins over event ({EventTs}, {EventNode})",
                articleId, tombstone.LamportTs, tombstone.SourceNodeId, evt.LamportTs, evt.NodeId);
            return;
        }

        var existing = await articleRepo.GetByIdAsync(articleId, includeDeleted: true);
        if (existing == null)
        {
            // Article doesn't exist locally — create it
            await ApplyArticleCreateCoreAsync(evt);
            return;
        }

        // Named once because the conflict-version row below has to record the SAME version this
        // comparison just ruled against — that row is how a human recovers the losing body, and it
        // is worthless if its (lamport, node) does not match what actually lost.
        var existingVersion = RowVersion.Of(existing.LamportTs, existing.SourceNodeId);

        if (ConflictResolver.IncomingWins(existingVersion, new RowVersion(evt.LamportTs, evt.NodeId)))
        {
            // Incoming event wins — save current as conflict_version (with metadata for recovery).
            // Deliberately OUTSIDE the transaction below and BEFORE it: IConflictVersionRepository
            // doesn't take a transaction (unlike the article/body/tag repos), so it can't join that
            // transaction. Ordering it first instead of last keeps retries safe: if the process
            // crashes between this write and the transactional update, the old content is already
            // preserved, and a redelivered event just re-reads the same still-unchanged `existing`
            // row, re-runs IncomingWins the same way, and writes a second (harmless, duplicate)
            // conflict-version row before completing the update — no data is ever lost either way.
            var existingBody = await bodyRepo.GetByArticleIdAsync(existing.Id);
            if (existingBody != null && existing.LamportTs > 0)
            {
                await conflictRepo.CreateAsync(new ConflictVersion
                {
                    Id = Guid.NewGuid(),
                    ArticleId = existing.Id,
                    SourceNodeId = existingVersion.SourceNodeId,
                    LamportTs = existingVersion.LamportTs,
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

            // Folder auto-vivification stays outside the transaction — see the identical comment in
            // ApplyArticleCreateCoreAsync.
            await folderRepo.EnsureExistsAsync(p.TreePath, evt.NodeId);
            var folder = await folderRepo.GetByPathAsync(p.TreePath);
            existing.FolderId = folder?.Id;

            var body = await PayloadToBodyAsync(articleId, p);
            var tags = (p.ConceptTags ?? []).ToList();

            // Precompute BEFORE the transaction — see the identical comment in
            // ApplyArticleCreateCoreAsync.
            var precomputedEmbeddings = await conceptTagService.PrecomputeNewTagEmbeddingsAsync(tags);

            // H5: article row + body + concept tags land together or not at all. See the long
            // comment in ApplyArticleCreateCoreAsync for the failure mode this closes — same
            // mechanism, just on the update path (a crash between UpdateAsync and UpsertAsync used
            // to leave new metadata paired with the OLD body, permanently, since the retry would
            // then tie on (LamportTs, SourceNodeId) and lose IncomingWins).
            using (var conn = connFactory.CreateConnection())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    await articleRepo.UpdateAsync(existing, tx);
                    await bodyRepo.UpsertAsync(body, tx);
                    await conceptTagService.SetForArticleAsync(articleId, tags, precomputedEmbeddings, tx);
                    tx.Commit();
                }
                catch
                {
                    try { tx.Rollback(); } catch { /* SQLite may have already auto-rolled back; don't mask the real failure */ }
                    throw;
                }
            }
        }
        else
        {
            var incomingBody = await PayloadToBodyAsync(articleId, p);
            await conflictRepo.CreateAsync(new ConflictVersion
            {
                Id = Guid.NewGuid(),
                ArticleId = articleId,
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
        using var _ = await ArticleWriteLock.AcquireAsync(evt.ArticleId.Value);
        await ApplyArticleDeleteCoreAsync(evt);
    }

    private async Task ApplyArticleDeleteCoreAsync(SyncEvent evt)
    {
        var articleId = evt.ArticleId!.Value;
        var p = Deserialize<ArticleDeletePayload>(evt.Payload);

        var existing = await articleRepo.GetByIdAsync(articleId, includeDeleted: true);
        if (existing == null)
        {
            // Out-of-order: DELETE arrived before CREATE. Without recording a tombstone here,
            // a later CREATE would resurrect the article unconditionally — the delete would
            // be permanently lost. Mirror the comment SoftDeletePlaceholderAsync pattern by
            // writing a tombstone with the delete's lamport so a late CREATE goes through
            // the LWW gate at the top of ApplyArticleCreateCoreAsync.
            // Wave 2 audit: claude-A #1, kilo-1 #2.
            await tombstoneRepo.CreateAsync(new Tombstone
            {
                ArticleId = articleId,
                CreatedAt = p.DeletedAt,
                ExpiresAt = p.DeletedAt.AddDays(60),
                LamportTs = evt.LamportTs,
                SourceNodeId = evt.NodeId
            });
            return;
        }
        if (existing.Status != "A")
        {
            // Already deleted — but by WHICH delete? Two nodes deleting the same article
            // independently each apply the other's delete to a row that is already 'D'. Returning
            // here unconditionally (what this used to do) left each node attributing the row to
            // its own delete, so the two disagreed about its version forever; a later event at the
            // same Lamport then resolved differently on each of them. Run the same comparison as
            // everywhere else and converge on the delete that wins it.
            var recorded = RowVersion.Of(existing.LamportTs, existing.SourceNodeId);
            var arriving = new RowVersion(evt.LamportTs, evt.NodeId);
            if (ConflictResolver.IncomingWins(recorded, arriving))
            {
                await tombstoneRepo.CreateAsync(new Tombstone
                {
                    ArticleId = articleId,
                    CreatedAt = p.DeletedAt,
                    ExpiresAt = p.DeletedAt.AddDays(60),
                    LamportTs = evt.LamportTs,
                    SourceNodeId = evt.NodeId
                });
                await articleRepo.SetDeleteVersionAsync(articleId, arriving);
            }
            return;
        }

        // LWW check: only delete + tombstone if incoming event wins over existing state.
        // A stale delete that loses LWW must NOT create a tombstone — otherwise it would
        // block recreation of an article ID that was never actually deleted (60-day TTL).
        if (!ConflictResolver.IncomingWins(
                RowVersion.Of(existing.LamportTs, existing.SourceNodeId),
                new RowVersion(evt.LamportTs, evt.NodeId)))
            return;

        // H5: tombstone BEFORE soft-delete, not after. TombstoneRepository.CreateAsync is an
        // idempotent LWW upsert (ON CONFLICT ... WHERE excluded.lamport_ts > ...), safe to call
        // more than once. With the OLD order (soft-delete then tombstone) a crash in between left
        // the article permanently Status='D' with no tombstone ever written: the early-return guard
        // above (`existing.Status != "A"`) fires on every retry before the tombstone write is ever
        // reached again, so the gap could never self-heal — and without a tombstone, an
        // out-of-order CREATE for the same id would resurrect the "deleted" article. Writing the
        // (idempotent) tombstone first means a crash before the soft-delete just leaves the article
        // status still 'A', so the retry runs this whole method again from the top and completes it.
        await tombstoneRepo.CreateAsync(new Tombstone
        {
            ArticleId = articleId,
            CreatedAt = p.DeletedAt,
            ExpiresAt = p.DeletedAt.AddDays(60),
            LamportTs = evt.LamportTs,
            SourceNodeId = evt.NodeId
        });
        // The row carries the delete's version, not the last edit's — otherwise a peer update
        // older than this delete still wins ApplyArticleUpdateCoreAsync's comparison and resurrects
        // the article. The tombstone above guards the create path; this guards the update path.
        await articleRepo.SoftDeleteAsync(articleId, new RowVersion(evt.LamportTs, evt.NodeId));
    }
}
