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
