using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Search.Indexing;
using BeeMemoryBank.Search.Segment;
using BeeMemoryBank.Storage.Search;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Search;

/// <summary>
/// WP-11: the integration glue between three already-merged pieces --
/// <see cref="BeeMemoryBank.Search.Segment"/>'s segment format,
/// <see cref="BeeMemoryBank.Search.Indexing.IndexBuilder"/>'s LSM-lite lifecycle, and
/// <see cref="EncryptedSegmentStore"/>'s encrypted-at-rest persistence. Owns the unlock warm-start
/// flow (reload every persisted segment back into a live <see cref="IndexBuilder"/>, or fall back
/// to a full rebuild) and the tombstone-durability plumbing (Gap 2 from wp-11.md): every place
/// <see cref="IndexBuilder"/> tombstones a sealed segment in memory needs a corresponding durable
/// write for segments that are actually persisted.
///
/// <para>
/// Scoped (not singleton): its dependencies (repositories, <see cref="EncryptedSegmentStore"/>) are
/// themselves scoped, following the same per-cycle-scope pattern
/// <see cref="PendingIndexProcessor"/> (and its sibling <c>PendingEmbeddingProcessor</c>) already
/// use. The state that genuinely needs to survive across cycles/scopes --
/// <see cref="Indexing.IndexBuilder"/> itself and the internal-id-to-persisted-Guid map -- lives in
/// singletons (<see cref="IndexBuilder"/>, <see cref="SearchIndexRuntimeState"/>) injected here.
/// </para>
/// </summary>
public sealed class SearchIndexLifecycleService(
    IndexBuilder builder,
    SearchIndexRuntimeState runtimeState,
    SegmentManifestRepository manifestRepo,
    EncryptedSegmentStore segmentStore,
    SegmentTombstoneRepository tombstoneRepo,
    IArticleRepository articleRepo,
    ILogger<SearchIndexLifecycleService> logger)
{
    /// <summary>The process-wide <see cref="IndexBuilder"/> singleton this service manages.</summary>
    public IndexBuilder Builder { get; } = builder;

    /// <summary>
    /// Unlock warm-start (see wp-11.md Task 4). Idempotent per process: the first call to actually
    /// run this (across every <see cref="PendingIndexProcessor"/> cycle, since it is invoked
    /// unconditionally every cycle just like
    /// <c>EmbeddingProjectionService.EnsureProjectionMatrixAsync</c>) enumerates the current
    /// manifest and attempts to load every recorded segment:
    /// <list type="bullet">
    /// <item><description>
    /// If every segment loads successfully, each is reconstructed (Gap 1's fix,
    /// <see cref="SegmentReader.EnumerateTerms"/> + <see cref="IndexBuilder.AdoptPersistedSegment"/>)
    /// and adopted into <see cref="Builder"/> along with its durably-persisted tombstones (Gap 2's
    /// fix), so previously-indexed content is immediately findable without waiting for a reindex.
    /// </description></item>
    /// <item><description>
    /// If ANY segment fails to load (missing, corrupted, wrong version, stale dek_epoch -- any
    /// <see cref="SegmentRebuildReason"/>), no segment is adopted at all -- this deliberately does
    /// NOT attempt partial recovery. See <see cref="TriggerFullRebuildAsync"/> and wp-11.md's
    /// "full-rebuild-on-any-failure" tradeoff.
    /// </description></item>
    /// </list>
    /// </summary>
    public async Task EnsureWarmStartedAsync(CancellationToken ct = default)
    {
        if (!runtimeState.TryBeginWarmStart())
        {
            return;
        }

        List<SegmentManifestEntry> manifestEntries;
        try
        {
            manifestEntries = await manifestRepo.GetAllManifestsAsync();
        }
        catch (Exception ex)
        {
            // Can't even enumerate the manifest -- be as conservative as an actual load failure
            // rather than leaving the index silently empty and unindexed forever.
            logger.LogError(ex, "Warm-start: failed to enumerate the search index manifest; triggering a full rebuild.");
            await TriggerFullRebuildAsync(ct);
            return;
        }

        if (manifestEntries.Count == 0)
        {
            // Normal for a brand-new vault, or right after a prior rebuild cleared the manifest --
            // PendingIndexProcessor will build the index from scratch via index_pending articles.
            return;
        }

        var loaded = new List<(SegmentManifestEntry Manifest, byte[] Bytes)>(manifestEntries.Count);
        foreach (SegmentManifestEntry manifest in manifestEntries)
        {
            SegmentLoadResult result = await segmentStore.LoadAsync(manifest.SegmentId);
            if (!result.Success)
            {
                logger.LogWarning(
                    "Warm-start: segment {SegmentId} failed to load ({Reason}); treating the whole persisted search index as untrustworthy and triggering a full rebuild instead of a partial recovery.",
                    manifest.SegmentId, result.Reason);
                await TriggerFullRebuildAsync(ct);
                return;
            }

            loaded.Add((manifest, result.SegmentBytes!));
        }

        foreach ((SegmentManifestEntry manifest, byte[] bytes) in loaded)
        {
            try
            {
                // SegmentReader's constructor only validates the fixed-size header (magic, format
                // version, declared doc/term counts) -- it does not touch the doc table or term
                // dictionary. Actual payload parsing is deferred and lazy: AdoptPersistedSegment
                // (via EnumerateTerms/GetPostings/GetDocument) is what walks the real byte offsets,
                // so a segment with a valid header but corrupted body (truncated postings, an
                // out-of-bounds text/postings offset, a doc count that no longer matches the real
                // table) only throws once THAT runs -- an independent finding from an adversarial
                // review (2026-08-12) of this same fix, confirmed by reading SegmentReader.cs: those
                // methods raise plain framework exceptions (ArgumentOutOfRangeException from
                // Span.Slice, etc.), not a specific, easily-filtered type. Both steps must therefore
                // share one try/catch and one broad `catch (Exception)` -- this is a trust boundary
                // for externally-persisted, potentially-corrupted binary data (the same "any load
                // failure means the whole persisted index is untrustworthy" reasoning the
                // SegmentLoadResult check above already applies), not a place where narrowing the
                // catch would protect against masking a real programming bug.
                var reader = new SegmentReader(bytes);
                HashSet<Guid> tombstones = await tombstoneRepo.GetForSegmentAsync(manifest.SegmentId);
                int mergeCountBeforeAdopt = Builder.MergeCount;
                int internalId = Builder.AdoptPersistedSegment(reader, tombstones);
                runtimeState.RegisterPersistedSegment(internalId, manifest.SegmentId);

                // WP-19: AdoptPersistedSegment ends with its own MaybeMergeLocked call (same as a
                // fresh seal), so folding this one segment in on top of whatever was already
                // adopted so far can itself cross a merge threshold. Persisting that merge's output
                // immediately -- right here, per segment -- rather than waiting until this whole
                // loop finishes matters whenever the manifest holds more un-merged segments than a
                // single merge threshold's worth (the expected shape of the very first warm-start
                // after upgrading to this WP, against a vault whose manifest accumulated many
                // never-persisted historical merges under the old, buggy behavior): adopting could
                // then trigger SEVERAL merges back to back across this one loop, and IndexBuilder's
                // "only remembers the LAST merge" contract (see
                // GetMostRecentlyMergedSegmentForPersistence's own doc comment) would otherwise
                // silently lose every merge except the final one -- stranding the earlier merges'
                // now-superseded inputs in the manifest, which is exactly the "same article live in
                // more than one sealed segment" state this whole WP exists to eliminate. Checking
                // after every individual AdoptPersistedSegment call (which, like AddOrUpdateDocument,
                // can trigger at most one merge per call) guarantees none of them is ever missed,
                // and also means a merge that folds in THIS segment's own just-registered internal
                // id (adopted moments ago, immediately above) is retired correctly too -- its mapping
                // is already registered by the time this check runs.
                if (Builder.MergeCount > mergeCountBeforeAdopt)
                {
                    await PersistMostRecentlyMergedSegmentAsync(ct);
                }
            }
            catch (Exception ex)
            {
                // Same "untrustworthy as a whole" treatment as a SegmentLoadResult failure above --
                // this is the plaintext payload's OWN inner format being unreadable or corrupted
                // (e.g. written by a newer node version, or damaged on disk), a failure mode
                // EncryptedSegmentStore.LoadAsync cannot see since it only validates the outer
                // encrypted container. See WP-13's SearchIndexLifecycleFormatVersionResilienceTests
                // for the format-version scenario this originally closed.
                logger.LogWarning(
                    ex,
                    "Warm-start: segment {SegmentId} could not be read or adopted; treating the whole persisted search index as untrustworthy and triggering a full rebuild instead of a partial recovery.",
                    manifest.SegmentId);
                await TriggerFullRebuildAsync(ct);
                return;
            }
        }

        logger.LogInformation("Search index warm-started from {Count} persisted segment(s).", loaded.Count);
    }

    /// <summary>
    /// Persists the segment <see cref="Builder"/> most recently sealed, if any, and records its
    /// internal-id-to-persisted-Guid mapping so future tombstones against it can be durably
    /// written. Callers detect "a seal just happened" by comparing <see cref="IndexBuilder.SealCount"/>
    /// before/after an <see cref="IndexBuilder.AddOrUpdateDocument"/> call (see
    /// <see cref="PendingIndexProcessor"/>).
    /// </summary>
    public async Task PersistMostRecentlySealedSegmentAsync(CancellationToken ct = default)
    {
        SealedSegmentPersistenceInfo? sealedInfo = Builder.GetMostRecentlySealedSegmentForPersistence();
        if (sealedInfo is null)
        {
            return;
        }

        var persistedId = Guid.NewGuid();
        await segmentStore.StoreAsync(persistedId, sealedInfo.Value.Bytes, sealedInfo.Value.DocumentCount);
        runtimeState.RegisterPersistedSegment(sealedInfo.Value.SegmentId, persistedId);
    }

    /// <summary>
    /// Gap 2's fix: for every <see cref="SegmentTombstoneEvent"/> IndexBuilder just reported (from
    /// an <see cref="IndexBuilder.AddOrUpdateDocument"/> or <see cref="IndexBuilder.RemoveDocument"/>
    /// call), durably records the tombstone IF that segment has a known persisted Guid. A segment
    /// with no known mapping is skipped: there is no on-disk file to write a tombstone row against.
    ///
    /// <para>
    /// <b>WP-19 update:</b> "no known mapping" used to mean, in practice, ANY merge output -- WP-11
    /// persisted fresh seals but never a merge's output, so a merge-output segment (and therefore
    /// every tombstone against it) had no durable counterpart at all, and every restart re-adopted
    /// and re-merged the same un-collapsed seals from scratch (the very bug this WP fixes). Since a
    /// merge's output is now durably persisted too (see <see cref="PersistMostRecentlyMergedSegmentAsync"/>),
    /// this skip is no longer a standing gap for merge outputs specifically -- it now only fires for
    /// the genuinely transient case the rest of this doc comment already describes: an actual
    /// failure/crash between an in-memory tombstone (or merge) and its corresponding durable write
    /// finishing. Losing that one durable write means one stale result could transiently reappear
    /// after a restart, until the next full rebuild -- never silent data corruption, since the
    /// tombstone is a filter, not the source of truth for content.
    /// </para>
    /// </summary>
    public async Task PersistTombstonesAsync(IReadOnlyList<SegmentTombstoneEvent> events, CancellationToken ct = default)
    {
        foreach (SegmentTombstoneEvent evt in events)
        {
            if (runtimeState.TryGetPersistedSegmentId(evt.SegmentId, out Guid persistedSegmentId))
            {
                await tombstoneRepo.AddAsync(persistedSegmentId, evt.ArticleId);
            }
        }
    }

    /// <summary>
    /// WP-19: persists the segment <see cref="Builder"/> most recently merged, if any, replacing its
    /// consumed inputs' on-disk manifest/tombstone rows with the merge's output in one atomic
    /// database transaction -- see <see cref="SegmentManifestRepository.ReplaceMergedSegmentsAsync"/>
    /// for why that transaction is the crash-safety-critical piece. Callers detect "a merge just
    /// happened" the same way <see cref="PersistMostRecentlySealedSegmentAsync"/>'s callers detect a
    /// seal: comparing <see cref="IndexBuilder.MergeCount"/> before/after a call that can trigger one
    /// (<see cref="IndexBuilder.AddOrUpdateDocument"/> in <see cref="PendingIndexProcessor"/>, or
    /// <see cref="IndexBuilder.AdoptPersistedSegment"/> in <see cref="EnsureWarmStartedAsync"/>).
    ///
    /// <para>
    /// <b>Crash-safety ordering, chosen deliberately -- and why not another order:</b>
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Write the merged segment's bytes to a brand-new file</b> (a fresh persisted Guid nothing
    /// references yet), via <see cref="EncryptedSegmentStore.StoreMergedSegmentFileAsync"/>'s own
    /// atomic temp-file-then-rename. No manifest row points at this file until step 2 commits.
    /// </description></item>
    /// <item><description>
    /// <b>Commit ONE database transaction</b> (<see cref="SegmentManifestRepository.ReplaceMergedSegmentsAsync"/>)
    /// that both inserts the new file's manifest row AND deletes every consumed input's manifest and
    /// tombstone rows.
    /// </description></item>
    /// <item><description>
    /// <b>Only after that transaction has durably committed</b>, best-effort-delete the consumed
    /// inputs' now-unreferenced files from disk.
    /// </description></item>
    /// </list>
    /// <para>
    /// A crash between steps 1 and 2 leaves the new file an unreferenced orphan and every OLD
    /// segment's manifest/tombstone rows fully intact -- the next warm-start simply reloads the
    /// pre-merge segments exactly as it would have before this WP existed, and the merge is retried
    /// next time its threshold trips. No document is lost, and no article ends up live in more than
    /// one persisted segment. A crash between steps 2 and 3 leaves the manifest already correctly
    /// reflecting the merged-only state (the old rows are gone, durably), so warm-start correctly
    /// adopts only the new merged segment; the old files are just wasted, unreferenced disk space --
    /// the same category of harmless leftover <see cref="EncryptedSegmentStore"/>'s own orphaned-
    /// temp-file sweep already tolerates elsewhere in this WP's surrounding code.
    /// </para>
    /// <para>
    /// The ordering this deliberately rules out is deleting the old rows/files FIRST and writing the
    /// new file after: a crash in that window would leave the manifest with rows pointing at
    /// now-missing files for segments that no longer physically exist anywhere (deleted, and the
    /// replacement never got written), which <see cref="EncryptedSegmentStore.LoadAsync"/> already
    /// turns into <see cref="SegmentRebuildReason.FileMissing"/> -- and per
    /// <see cref="EnsureWarmStartedAsync"/>'s own "any single load failure means the whole persisted
    /// index is untrustworthy" policy, that forces a full rebuild of the ENTIRE vault on the very
    /// next restart. That full-rebuild-on-every-crash outcome is exactly the wall this WP exists to
    /// remove, so this ordering must never be reversed.
    /// </para>
    /// </summary>
    public async Task PersistMostRecentlyMergedSegmentAsync(CancellationToken ct = default)
    {
        MergedSegmentPersistenceInfo? mergedInfo = Builder.GetMostRecentlyMergedSegmentForPersistence();
        if (mergedInfo is null)
        {
            return;
        }

        // Resolve which of the merge's consumed inputs actually correspond to a persisted on-disk
        // segment. An internal id with no known mapping (e.g. an earlier, still-un-persisted merge
        // output later folded into THIS merge before its own persistence call ever ran -- see
        // IndexBuilder's residual-gap note) is safely skipped, same "unknown id, nothing to do"
        // tolerance PersistTombstonesAsync already applies.
        var retiredPersistedIds = new List<Guid>();
        foreach (int internalId in mergedInfo.Value.ReplacedSegmentIds)
        {
            if (runtimeState.TryGetPersistedSegmentId(internalId, out Guid persistedSegmentId))
            {
                retiredPersistedIds.Add(persistedSegmentId);
            }
        }

        SegmentManifestEntry? newManifestEntry = null;
        int? newInternalSegmentId = null;
        Guid newPersistedId = default;

        if (mergedInfo.Value.NewSegment is { } newSegment)
        {
            // Step 1 (see this method's own doc comment): the new file, durably written, BEFORE
            // anything references it from the database.
            newPersistedId = Guid.NewGuid();
            (string filePath, int dekEpoch) = await segmentStore.StoreMergedSegmentFileAsync(newPersistedId, newSegment.Bytes);

            newManifestEntry = new SegmentManifestEntry
            {
                SegmentId = newPersistedId,
                FilePath = filePath,
                DocCount = newSegment.DocumentCount,
                DekEpoch = dekEpoch,
                FormatVersion = EncryptedSegmentFormat.FormatVersion,
                CreatedAt = DateTime.UtcNow,
            };
            newInternalSegmentId = newSegment.SegmentId;
        }

        // Step 2: one atomic transaction installs the new row (if any) and retires every consumed
        // input's rows across both tables.
        List<string> retiredFilePaths = await manifestRepo.ReplaceMergedSegmentsAsync(newManifestEntry, retiredPersistedIds);

        // Step 3: only now, after step 2's transaction has durably committed, sweep the
        // now-unreferenced old files. Best-effort/non-fatal for the same reason
        // EncryptedSegmentStore's own orphaned-temp-file cleanup is: correctness never depended on
        // this succeeding, only on step 2 having already committed.
        foreach (string filePath in retiredFilePaths)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
                // Best-effort: a leftover, now-unreferenced segment file is wasted disk space, never
                // a correctness problem -- the manifest transaction that made it unreferenced has
                // already committed regardless of whether this delete succeeds.
            }
        }

        if (newInternalSegmentId is int newId)
        {
            runtimeState.RegisterPersistedSegment(newId, newPersistedId);
        }

        // Whatever internal ids this merge consumed no longer name a live segment inside
        // IndexBuilder -- their on-disk rows are gone too now, so drop their internal-id ->
        // persisted-Guid mappings to avoid an unbounded (if slow) memory leak across a long-running
        // process's lifetime of repeated merges. Safe even for ids that were never registered (e.g.
        // the residual-gap case above) -- see RemovePersistedSegment's own doc comment.
        foreach (int internalId in mergedInfo.Value.ReplacedSegmentIds)
        {
            runtimeState.RemovePersistedSegment(internalId);
        }
    }

    /// <summary>
    /// wp-11.md Task 4/5: treats the whole persisted search index as no longer trustworthy --
    /// clears the manifest and tombstone tables and re-flags every active article as
    /// index_pending, so <see cref="PendingIndexProcessor"/> reindexes from scratch in the
    /// background. Deliberately conservative: a single bad segment costs a full rebuild rather than
    /// attempting to reconstruct "which specific articles were in the one segment that failed,"
    /// which nothing currently tracks -- see wp-11-report.md for the full tradeoff discussion.
    ///
    /// <para>
    /// Coordinated by <see cref="SearchIndexRuntimeState.RebuildLock"/> (a plain
    /// <see cref="SemaphoreSlim"/>, not the Api-layer <c>HeavyOperationLock</c> -- see that type's
    /// doc comment) so two overlapping triggers (e.g. two segments both failing to load in the same
    /// warm-start pass) collapse into a single rebuild rather than each independently re-flagging
    /// every article and clearing tables that the other just cleared.
    /// </para>
    /// </summary>
    public async Task TriggerFullRebuildAsync(CancellationToken ct = default)
    {
        await runtimeState.RebuildLock.WaitAsync(ct);
        try
        {
            logger.LogWarning("Triggering a full search index rebuild: clearing the persisted manifest/tombstones and re-flagging every active article as index-pending.");
            int affected = await articleRepo.MarkAllIndexPendingUnscopedAsync();
            await tombstoneRepo.DeleteAllAsync();
            await manifestRepo.DeleteAllManifestsAsync();
            runtimeState.ClearPersistedSegmentIds();
            logger.LogWarning("Full search index rebuild triggered: {Count} active article(s) re-flagged as index-pending.", affected);
        }
        finally
        {
            runtimeState.RebuildLock.Release();
        }
    }
}
