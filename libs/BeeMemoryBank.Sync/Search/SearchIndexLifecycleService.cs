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
            SegmentReader reader;
            try
            {
                reader = new SegmentReader(bytes);
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                // Same "untrustworthy as a whole" treatment as a SegmentLoadResult failure above --
                // this is the plaintext payload's OWN inner format (SegmentLayout.FormatVersion)
                // being unreadable (e.g. written by a newer node version), a failure mode
                // EncryptedSegmentStore.LoadAsync cannot see since it only validates the outer
                // encrypted container. See WP-13's
                // SearchIndexLifecycleFormatVersionResilienceTests for the scenario this closes.
                logger.LogWarning(
                    ex,
                    "Warm-start: segment {SegmentId} has an unreadable inner format; treating the whole persisted search index as untrustworthy and triggering a full rebuild instead of a partial recovery.",
                    manifest.SegmentId);
                await TriggerFullRebuildAsync(ct);
                return;
            }

            HashSet<Guid> tombstones = await tombstoneRepo.GetForSegmentAsync(manifest.SegmentId);
            int internalId = Builder.AdoptPersistedSegment(reader, tombstones);
            runtimeState.RegisterPersistedSegment(internalId, manifest.SegmentId);
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
    /// with no known mapping (e.g. one that only ever existed as a merge output, which this WP does
    /// not persist -- see wp-11-report.md) is skipped: there is no on-disk file to write a
    /// tombstone row against. This is the documented residual gap: losing this durable write (e.g.
    /// a crash between the in-memory tombstone and this call) means one stale result could
    /// transiently reappear after a restart, until the next full rebuild -- never silent data
    /// corruption, since the tombstone is a filter, not the source of truth for content.
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
            int affected = await articleRepo.MarkAllIndexPendingAsync();
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
