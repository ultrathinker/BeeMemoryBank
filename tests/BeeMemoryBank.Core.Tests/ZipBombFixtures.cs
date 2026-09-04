using System.IO.Compression;
using System.Text;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Builders for the hostile archives the import services have to survive. Kept out of the two
/// import test classes because both need the same shapes and one of them (the forged size field)
/// is fiddly enough that having two copies would invite them to drift apart.
/// </summary>
internal static class ZipBombFixtures
{
    internal const int OneMb = 1024 * 1024;

    internal static byte[] BuildArchive(Action<ZipArchive> populate)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            populate(zip);
        return ms.ToArray();
    }

    internal static void AddText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    /// <summary>
    /// Adds an entry of <paramref name="megabytes"/> MB of a single repeated character. Deflate
    /// squeezes that to roughly a thousandth of its size, which is the whole point: the archive
    /// stays small enough to sail through the endpoint's upload limit while the entry expands
    /// far past what the node can hold.
    /// </summary>
    internal static void AddFiller(ZipArchive zip, string name, int megabytes,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        using var stream = entry.Open();
        var chunk = new byte[OneMb];
        chunk.AsSpan().Fill((byte)'a');
        for (var i = 0; i < megabytes; i++) stream.Write(chunk);
    }

    /// <summary>
    /// Rewrites the "uncompressed size" field in the archive's local file headers and central
    /// directory headers, leaving the compressed size (and the data itself) untouched — the
    /// archive now DECLARES <paramref name="declaredBytes"/> while still handing out every byte
    /// it physically holds.
    ///
    /// <para>
    /// Only meaningful for a STORED entry. .NET bounds a deflated entry by the declared size, so
    /// shrinking that field on a compressed entry just truncates the read; a stored entry is
    /// bounded by its compressed size instead, which is how the declaration and the reality come
    /// apart. That is exactly the case a <c>ZipArchiveEntry.Length</c> check would wave through.
    /// </para>
    ///
    /// <para>
    /// The scan is signature-based, which is safe here only because the payload is repeated
    /// filler that cannot contain a "PK" header signature.
    /// </para>
    /// </summary>
    internal static void ForgeDeclaredUncompressedSize(byte[] archive, uint declaredBytes)
    {
        const int localHeaderUncompressedSizeOffset = 22;
        const int centralHeaderUncompressedSizeOffset = 24;

        var forged = BitConverter.GetBytes(declaredBytes);
        var patched = 0;
        for (var i = 0; i + 30 <= archive.Length; i++)
        {
            if (archive[i] != 0x50 || archive[i + 1] != 0x4B) continue;

            if (archive[i + 2] == 0x03 && archive[i + 3] == 0x04)
            {
                forged.CopyTo(archive, i + localHeaderUncompressedSizeOffset);
                patched++;
            }
            else if (archive[i + 2] == 0x01 && archive[i + 3] == 0x02)
            {
                forged.CopyTo(archive, i + centralHeaderUncompressedSizeOffset);
                patched++;
            }
        }

        if (patched == 0)
            throw new InvalidOperationException("No ZIP headers found to forge — the fixture is broken.");
    }

    /// <summary>Confirms the forged archive really does lie, so a failing bomb test can never be
    /// mistaken for "the fixture silently stopped being hostile".</summary>
    internal static (long declared, long compressed) DeclaredSizeOf(byte[] archive, string entryName)
    {
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        var entry = zip.GetEntry(entryName) ?? throw new InvalidOperationException($"No entry '{entryName}'.");
        return (entry.Length, entry.CompressedLength);
    }
}
