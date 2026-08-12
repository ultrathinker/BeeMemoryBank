using System.Buffers.Binary;
using BeeMemoryBank.Search.Segment;

namespace BeeMemoryBank.Search.Tests.Segment;

/// <summary>
/// WP-13 Task 1: "format-version-from-the-future" -- simulates a downgrade scenario where a newer
/// node version wrote a "BMBI" segment using a format version this codebase does not understand,
/// and an older version now tries to read it. Checked before writing this: none of
/// SegmentRoundtripTests.cs, SegmentCollisionTests.cs, or SegmentScaleTests.cs in this same
/// directory ever construct a <see cref="SegmentReader"/> over a header with a tampered
/// <see cref="SegmentLayout.FormatVersion"/> -- every existing test builds bytes via
/// <see cref="SegmentWriter.Build"/> and reads them back unmodified. This is a genuine gap at the
/// "BMBI" segment level, distinct from (and not covered by)
/// EncryptedSegmentStoreTests.Load_WrongFormatVersionInFileHeader_ReturnsRebuildNeededWithoutThrowing,
/// which only exercises the OUTER encrypted-container format version
/// (<c>EncryptedSegmentFormat.FormatVersion</c>) -- a completely separate version number from the
/// INNER "BMBI" segment format checked here.
///
/// <para>
/// Finding (see wp-13-report.md for the full writeup): in isolation, <see cref="SegmentReader"/>'s
/// constructor DOES correctly reject a future format version -- it throws
/// <see cref="NotSupportedException"/> rather than silently misinterpreting newer-format bytes as
/// the current format. That part of the system is safe by design. The real gap is one layer up:
/// <c>BeeMemoryBank.Sync.Search.SearchIndexLifecycleService.EnsureWarmStartedAsync</c> constructs a
/// <see cref="SegmentReader"/> directly over a successfully-decrypted persisted segment's bytes
/// without catching this exception, so a future-inner-format segment crashes warm-start instead of
/// triggering the graceful full-rebuild fallback every other known failure mode gets. See
/// BeeMemoryBank.Sync.Tests.Search.SearchIndexLifecycleFormatVersionResilienceTests for that
/// system-level reproduction; this file only proves the low-level primitive's own behavior is
/// correct.
/// </para>
/// </summary>
public class SegmentReaderFormatVersionTests
{
    [Fact]
    public void Constructor_FutureFormatVersion_ThrowsNotSupportedException_DoesNotMisinterpretBytes()
    {
        byte[] segment = SegmentWriter.Build(
        [
            new SegmentDocument(0, Guid.NewGuid(), Guid.NewGuid(), ["alpha", "beta"]),
        ]);

        // Claim a format version from the future (magic bytes and everything else left intact --
        // only the version field is a lie, exactly what a newer writer's real output would look
        // like to an older reader).
        BinaryPrimitives.WriteInt32LittleEndian(
            segment.AsSpan(SegmentLayout.HeaderFormatVersionOffset, 4), SegmentLayout.FormatVersion + 1);

        Action act = () => _ = new SegmentReader(segment);

        act.Should().Throw<NotSupportedException>(
            "a segment claiming a newer format version than this build understands must be rejected explicitly, never silently parsed as the current format");
    }

    [Fact]
    public void Constructor_FutureFormatVersion_DoesNotThrowGenericOrSilentlyCorruptReads()
    {
        // Companion assertion: the rejection must be the specific, documented exception type (not
        // e.g. an IndexOutOfRangeException from misreading the doc table with wrong offsets, which
        // would indicate the version field was ignored and the rest of the header/body was parsed
        // as if it were the current layout).
        byte[] segment = SegmentWriter.Build(
        [
            new SegmentDocument(0, Guid.NewGuid(), Guid.NewGuid(), ["term"]),
        ]);
        BinaryPrimitives.WriteInt32LittleEndian(
            segment.AsSpan(SegmentLayout.HeaderFormatVersionOffset, 4), 999);

        Exception? caught = null;
        try
        {
            _ = new SegmentReader(segment);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull();
        caught.Should().BeOfType<NotSupportedException>();
        caught!.Message.Should().Contain("999", "the error should name the actual unsupported version for diagnosability");
    }
}
