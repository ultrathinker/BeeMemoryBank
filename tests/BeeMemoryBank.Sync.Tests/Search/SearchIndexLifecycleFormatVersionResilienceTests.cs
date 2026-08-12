using System.Buffers.Binary;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Search.Indexing;
using BeeMemoryBank.Search.Segment;
using BeeMemoryBank.Storage.Search;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Sync.Tests.Search;

/// <summary>
/// WP-13 Task 1: the system-level half of the "format-version-from-the-future" investigation (the
/// low-level half lives in BeeMemoryBank.Search.Tests.Segment.SegmentReaderFormatVersionTests and
/// BeeMemoryBank.Storage.Tests.Search.EncryptedSegmentFormatVersionTests).
///
/// <para>
/// <b>Checked before writing this</b> to confirm the gap below is not already covered:
/// SearchIndexLifecycleIntegrationTests.cs's
/// <c>CorruptedSegmentFile_TriggersFullRebuild_ResetsIndexPendingInsteadOfCrashingOrPartialIndex</c>
/// tampers a byte inside a sealed segment's CIPHERTEXT (so decryption itself fails, which
/// <see cref="EncryptedSegmentStore.LoadAsync"/> already turns into
/// <see cref="SegmentRebuildReason.CorruptedBlock"/>, a documented "rebuild needed" signal) --
/// that is a completely different failure point from this test, which tampers the INNER "BMBI"
/// format version field INSIDE the (still successfully decryptable) plaintext, a byte range
/// <see cref="EncryptedSegmentStore.LoadAsync"/> never inspects at all (it only checks the OUTER
/// container's own format version, a separate field entirely -- see
/// EncryptedSegmentStoreTests.Load_WrongFormatVersionInFileHeader_ReturnsRebuildNeededWithoutThrowing
/// for that already-covered, different scenario).
/// </para>
///
/// <para>
/// <b>Finding (real gap, not fixed -- see wp-13-report.md):</b> Storing a segment whose plaintext
/// bytes have a future <see cref="SegmentLayout.FormatVersion"/> (simulating "a newer node version
/// wrote this segment, an older version is now warm-starting from it") decrypts successfully --
/// <see cref="EncryptedSegmentStore.LoadAsync"/> has no way to know the plaintext payload's own
/// internal format changed, since that is a layer entirely below it. But
/// <see cref="SearchIndexLifecycleService.EnsureWarmStartedAsync"/> then constructs a
/// <c>new SegmentReader(bytes)</c> directly over that payload with no try/catch around it -- unlike
/// every one of the five documented <see cref="SegmentRebuildReason"/> signals, which it catches
/// and turns into a graceful <see cref="SearchIndexLifecycleService.TriggerFullRebuildAsync"/> call,
/// this specific failure mode (a <see cref="NotSupportedException"/> from a too-new inner segment
/// format) propagates straight out of <c>EnsureWarmStartedAsync</c> uncaught. In production that
/// means the entire <c>PendingIndexProcessor</c> cycle faults every single time it runs (see
/// <c>PendingIndexProcessor.ExecuteAsync</c>'s outer catch, which only logs and retries next
/// interval -- the same bad segment is retried and re-throws every cycle forever) instead of
/// self-healing via a full rebuild the way a corrupted/wrong-container-version segment already
/// does.
/// </para>
///
/// <para>
/// <b>Not fixed here:</b> the fix belongs in <c>SearchIndexLifecycleService.cs</c>
/// (BeeMemoryBank.Sync), which is outside wp-13.md's enumerated list of files this WP is allowed to
/// modify (<c>SegmentReader.cs</c>, <c>EncryptedSegmentFormat.cs</c>, <c>EncryptedSegmentStore.cs</c>
/// only) -- and it is WP-11's design, not this test-writing WP's, to rework. The recommended fix:
/// wrap the <c>new SegmentReader(bytes)</c> call (and ideally the whole per-manifest reconstruction
/// loop) in a try/catch for <see cref="NotSupportedException"/> (and defensively
/// <see cref="ArgumentException"/>, which the same constructor throws for a too-short/bad-magic
/// buffer) that calls <see cref="SearchIndexLifecycleService.TriggerFullRebuildAsync"/> the same way
/// an <see cref="SegmentLoadResult"/> failure already does.
/// </para>
///
/// <para>
/// This test intentionally pins the CURRENT (undesirable) behavior so it stays documented and
/// green rather than silently regressing further; it is not an endorsement of the crash.
/// </para>
/// </summary>
public class SearchIndexLifecycleFormatVersionResilienceTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private SegmentManifestRepository _manifestRepo = null!;
    private SessionService _session = null!;
    private string _segmentsDir = null!;
    private EncryptedSegmentStore _store = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        _factory = DbConnectionFactory.CreateInMemory("wp13_fmt_" + Guid.NewGuid().ToString("N"));
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        _manifestRepo = new SegmentManifestRepository(_factory);
        _session = new SessionService(new KeySlotRepository(_factory));
        _session.UnlockWithDek(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        _segmentsDir = Path.Combine(Path.GetTempPath(), "bmb_wp13_fmt_" + Guid.NewGuid().ToString("N"));
        _store = new EncryptedSegmentStore(_manifestRepo, _session, _segmentsDir);

        var nodeRepo = new NodeIdentityRepository(_factory);
        await nodeRepo.CreateAsync(new NodeIdentity
        {
            NodeId = Guid.NewGuid(),
            DisplayName = "test-node",
            Ed25519PublicKey = new byte[32],
            Ed25519PrivateKey = new byte[32],
            Ed25519PrivateKeyIV = null,
            Ed25519PrivateKeyV = 0,
            CanGenerateEmbeddings = false,
            InitialSyncCompleted = true,
            CreatedAt = DateTime.UtcNow,
        });
    }

    public Task DisposeAsync()
    {
        _session.Lock();
        _factory.Dispose();
        if (Directory.Exists(_segmentsDir))
        {
            try { Directory.Delete(_segmentsDir, recursive: true); } catch { /* best-effort */ }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task EnsureWarmStartedAsync_PersistedSegmentWithFutureInnerFormatVersion_CurrentlyThrowsInsteadOfTriggeringRebuild()
    {
        // A real, validly-encrypted container whose DECRYPTED payload claims a "BMBI" format
        // version from the future -- exactly what a newer node's real persisted segment would look
        // like to this older build. The OUTER encrypted-container format (EncryptedSegmentFormat)
        // is untouched and current, so EncryptedSegmentStore.LoadAsync has no reason to reject it.
        byte[] segmentBytes = SegmentWriter.Build(
        [
            new SegmentDocument(0, Guid.NewGuid(), Guid.NewGuid(), ["futureterm"]),
        ]);
        BinaryPrimitives.WriteInt32LittleEndian(
            segmentBytes.AsSpan(SegmentLayout.HeaderFormatVersionOffset, 4), SegmentLayout.FormatVersion + 1);

        var segmentId = Guid.NewGuid();
        await _store.StoreAsync(segmentId, segmentBytes, docCount: 1);

        // Sanity: the container decrypts fine at EncryptedSegmentStore's own layer -- the failure
        // this test is about only happens one layer above, at SegmentReader construction.
        SegmentLoadResult loadResult = await _store.LoadAsync(segmentId);
        loadResult.Success.Should().BeTrue("the encrypted container itself is well-formed and current -- only the payload's OWN inner format is from the future, which LoadAsync cannot see");

        var tombstoneRepo = new SegmentTombstoneRepository(_factory);
        var articleRepo = new ArticleRepository(_factory, new CallerScopeHolder());
        var builder = new IndexBuilder();
        var runtimeState = new SearchIndexRuntimeState();
        var lifecycle = new SearchIndexLifecycleService(
            builder, runtimeState, _manifestRepo, _store, tombstoneRepo, articleRepo,
            NullLogger<SearchIndexLifecycleService>.Instance);

        Func<Task> warmStart = () => lifecycle.EnsureWarmStartedAsync(CancellationToken.None);

        // THE FINDING: unlike every documented SegmentRebuildReason (missing/corrupted/wrong
        // container version/stale epoch), this failure mode is NOT caught -- it propagates as a
        // raw NotSupportedException instead of resolving to a graceful full rebuild. See this
        // class's doc comment for the precise gap and why it is reported, not fixed, in this WP.
        await warmStart.Should().ThrowAsync<NotSupportedException>(
            "current behavior: SearchIndexLifecycleService.EnsureWarmStartedAsync does not catch SegmentReader's rejection of a future inner format version -- this is WP-13's documented finding, not desired behavior");

        // Confirms this really did crash rather than quietly self-heal: the graceful path this WP
        // compares against always clears the manifest before returning (see
        // SearchIndexLifecycleIntegrationTests.CorruptedSegmentFile_TriggersFullRebuild...). Here it
        // does not, because TriggerFullRebuildAsync was never reached.
        (await _manifestRepo.GetAllManifestsAsync()).Should().NotBeEmpty(
            "proof the full-rebuild fallback every other failure mode gets was never reached for this one");
    }
}
