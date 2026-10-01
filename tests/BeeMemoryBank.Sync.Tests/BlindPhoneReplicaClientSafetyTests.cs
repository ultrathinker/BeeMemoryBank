using System.Formats.Tar;
using System.IO.Compression;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Sync.Tests;

public sealed class BlindPhoneReplicaClientSafetyTests
{
    [Theory]
    [InlineData(null, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==", 1)]
    [InlineData("not-a-hash", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==", 1)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", "not-base64", 1)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==", 0)]
    public void ValidatePartialMetadata_RejectsFieldsThatCannotSafelyResume(string? sha256, string signatureB64, long length)
    {
        var act = () => BlindPhoneReplicaClient.ValidatePartialMetadata(sha256, signatureB64, length);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task ReadArchiveEntryAsync_RejectsAManifestOverItsMemoryLimit()
    {
        var archive = CreateArchive("manifest.json", new byte[1025]);

        Func<Task> act = async () => _ = await BlindPhoneReplicaClient.ReadArchiveEntryAsync(
            archive, "manifest.json", 1024, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task CopyWithLimitAsync_StopsAnOversizedArchiveEntryBeforeWritingBeyondTheLimit()
    {
        await using var input = new MemoryStream(new byte[1025]);
        await using var output = new MemoryStream();

        Func<Task> act = async () => _ = await BlindPhoneReplicaClient.CopyWithLimitAsync(
            input, output, 1024, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        output.Length.Should().Be(1024);
    }

    [Fact]
    public async Task ExtractAsync_RejectsAnEntryOverThePerEntryLimit_WithoutWritingPastIt()
    {
        var destination = NewDestination();
        var archive = ArchiveFile(("big.bin", new byte[1025]));

        Func<Task> act = () => BlindPhoneReplicaClient.ExtractAsync(
            archive, destination, CancellationToken.None, maximumEntryBytes: 1024, maximumTotalBytes: 100_000);

        await act.Should().ThrowAsync<InvalidDataException>();
        new FileInfo(Path.Combine(destination, "big.bin")).Length.Should().Be(1024);
    }

    [Fact]
    public async Task ExtractAsync_RejectsEntriesThatTogetherPassTheCumulativeLimit()
    {
        var destination = NewDestination();
        var archive = ArchiveFile(("a.bin", new byte[600]), ("b.bin", new byte[600]));

        Func<Task> act = () => BlindPhoneReplicaClient.ExtractAsync(
            archive, destination, CancellationToken.None, maximumEntryBytes: 1024, maximumTotalBytes: 1000);

        await act.Should().ThrowAsync<InvalidDataException>();
        new FileInfo(Path.Combine(destination, "b.bin")).Length.Should().Be(400, "the second entry gets only what is left of the total");
    }

    [Fact]
    public async Task ExtractAsync_RejectsAnEntryOnceTheCumulativeLimitIsSpentExactly()
    {
        var destination = NewDestination();
        var archive = ArchiveFile(("a.bin", new byte[500]), ("b.bin", new byte[500]), ("c.bin", new byte[1]));

        Func<Task> act = () => BlindPhoneReplicaClient.ExtractAsync(
            archive, destination, CancellationToken.None, maximumEntryBytes: 1024, maximumTotalBytes: 1000);

        await act.Should().ThrowAsync<InvalidDataException>();
        File.Exists(Path.Combine(destination, "b.bin")).Should().BeTrue("exactly the limit is allowed");
    }

    [Fact]
    public async Task ExtractAsync_AcceptsEntriesWithinBothLimits()
    {
        var destination = NewDestination();
        var archive = ArchiveFile(("a.bin", new byte[500]), ("b.bin", new byte[500]));

        await BlindPhoneReplicaClient.ExtractAsync(
            archive, destination, CancellationToken.None, maximumEntryBytes: 500, maximumTotalBytes: 1000);

        new FileInfo(Path.Combine(destination, "a.bin")).Length.Should().Be(500);
        new FileInfo(Path.Combine(destination, "b.bin")).Length.Should().Be(500);
    }

    private static string ArchiveFile(params (string Name, byte[] Contents)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "bmb-extract-" + Guid.NewGuid().ToString("N") + ".tar.gz");
        using var stream = CreateArchive(entries);
        File.WriteAllBytes(path, stream.ToArray());
        return path;
    }

    private static string NewDestination() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "bmb-extract-" + Guid.NewGuid().ToString("N"))).FullName;

    private static MemoryStream CreateArchive(params (string Name, byte[] Contents)[] entries)
    {
        var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.NoCompression, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            foreach (var (name, contents) in entries)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(contents) });
        }
        archive.Position = 0;
        return archive;
    }

    private static MemoryStream CreateArchive(string name, byte[] contents)
    {
        var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.NoCompression, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(contents)
            });
        }
        archive.Position = 0;
        return archive;
    }
}
