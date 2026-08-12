using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Search.Segment;
using BeeMemoryBank.Storage.Search;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Search;

/// <summary>
/// WP-13 Task 2: "real process kill mid-write, then restart" -- investigates whether
/// <see cref="EncryptedSegmentStore.StoreAsync"/> can leave disk state that a subsequent load
/// mishandles (crashes, or returns truncated/garbage data) after a hard kill mid-write.
///
/// <para>
/// <b>Checked before writing this</b> to confirm these scenarios are not already covered:
/// EncryptedSegmentStoreTests.cs has five "rebuild needed, not throw" tests plus tamper/block-swap/
/// reorder tests, but every single one of them flips or splices bytes WITHIN an otherwise
/// full-length, correctly-sized file -- none of them TRUNCATE a file (chop off its tail), which is
/// the distinct failure shape a genuinely interrupted write produces. That gap is closed by
/// <see cref="TruncatedContainerFile_SimulatingWriteInterruptedMidway_ReturnsRebuildNeededWithoutThrowing"/>
/// and <see cref="ExtremelyTruncatedContainerFile_ShorterThanHeader_ReturnsRebuildNeededWithoutThrowing"/>
/// below. SearchIndexLifecycleIntegrationTests.cs's corrupted-segment test also only bit-flips a
/// byte, for the same reason.
/// </para>
///
/// <para>
/// <b>Finding 1 (already safe by design, verified below):</b>
/// <see cref="EncryptedSegmentStore.StoreAsync"/>'s file write goes through a private
/// <c>WriteFileAtomicAsync</c> helper: write to a randomly-named temp file, then
/// <see cref="File.Move(string, string, bool)"/> the temp file onto the real path. A same-volume
/// <c>File.Move</c> is atomic at the filesystem level (it is a directory-entry rename, not a
/// byte-by-byte copy) -- so a real process kill can only ever land in one of two states: (a) before
/// the rename, leaving the OLD file (if any) fully intact and an extra orphaned temp file, or (b)
/// after the rename, leaving the NEW file fully intact. There is no window in which the FINAL path
/// itself ends up holding a partial write -- a torn write at the path <see cref="EncryptedSegmentStore.LoadAsync"/>
/// actually reads from is structurally impossible, not just unlikely. The truncation tests below
/// still verify the DEFENSIVE side of this (what happens if a truncated file somehow ends up at the
/// final path anyway, e.g. externally corrupted or copied mid-write by some other tool) --
/// <see cref="EncryptedSegmentStore.LoadAsync"/>'s bounds-checked decode handles that gracefully
/// too, but this is defense-in-depth, not a scenario the atomic-rename design leaves open on its
/// own.
/// </para>
///
/// <para>
/// <b>Finding 2 (real gap, found and fixed):</b> while the FINAL path can never be torn, the
/// intermediate TEMP file has no such protection -- a kill between the temp file's write completing
/// and the rename leaves that "*.tmp" file orphaned forever, because the process dies before its
/// own <c>finally</c> cleanup block runs. Nothing previously swept these up (verified: no reference
/// to them exists anywhere outside <c>WriteFileAtomicAsync</c> itself, and
/// <see cref="SegmentManifestRepository"/>'s own doc comment about leaving orphaned <c>.bmesg</c>
/// files after a full rebuild is a different, already-deliberate tradeoff). This was never a
/// correctness bug (an orphaned temp file is never referenced by any manifest row, so it can never
/// be loaded/misread) but was an unbounded disk-space leak across repeated crashes. Fixed in
/// <see cref="EncryptedSegmentStore.StoreAsync"/> with a small, best-effort, age-gated sweep --
/// see <see cref="OrphanedTempFile_OlderThanCleanupThreshold_IsRemovedByNextStore"/> and
/// <see cref="OrphanedTempFile_YoungerThanCleanupThreshold_IsLeftAloneInCaseItIsAConcurrentWriteInFlight"/>.
/// </para>
/// </summary>
public class EncryptedSegmentStoreResilienceTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private SegmentManifestRepository _manifestRepo = null!;
    private SessionService _session = null!;
    private string _segmentsDir = null!;
    private EncryptedSegmentStore _store = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        _factory = DbConnectionFactory.CreateInMemory("enc_seg_resil_" + Guid.NewGuid().ToString("N"));
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        _manifestRepo = new SegmentManifestRepository(_factory);
        _session = new SessionService(new KeySlotRepository(_factory));
        _session.UnlockWithDek(RandomNumberGenerator.GetBytes(32));

        _segmentsDir = Path.Combine(Path.GetTempPath(), "bmb_enc_seg_resil_" + Guid.NewGuid().ToString("N"));
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
            try { Directory.Delete(_segmentsDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
        return Task.CompletedTask;
    }

    private static byte[] BuildRealSegment()
    {
        var docs = Enumerable.Range(0, 5)
            .Select(i => new SegmentDocument(i, Guid.NewGuid(), Guid.NewGuid(), [$"term{i}", "shared"]))
            .ToList();
        return SegmentWriter.Build(docs);
    }

    // ── Truncated-file simulation of an interrupted write ──────────────────────────

    [Fact]
    public async Task TruncatedContainerFile_SimulatingWriteInterruptedMidway_ReturnsRebuildNeededWithoutThrowing()
    {
        var segmentId = Guid.NewGuid();
        await _store.StoreAsync(segmentId, BuildRealSegment(), docCount: 5);

        var manifest = await _manifestRepo.GetManifestAsync(segmentId);
        byte[] fullBytes = await File.ReadAllBytesAsync(manifest!.FilePath);
        fullBytes.Length.Should().BeGreaterThan(EncryptedSegmentFormat.HeaderSize, "test setup should produce more than just a header so truncation is meaningful");

        // Simulate "only the first half of the encrypted segment's bytes made it to disk" -- the
        // brief's literal description of a torn/interrupted write, reproduced directly at the file
        // level regardless of exactly which OS mechanism would produce it.
        byte[] truncated = fullBytes[..(fullBytes.Length / 2)];
        await File.WriteAllBytesAsync(manifest.FilePath, truncated);

        SegmentLoadResult result = await _store.LoadAsync(segmentId);

        result.Success.Should().BeFalse();
        result.SegmentBytes.Should().BeNull("a truncated file must never yield partial/garbage bytes to the caller");
        result.Reason.Should().Be(SegmentRebuildReason.CorruptedBlock, "Decode's own bounds checks (declared block/IV/ciphertext lengths running past the truncated buffer's end) throw CryptographicException, which LoadAsync folds into this same signal as any other corruption");
    }

    [Fact]
    public async Task ExtremelyTruncatedContainerFile_ShorterThanHeader_ReturnsRebuildNeededWithoutThrowing()
    {
        var segmentId = Guid.NewGuid();
        await _store.StoreAsync(segmentId, BuildRealSegment(), docCount: 5);

        var manifest = await _manifestRepo.GetManifestAsync(segmentId);

        // Even more extreme: fewer bytes made it to disk than the fixed 32-byte header itself
        // (e.g. the kill happened before WriteAllBytesAsync's very first OS-level flush). This
        // exercises TryParseHeader's `data.Length < HeaderSize` branch specifically.
        byte[] tinyFragment = (await File.ReadAllBytesAsync(manifest!.FilePath))[..10];
        await File.WriteAllBytesAsync(manifest.FilePath, tinyFragment);

        SegmentLoadResult result = await _store.LoadAsync(segmentId);

        result.Success.Should().BeFalse();
        result.SegmentBytes.Should().BeNull();
        result.Reason.Should().Be(SegmentRebuildReason.CorruptedBlock, "TryParseHeader returning false (buffer shorter than the fixed header) must fold into the same graceful signal, not a raw exception");
    }

    // ── Orphaned temp file from a kill between temp-write and rename ───────────────

    [Fact]
    public async Task OrphanedTempFile_OlderThanCleanupThreshold_IsRemovedByNextStore()
    {
        Directory.CreateDirectory(_segmentsDir);

        // Reproduces exactly what WriteFileAtomicAsync's temp file naming produces
        // ("{finalPath}.{guid}.tmp"), backdated to look like it has been sitting there since a
        // crash long ago (rather than being written by a write that is genuinely still in flight).
        string fakeFinalPath = Path.Combine(_segmentsDir, Guid.NewGuid().ToString("N") + ".bmesg");
        string orphanedTempPath = $"{fakeFinalPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(orphanedTempPath, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(orphanedTempPath, DateTime.UtcNow - TimeSpan.FromHours(1));

        // A completely unrelated, legitimate store -- this is what a real subsequent write cycle
        // after the crash looks like; it should incidentally sweep up the old orphan.
        var segmentId = Guid.NewGuid();
        await _store.StoreAsync(segmentId, BuildRealSegment(), docCount: 5);

        File.Exists(orphanedTempPath).Should().BeFalse("an old orphaned temp file from a prior crash must be cleaned up as a side effect of ordinary subsequent writes, not accumulate forever");

        // The real write that triggered the sweep must still have succeeded normally.
        var result = await _store.LoadAsync(segmentId);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task OrphanedTempFile_YoungerThanCleanupThreshold_IsLeftAloneInCaseItIsAConcurrentWriteInFlight()
    {
        Directory.CreateDirectory(_segmentsDir);

        // A temp file with a fresh timestamp -- indistinguishable, from a point-in-time snapshot
        // alone, from one a concurrent StoreAsync call is actively still writing right now. The
        // cleanup sweep must not delete out from under a genuinely in-flight write.
        string fakeFinalPath = Path.Combine(_segmentsDir, Guid.NewGuid().ToString("N") + ".bmesg");
        string freshTempPath = $"{fakeFinalPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(freshTempPath, [1, 2, 3, 4]);

        var segmentId = Guid.NewGuid();
        await _store.StoreAsync(segmentId, BuildRealSegment(), docCount: 5);

        File.Exists(freshTempPath).Should().BeTrue("a recently-modified temp file must not be swept up -- it could belong to a write that is still genuinely in progress");
    }
}
