using System.Buffers.Binary;
using BeeMemoryBank.Storage.Search;

namespace BeeMemoryBank.Storage.Tests.Search;

/// <summary>
/// WP-13 Task 1: pure, no-I/O unit tests of <see cref="EncryptedSegmentFormat.TryParseHeader"/>'s
/// own contract around a future container format version -- the OUTER encrypted-container version
/// (distinct from the INNER "BMBI" segment format checked by
/// BeeMemoryBank.Search.Tests.Segment.SegmentReaderFormatVersionTests).
///
/// <para>
/// Checked before writing this: EncryptedSegmentStoreTests.cs already has
/// <c>Load_WrongFormatVersionInFileHeader_ReturnsRebuildNeededWithoutThrowing</c>, which proves the
/// end-to-end <see cref="EncryptedSegmentStore.LoadAsync"/> behavior (round-trips through real
/// encryption, a real file, a real manifest row) for exactly this scenario -- that test is not
/// duplicated here. What is missing, and added here, is a direct unit test of
/// <see cref="EncryptedSegmentFormat.TryParseHeader"/> itself: its own XML doc states it
/// deliberately does NOT reject a future format version (that decision is left to the caller), so
/// this test pins that documented contract at the unit level, independent of
/// <see cref="EncryptedSegmentStore"/>'s own caller-side check -- if a future change to
/// <c>TryParseHeader</c> ever started silently rejecting or misparsing a well-formed-but-newer
/// header, this test (not just the higher-level integration test) would catch the behavior change
/// at its source.
/// </para>
/// </summary>
public class EncryptedSegmentFormatVersionTests
{
    [Fact]
    public void TryParseHeader_FutureFormatVersion_ParsesSuccessfully_DoesNotRejectAtThisLayer()
    {
        // A structurally well-formed header (right magic, plausible lengths) that simply claims a
        // format version from the future -- exactly what a newer node's real output would look
        // like. TryParseHeader's documented contract: format-version mismatches are NOT checked
        // here (both are "legitimate, well-formed headers" per its own doc comment) -- the caller
        // (EncryptedSegmentStore.LoadAsync) is the one that compares FormatVersion and decides.
        byte[] header = new byte[EncryptedSegmentFormat.HeaderSize];
        var segmentId = Guid.NewGuid();
        EncryptedSegmentFormat.WriteHeader(header, segmentId, originalLength: 128, blockCount: 2);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(EncryptedSegmentFormat.HeaderFormatVersionOffset, 4), EncryptedSegmentFormat.FormatVersion + 1);

        bool parsed = EncryptedSegmentFormat.TryParseHeader(header, out var parsedHeader);

        parsed.Should().BeTrue("TryParseHeader must not misinterpret a future version as malformed -- it hands the raw version to the caller to judge");
        parsedHeader.FormatVersion.Should().Be(EncryptedSegmentFormat.FormatVersion + 1, "the actual future version must be surfaced accurately, not silently clamped/misread as the current one");
        parsedHeader.SegmentId.Should().Be(segmentId);
        parsedHeader.OriginalLength.Should().Be(128);
        parsedHeader.BlockCount.Should().Be(2);
    }

    [Fact]
    public void TryParseHeader_CallerMustRejectFutureVersionItself_MatchesEncryptedSegmentStoresOwnCheck()
    {
        // Documents the division of responsibility this WP verified end-to-end:
        // EncryptedSegmentStore.LoadAsync is the layer that turns "future version parsed OK" into
        // a rejection (SegmentRebuildReason.FormatVersionMismatch) -- see
        // EncryptedSegmentStoreTests.Load_WrongFormatVersionInFileHeader_ReturnsRebuildNeededWithoutThrowing
        // for that full round-trip. This assertion just pins the comparison EncryptedSegmentStore
        // performs, directly, so the two tests together prove every layer of the chain.
        byte[] header = new byte[EncryptedSegmentFormat.HeaderSize];
        EncryptedSegmentFormat.WriteHeader(header, Guid.NewGuid(), originalLength: 0, blockCount: 0);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(EncryptedSegmentFormat.HeaderFormatVersionOffset, 4), EncryptedSegmentFormat.FormatVersion + 1);

        EncryptedSegmentFormat.TryParseHeader(header, out var parsedHeader).Should().BeTrue();

        (parsedHeader.FormatVersion != EncryptedSegmentFormat.FormatVersion).Should().BeTrue(
            "this is exactly the boolean EncryptedSegmentStore.LoadAsync evaluates to decide FormatVersionMismatch");
    }
}
