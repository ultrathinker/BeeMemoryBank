using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace BeeMemoryBank.AppPaths.Tests;

/// <summary>Builds the archives the scanner tests plant: tar (every format), gzip, zip, and the damaged variants of each.</summary>
internal static class ArchiveBuilder
{
    public static byte[] Tar(TarEntryFormat format, params (string Name, byte[] Data)[] members)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, format, leaveOpen: true))
        {
            foreach (var (name, data) in members)
            {
                TarEntry entry = format switch
                {
                    TarEntryFormat.Pax => new PaxTarEntry(TarEntryType.RegularFile, name),
                    TarEntryFormat.Gnu => new GnuTarEntry(TarEntryType.RegularFile, name),
                    TarEntryFormat.Ustar => new UstarTarEntry(TarEntryType.RegularFile, name),
                    _ => new V7TarEntry(TarEntryType.V7RegularFile, name),
                };
                entry.DataStream = new MemoryStream(data);
                writer.WriteEntry(entry);
            }
        }
        return output.ToArray();
    }

    public static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(data);
        return output.ToArray();
    }

    public static byte[] Zip(CompressionLevel level, params (string Name, byte[] Data)[] members)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in members)
            {
                var entry = zip.CreateEntry(name, level);
                using var stream = entry.Open();
                stream.Write(data);
            }
        }
        return output.ToArray();
    }

    /// <summary>Builds a ZIP whose first deflate block is readable and whose next block is reserved/invalid.</summary>
    public static byte[] ZipWithInvalidDeflateTail(string name, byte[] payload)
    {
        if (payload.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(payload));
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var compressed = new byte[payload.Length + 6];
        BinaryPrimitives.WriteUInt16LittleEndian(compressed.AsSpan(1), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(compressed.AsSpan(3), (ushort)~payload.Length);
        payload.CopyTo(compressed, 5);
        compressed[^1] = 0x07; // BFINAL=1, BTYPE=3: a reserved deflate block.
        var crc = Crc32(payload);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        WriteZipLocalHeader(writer, nameBytes, crc, compressed.Length, payload.Length);
        writer.Write(nameBytes);
        writer.Write(compressed);
        var centralOffset = (uint)output.Position;
        writer.Write(0x02014B50u);
        writer.Write((ushort)20); // version made by
        writer.Write((ushort)20); // version needed
        writer.Write((ushort)0); // flags
        writer.Write((ushort)8); // deflate
        writer.Write(0u); // time/date
        writer.Write(crc);
        writer.Write((uint)compressed.Length);
        writer.Write((uint)payload.Length);
        writer.Write((ushort)nameBytes.Length);
        writer.Write((ushort)0); // extra length
        writer.Write((ushort)0); // comment length
        writer.Write((ushort)0); // disk number
        writer.Write((ushort)0); // internal attributes
        writer.Write(0u); // external attributes
        writer.Write(0u); // local-header offset
        writer.Write(nameBytes);
        var centralSize = (uint)output.Position - centralOffset;
        writer.Write(0x06054B50u);
        writer.Write((ushort)0); // disk
        writer.Write((ushort)0); // central-directory disk
        writer.Write((ushort)1); // entries on disk
        writer.Write((ushort)1); // entries total
        writer.Write(centralSize);
        writer.Write(centralOffset);
        writer.Write((ushort)0); // comment length
        writer.Flush();
        return output.ToArray();
    }

    private static void WriteZipLocalHeader(BinaryWriter writer, byte[] name, uint crc, int compressedLength, int payloadLength)
    {
        writer.Write(0x04034B50u);
        writer.Write((ushort)20); // version needed
        writer.Write((ushort)0); // flags
        writer.Write((ushort)8); // deflate
        writer.Write(0u); // time/date
        writer.Write(crc);
        writer.Write((uint)compressedLength);
        writer.Write((uint)payloadLength);
        writer.Write((ushort)name.Length);
        writer.Write((ushort)0); // extra length
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    /// <summary>Sets bits in the general-purpose flags of every central-directory entry of a zip.</summary>
    public static byte[] ZipWithFlags(byte[] zip, ushort setBits)
    {
        var copy = (byte[])zip.Clone();
        for (var i = 0; i + 46 < copy.Length; i++)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(i)) != 0x02014b50) continue;
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(i + 8));
            BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(i + 8), (ushort)(flags | setBits));
        }
        return copy;
    }

    /// <summary>Sets the compression method of every central-directory entry of a zip.</summary>
    public static byte[] ZipWithMethod(byte[] zip, ushort method)
    {
        var copy = (byte[])zip.Clone();
        for (var i = 0; i + 46 < copy.Length; i++)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(i)) != 0x02014b50) continue;
            BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(i + 10), method);
        }
        return copy;
    }

    /// <summary>Corrupts the checksum field of the header of the Nth entry of a V7 or ustar tar whose members all fit one block of data.</summary>
    public static byte[] TarWithBadChecksum(byte[] tar, int entryIndex, int memberDataSize)
    {
        var copy = (byte[])tar.Clone();
        var padded = (memberDataSize + 511) / 512 * 512;
        var header = entryIndex * (512 + padded);
        copy[header + 148] = (byte)'9'; // not an octal digit: no header checksum can be valid
        return copy;
    }

    /// <summary>A file that starts like a format the scanner cannot open and carries random bytes after the magic.</summary>
    public static byte[] WithMagic(byte[] magic, int total = 4096)
    {
        var bytes = new byte[total];
        new Random(5).NextBytes(bytes);
        magic.CopyTo(bytes, 0);
        return bytes;
    }

    public static readonly byte[] Bzip2 = [(byte)'B', (byte)'Z', (byte)'h', (byte)'9', 0x31, 0x41, 0x59, 0x26, 0x53, 0x59];
    public static readonly byte[] Xz = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];
    public static readonly byte[] Zstd = [0x28, 0xB5, 0x2F, 0xFD];
    public static readonly byte[] Lz4 = [0x04, 0x22, 0x4D, 0x18];
    public static readonly byte[] SevenZip = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
    public static readonly byte[] Rar = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07];

    public static byte[] RandomBytes(int length, int seed = 11)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
