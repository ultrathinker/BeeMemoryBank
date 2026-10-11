using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BeeMemoryBank.TestSupport;

public sealed record CanaryHit(string Canary, string Encoding, string Where, long Offset);

/// <summary>Canary values and a byte scanner for the Step A red tests (docs/design/step-a-spec.md 16.1).</summary>
public static class MetadataCanaries
{
    public static string Ascii(string label) => $"cnry{label}{Guid.NewGuid():N}";

    public static string NonAscii(string label) => $"ZürichΩ{label}{Guid.NewGuid():N}";

    /// <summary>The byte forms a canary may take in a file, a body or a log.</summary>
    public static IEnumerable<(string Name, byte[] Bytes)> Forms(string canary)
    {
        var utf8 = Encoding.UTF8.GetBytes(canary);
        yield return ("utf8", utf8);
        yield return ("utf16le", Encoding.Unicode.GetBytes(canary));
        yield return ("lower", Encoding.UTF8.GetBytes(canary.ToLowerInvariant()));
        var escaped = JsonSerializer.Serialize(canary, new JsonSerializerOptions { Encoder = JavaScriptEncoder.Default });
        yield return ("json", Encoding.UTF8.GetBytes(escaped[1..^1]));
        for (var shift = 0; shift < 3; shift++)
        {
            var shifted = new byte[shift + utf8.Length];
            utf8.CopyTo(shifted, shift);
            var b64 = Convert.ToBase64String(shifted);
            var start = shift == 0 ? 0 : 4;
            var core = b64[start..^4];
            if (core.Length >= 8) yield return ($"base64+{shift}", Encoding.ASCII.GetBytes(core));
        }
    }

    public static List<CanaryHit> Scan(byte[] haystack, IEnumerable<string> canaries, string where)
    {
        var hits = new List<CanaryHit>();
        foreach (var canary in canaries)
            foreach (var (name, needle) in Forms(canary))
            {
                var at = haystack.AsSpan().IndexOf(needle);
                if (at >= 0) hits.Add(new CanaryHit(canary, name, where, at));
            }
        return hits;
    }

    public static List<CanaryHit> ScanFile(string path, IEnumerable<string> canaries)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Scan(memory.ToArray(), canaries, path);
    }

    /// <summary>Every file below <paramref name="directory"/> (database, -wal, -shm, journals, logs, packages as raw bytes).</summary>
    public static List<CanaryHit> ScanDirectory(string directory, IEnumerable<string> canaries)
    {
        var list = canaries.ToList();
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .SelectMany(f => ScanFile(f, list)).ToList();
    }

    /// <summary>A .tar.gz package: the compressed bytes, then every entry decompressed.</summary>
    public static List<CanaryHit> ScanArchive(string archivePath, IEnumerable<string> canaries)
    {
        var list = canaries.ToList();
        var hits = ScanFile(archivePath, list);
        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.DataStream is null) continue;
            using var memory = new MemoryStream();
            entry.DataStream.CopyTo(memory);
            hits.AddRange(Scan(memory.ToArray(), list, $"{archivePath}!{entry.Name}"));
        }
        return hits;
    }
}
