using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Search.Segment;
using BeeMemoryBank.Storage.Search;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests.Search;

/// <summary>
/// Covers WP-09's Definition of Done: byte-for-byte roundtrip through a real SegmentReader, all
/// five documented "load should signal rebuild-needed, not throw" cases, and the tamper/block-swap
/// tests proving the per-block AAD binding is doing real cryptographic work (not just present in
/// code but inert).
/// </summary>
public class EncryptedSegmentStoreTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private SegmentManifestRepository _manifestRepo = null!;
    private SessionService _session = null!;
    private string _segmentsDir = null!;
    private EncryptedSegmentStore _store = null!;

    public async Task InitializeAsync()
    {
        // Registers Dapper's Guid/DateTime TEXT-column type handlers. A one-time, static, global
        // registration (see DapperConfig._configured) that AddStorage() always performs in real
        // wiring -- called explicitly here too so this test class doesn't silently depend on some
        // other test class in the assembly having already triggered it first (e.g. when run via
        // `dotnet test --filter` in isolation).
        DapperConfig.Configure();

        _factory = DbConnectionFactory.CreateInMemory("enc_seg_" + Guid.NewGuid().ToString("N"));
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        _manifestRepo = new SegmentManifestRepository(_factory);
        _session = new SessionService(new KeySlotRepository(_factory));
        _session.UnlockWithDek(RandomNumberGenerator.GetBytes(32));

        _segmentsDir = Path.Combine(Path.GetTempPath(), "bmb_enc_seg_tests_" + Guid.NewGuid().ToString("N"));
        _store = new EncryptedSegmentStore(_manifestRepo, _session, _segmentsDir);

        await EnsureNodeIdentityAsync();
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

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private async Task EnsureNodeIdentityAsync()
    {
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

    private async Task SetDekEpochAsync(int epoch)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE tbl_node_identity SET dek_epoch = @epoch", new { epoch });
    }

    private static byte[] BuildRealSegment()
    {
        var docs = Enumerable.Range(0, 5)
            .Select(i => new SegmentDocument(i, Guid.NewGuid(), Guid.NewGuid(), [$"term{i}", "shared"]))
            .ToList();
        return SegmentWriter.Build(docs);
    }

    // ── Roundtrip ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StoreThenLoad_RoundtripsByteForByte_AndIsQueryableBySegmentReader()
    {
        var segmentId = Guid.NewGuid();
        byte[] original = BuildRealSegment();

        await _store.StoreAsync(segmentId, original, docCount: 5);
        var result = await _store.LoadAsync(segmentId);

        result.Success.Should().BeTrue();
        result.SegmentBytes.Should().Equal(original);

        var reader = new SegmentReader(result.SegmentBytes!);
        reader.DocumentCount.Should().Be(5);
        reader.GetPostings("shared").Select(p => p.DocId).Should().Equal(Enumerable.Range(0, 5));
    }

    [Fact]
    public async Task StoreThenLoad_MultiBlockSegment_RoundtripsExactly()
    {
        // Force multiple 64 KiB blocks by building a segment with many terms/postings.
        var docs = Enumerable.Range(0, 500)
            .Select(i => new SegmentDocument(
                i, Guid.NewGuid(), Guid.NewGuid(),
                Enumerable.Range(0, 50).Select(t => $"term{i}_{t}")))
            .ToList();
        byte[] original = SegmentWriter.Build(docs);
        original.Length.Should().BeGreaterThan(EncryptedSegmentFormat.BlockSize * 2, "test setup should exercise more than one block");

        var segmentId = Guid.NewGuid();
        await _store.StoreAsync(segmentId, original, docCount: 500);
        var result = await _store.LoadAsync(segmentId);

        result.Success.Should().BeTrue();
        result.SegmentBytes.Should().Equal(original);
    }

    // ── The five documented "rebuild needed, not throw" cases ──────────────────────

    [Fact]
    public async Task Load_ManifestRowMissing_ReturnsRebuildNeededWithoutThrowing()
    {
        var result = await _store.LoadAsync(Guid.NewGuid());

        result.Success.Should().BeFalse();
        result.Reason.Should().Be(SegmentRebuildReason.ManifestMissing);
        result.SegmentBytes.Should().BeNull();
    }

    [Fact]
    public async Task Load_FileMissing_ReturnsRebuildNeededWithoutThrowing()
    {
        var segmentId = Guid.NewGuid();
        await _store.StoreAsync(segmentId, BuildRealSegment(), docCount: 5);

        var manifest = await _manifestRepo.GetManifestAsync(segmentId);
        File.Delete(manifest!.FilePath);

        var result = await _store.LoadAsync(segmentId);

        result.Success.Should().BeFalse();
        result.Reason.Should().Be(SegmentRebuildReason.FileMissing);
    }

    [Fact]
    public async Task Load_DekEpochMismatch_ReturnsRebuildNeededWithoutThrowing()
    {
        var segmentId = Guid.NewGuid();
        await _store.StoreAsync(segmentId, BuildRealSegment(), docCount: 5);

        // Simulate a master DEK rotation having happened since this segment was written.
        await SetDekEpochAsync(2);

        var result = await _store.LoadAsync(segmentId);

        result.Success.Should().BeFalse();
        result.Reason.Should().Be(SegmentRebuildReason.DekEpochMismatch);
    }

    [Fact]
    public async Task Load_WrongFormatVersionInManifest_ReturnsRebuildNeededWithoutThrowing()
    {
        var segmentId = Guid.NewGuid();
        await _store.StoreAsync(segmentId, BuildRealSegment(), docCount: 5);

        using (var conn = _factory.CreateConnection())
        {
            await conn.ExecuteAsync(
                "UPDATE tbl_search_index_manifest SET format_version = 999 WHERE segment_id = @segmentId",
                new { segmentId = segmentId.ToString() });
        }

        var result = await _store.LoadAsync(segmentId);

        result.Success.Should().BeFalse();
        result.Reason.Should().Be(SegmentRebuildReason.FormatVersionMismatch);
    }

    [Fact]
    public async Task Load_WrongFormatVersionInFileHeader_ReturnsRebuildNeededWithoutThrowing()
    {
        // Manifest says the right version, but the on-disk header itself has been tampered with
        // to claim a different one -- both the manifest and the raw header are checked.
        var segmentId = Guid.NewGuid();
        await _store.StoreAsync(segmentId, BuildRealSegment(), docCount: 5);

        var manifest = await _manifestRepo.GetManifestAsync(segmentId);
        byte[] bytes = await File.ReadAllBytesAsync(manifest!.FilePath);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(EncryptedSegmentFormat.HeaderFormatVersionOffset, 4), 999);
        await File.WriteAllBytesAsync(manifest.FilePath, bytes);

        var result = await _store.LoadAsync(segmentId);

        result.Success.Should().BeFalse();
        result.Reason.Should().Be(SegmentRebuildReason.FormatVersionMismatch);
    }

    [Fact]
    public async Task Load_CorruptedBlock_ReturnsRebuildNeededWithoutThrowing()
    {
        var segmentId = Guid.NewGuid();
        await _store.StoreAsync(segmentId, BuildRealSegment(), docCount: 5);

        var manifest = await _manifestRepo.GetManifestAsync(segmentId);
        byte[] bytes = await File.ReadAllBytesAsync(manifest!.FilePath);
        // Flip one byte well past the header, inside the first block's ciphertext.
        bytes[EncryptedSegmentFormat.HeaderSize + 25] ^= 0xFF;
        await File.WriteAllBytesAsync(manifest.FilePath, bytes);

        var result = await _store.LoadAsync(segmentId);

        result.Success.Should().BeFalse();
        result.Reason.Should().Be(SegmentRebuildReason.CorruptedBlock);
    }

    // ── Tamper / block-swap: prove the AAD binding does real work ──────────────────

    [Fact]
    public async Task Load_TamperedCiphertextByte_DetectedAsCorruptionNotGarbage()
    {
        var segmentId = Guid.NewGuid();
        byte[] original = BuildRealSegment();
        await _store.StoreAsync(segmentId, original, docCount: 5);

        var manifest = await _manifestRepo.GetManifestAsync(segmentId);
        byte[] bytes = await File.ReadAllBytesAsync(manifest!.FilePath);
        int flipOffset = bytes.Length - 5; // inside the last block's ciphertext/tag
        bytes[flipOffset] ^= 0x01;
        await File.WriteAllBytesAsync(manifest.FilePath, bytes);

        var result = await _store.LoadAsync(segmentId);

        result.Success.Should().BeFalse();
        result.Reason.Should().Be(SegmentRebuildReason.CorruptedBlock);
        // Never silently returns tampered-but-plausible bytes.
        result.SegmentBytes.Should().BeNull();
    }

    [Fact]
    public async Task Load_BlockSwappedBetweenTwoSegments_DetectedViaAadMismatch()
    {
        // Two distinct segments, each with more than one block, so a same-index block can be
        // spliced from one file into the other's slot -- same declared IV/ciphertext lengths
        // (both blocks are built from equally-sized plaintext chunks), so only the AAD
        // (segmentId, blockIndex) distinguishes them.
        byte[] SegmentOfSize(int approxBytes)
        {
            var docs = Enumerable.Range(0, approxBytes / 200)
                .Select(i => new SegmentDocument(
                    i, Guid.NewGuid(), Guid.NewGuid(),
                    Enumerable.Range(0, 20).Select(t => $"blk{i}_{t}")))
                .ToList();
            return SegmentWriter.Build(docs);
        }

        var segmentIdA = Guid.NewGuid();
        var segmentIdB = Guid.NewGuid();
        byte[] segmentA = SegmentOfSize(EncryptedSegmentFormat.BlockSize * 3);
        byte[] segmentB = SegmentOfSize(EncryptedSegmentFormat.BlockSize * 3);

        await _store.StoreAsync(segmentIdA, segmentA, docCount: 1);
        await _store.StoreAsync(segmentIdB, segmentB, docCount: 1);

        var manifestA = await _manifestRepo.GetManifestAsync(segmentIdA);
        var manifestB = await _manifestRepo.GetManifestAsync(segmentIdB);

        byte[] fileA = await File.ReadAllBytesAsync(manifestA!.FilePath);
        byte[] fileB = await File.ReadAllBytesAsync(manifestB!.FilePath);

        // Splice A's block 0 (its full length-prefixed [ivLen][iv][ctLen][ct]) into B's block 0
        // slot. Both segments were built the same way, so their first blocks are the same
        // declared plaintext length (BlockSize) and therefore the same encoded length, keeping
        // the rest of B's container framing intact.
        int aBlock0Start = EncryptedSegmentFormat.HeaderSize;
        int aBlock0Len = BlockEncodedLength(fileA, aBlock0Start);
        int bBlock0Len = BlockEncodedLength(fileB, EncryptedSegmentFormat.HeaderSize);
        aBlock0Len.Should().Be(bBlock0Len, "both segments' first block should be a full BlockSize chunk of equal encoded length");

        byte[] swapped = (byte[])fileB.Clone();
        Array.Copy(fileA, aBlock0Start, swapped, EncryptedSegmentFormat.HeaderSize, aBlock0Len);
        await File.WriteAllBytesAsync(manifestB.FilePath, swapped);

        var result = await _store.LoadAsync(segmentIdB);

        result.Success.Should().BeFalse();
        result.Reason.Should().Be(SegmentRebuildReason.CorruptedBlock);
    }

    [Fact]
    public async Task Load_BlocksReorderedWithinSameSegment_DetectedViaAadMismatch()
    {
        var docs = Enumerable.Range(0, 3 * EncryptedSegmentFormat.BlockSize / 200)
            .Select(i => new SegmentDocument(
                i, Guid.NewGuid(), Guid.NewGuid(),
                Enumerable.Range(0, 20).Select(t => $"reorder{i}_{t}")))
            .ToList();
        byte[] original = SegmentWriter.Build(docs);
        original.Length.Should().BeGreaterThan(EncryptedSegmentFormat.BlockSize * 2);

        var segmentId = Guid.NewGuid();
        await _store.StoreAsync(segmentId, original, docCount: 1);

        var manifest = await _manifestRepo.GetManifestAsync(segmentId);
        byte[] file = await File.ReadAllBytesAsync(manifest!.FilePath);

        int block0Start = EncryptedSegmentFormat.HeaderSize;
        int block0Len = BlockEncodedLength(file, block0Start);
        int block1Start = block0Start + block0Len;
        int block1Len = BlockEncodedLength(file, block1Start);

        // Swap block 0 and block 1 in place (same encoded length: both are full BlockSize chunks).
        block0Len.Should().Be(block1Len);
        byte[] reordered = (byte[])file.Clone();
        Array.Copy(file, block1Start, reordered, block0Start, block1Len);
        Array.Copy(file, block0Start, reordered, block1Start, block0Len);
        await File.WriteAllBytesAsync(manifest.FilePath, reordered);

        var result = await _store.LoadAsync(segmentId);

        result.Success.Should().BeFalse();
        result.Reason.Should().Be(SegmentRebuildReason.CorruptedBlock);
    }

    /// <summary>
    /// Length, in bytes, of the length-prefixed block ([ivLength][iv][ciphertextLength][ciphertext])
    /// starting at <paramref name="blockStart"/> within <paramref name="file"/>.
    /// </summary>
    private static int BlockEncodedLength(byte[] file, int blockStart)
    {
        int pos = blockStart;
        int ivLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(pos, 4));
        pos += 4 + ivLen;
        int ctLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(pos, 4));
        pos += 4 + ctLen;
        return pos - blockStart;
    }

    // ── AAD encoding sanity ─────────────────────────────────────────────────────────

    [Fact]
    public void BuildBlockAad_DiffersByIndexAndBySegment()
    {
        var segmentId = Guid.NewGuid();
        byte[] aad0 = EncryptedSegmentFormat.BuildBlockAad(segmentId, 0);
        byte[] aad1 = EncryptedSegmentFormat.BuildBlockAad(segmentId, 1);
        byte[] aadOtherSegment = EncryptedSegmentFormat.BuildBlockAad(Guid.NewGuid(), 0);

        aad0.Should().HaveCount(EncryptedSegmentFormat.BlockAadSize);
        aad0.Should().NotEqual(aad1);
        aad0.Should().NotEqual(aadOtherSegment);
    }

    // ── WP-11: SegmentManifestRepository.GetAllManifestsAsync/DeleteAllManifestsAsync ──

    [Fact]
    public async Task GetAllManifestsAsync_NoSegmentsStored_ReturnsEmpty()
    {
        (await _manifestRepo.GetAllManifestsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllManifestsAsync_ReturnsEveryStoredSegmentsManifest()
    {
        var segmentId1 = Guid.NewGuid();
        var segmentId2 = Guid.NewGuid();
        await _store.StoreAsync(segmentId1, BuildRealSegment(), docCount: 5);
        await _store.StoreAsync(segmentId2, BuildRealSegment(), docCount: 5);

        var manifests = await _manifestRepo.GetAllManifestsAsync();

        manifests.Select(m => m.SegmentId).Should().BeEquivalentTo([segmentId1, segmentId2]);
    }

    [Fact]
    public async Task DeleteAllManifestsAsync_ClearsEveryRow_ButLeavesSegmentFilesOnDisk()
    {
        var segmentId = Guid.NewGuid();
        await _store.StoreAsync(segmentId, BuildRealSegment(), docCount: 5);
        var manifestBefore = await _manifestRepo.GetManifestAsync(segmentId);
        File.Exists(manifestBefore!.FilePath).Should().BeTrue();

        await _manifestRepo.DeleteAllManifestsAsync();

        (await _manifestRepo.GetAllManifestsAsync()).Should().BeEmpty();
        (await _manifestRepo.GetManifestAsync(segmentId)).Should().BeNull();
        // The full-rebuild path deliberately leaves orphaned segment files on disk (see
        // SegmentManifestRepository.DeleteAllManifestsAsync's doc comment) -- wasted space, not a
        // correctness problem, and simpler than adding file-cleanup I/O to an already-degraded path.
        File.Exists(manifestBefore.FilePath).Should().BeTrue();
    }
}
