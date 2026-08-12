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
/// Finding from an independent adversarial review (2026-08-12, via an AGY/Gemini agent, of the
/// warm-start fix in <see cref="SearchIndexLifecycleFormatVersionResilienceTests"/>): that fix only
/// wrapped <c>new SegmentReader(bytes)</c> in a try/catch. <see cref="SegmentReader"/>'s
/// constructor validates only the fixed-size header (magic bytes, format version, declared doc/
/// term counts) -- it never touches the doc table or term dictionary. Actual payload parsing is
/// deferred and lazy: <see cref="IndexBuilder.AdoptPersistedSegment"/> (via
/// <see cref="SegmentReader.EnumerateTerms"/>/<see cref="SegmentReader.GetDocument"/>) is what
/// walks the real byte offsets, and that call sat OUTSIDE the try/catch. A segment with a valid
/// header but a corrupted body (e.g. a doc count field that no longer matches the real doc table,
/// from partial disk corruption after the header was already written) throws a plain
/// <see cref="ArgumentOutOfRangeException"/> (from <see cref="Span{T}.Slice(int, int)"/> going out
/// of bounds inside <see cref="SegmentReader.GetDocument"/>) from OUTSIDE the try/catch, crashing
/// warm-start instead of triggering the same graceful full rebuild every other corruption signal
/// gets.
///
/// <para>
/// <b>Checked before writing this</b> to confirm the gap wasn't already covered:
/// <see cref="SearchIndexLifecycleFormatVersionResilienceTests"/> only exercises a bad HEADER field
/// (format version), which the constructor itself catches; this test corrupts the DOC COUNT field
/// instead, which the constructor reads but does not validate against the segment's actual size --
/// a failure point one layer deeper, inside <see cref="IndexBuilder.AdoptPersistedSegment"/>, not
/// <see cref="SegmentReader"/>'s constructor.
/// </para>
///
/// <para>
/// <b>Fix:</b> <see cref="SearchIndexLifecycleService.EnsureWarmStartedAsync"/> now wraps both the
/// <c>new SegmentReader(bytes)</c> call AND the <see cref="IndexBuilder.AdoptPersistedSegment"/>
/// call in one try/catch with a broad <c>catch (Exception)</c> -- a deliberately wide catch, since
/// this is a trust boundary for externally-persisted, potentially-corrupted binary data (the same
/// "any load failure means the whole persisted index is untrustworthy" reasoning the
/// <see cref="SegmentLoadResult"/> check earlier in the same method already applies), not a place
/// where narrowing the catch would protect against masking a real programming bug.
/// </para>
/// </summary>
public class SearchIndexLifecycleCorruptedPayloadResilienceTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private SegmentManifestRepository _manifestRepo = null!;
    private SessionService _session = null!;
    private string _segmentsDir = null!;
    private EncryptedSegmentStore _store = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        _factory = DbConnectionFactory.CreateInMemory("corrupt_payload_" + Guid.NewGuid().ToString("N"));
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        _manifestRepo = new SegmentManifestRepository(_factory);
        _session = new SessionService(new KeySlotRepository(_factory));
        _session.UnlockWithDek(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        _segmentsDir = Path.Combine(Path.GetTempPath(), "bmb_corrupt_payload_" + Guid.NewGuid().ToString("N"));
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
    public async Task EnsureWarmStartedAsync_PersistedSegmentWithCorruptedDocCount_TriggersFullRebuildInsteadOfThrowing()
    {
        // A real, validly-encrypted container with a CURRENT, valid header, whose declared doc
        // count no longer matches the real doc table -- e.g. the tail of the segment (containing
        // the actual document records) was truncated or damaged after the header was written. The
        // header itself (magic, format version) is untouched and current, so both
        // EncryptedSegmentStore.LoadAsync and SegmentReader's constructor accept it without
        // complaint; the failure only surfaces once something actually reads document 0.
        byte[] segmentBytes = SegmentWriter.Build(
        [
            new SegmentDocument(0, Guid.NewGuid(), Guid.NewGuid(), ["realterm"]),
        ]);
        BinaryPrimitives.WriteInt32LittleEndian(
            segmentBytes.AsSpan(SegmentLayout.HeaderDocCountOffset, 4), 1_000_000);

        var segmentId = Guid.NewGuid();
        await _store.StoreAsync(segmentId, segmentBytes, docCount: 1);

        // Sanity: the container decrypts fine, and the header alone (format version, magic) still
        // parses -- SegmentReader's constructor never inspects the doc table itself.
        SegmentLoadResult loadResult = await _store.LoadAsync(segmentId);
        loadResult.Success.Should().BeTrue("the encrypted container and the segment's fixed-size header are both well-formed -- only the doc table is now inconsistent with the declared count");

        // Sanity: doc id 0 is always valid regardless of the corrupted count (the one real document
        // this segment was built with is still physically there) -- the corruption only manifests
        // once AdoptPersistedSegment's iteration reaches a doc id past the real doc table, which the
        // declared (corrupted) count of 1,000,000 lets it try to do.
        var reader = new SegmentReader(loadResult.SegmentBytes!);
        var probe = () => reader.GetDocument(999_999);
        probe.Should().Throw<ArgumentOutOfRangeException>(
            "sanity check: reading a doc id past the real doc table (reachable because of the corrupted declared count) really does throw a plain framework exception, confirming this test reproduces the gap rather than something already guarded elsewhere");

        var tombstoneRepo = new SegmentTombstoneRepository(_factory);
        var articleRepo = new ArticleRepository(_factory, new CallerScopeHolder());
        var builder = new IndexBuilder();
        var runtimeState = new SearchIndexRuntimeState();
        var lifecycle = new SearchIndexLifecycleService(
            builder, runtimeState, _manifestRepo, _store, tombstoneRepo, articleRepo,
            NullLogger<SearchIndexLifecycleService>.Instance);

        Func<Task> warmStart = () => lifecycle.EnsureWarmStartedAsync(CancellationToken.None);

        await warmStart.Should().NotThrowAsync(
            "SearchIndexLifecycleService.EnsureWarmStartedAsync must catch AdoptPersistedSegment's failure on a corrupted-but-header-valid segment and trigger a full rebuild, not crash warm-start");

        (await _manifestRepo.GetAllManifestsAsync()).Should().BeEmpty(
            "TriggerFullRebuildAsync clears the manifest so PendingIndexProcessor reindexes from scratch, same as every other rebuild-triggering failure");
    }
}
