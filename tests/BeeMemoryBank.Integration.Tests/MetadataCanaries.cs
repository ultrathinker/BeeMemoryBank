using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BeeMemoryBank.AppPaths;

namespace BeeMemoryBank.TestSupport;

public sealed record CanaryHit(string Canary, string Encoding, string Where, long Offset);

/// <summary>Canary values and a byte scanner for the Step A red tests (docs/design/step-a-spec.md 16.1).</summary>
public static class MetadataCanaries
{
    private static readonly TimeSpan RescanWindow = TimeSpan.FromSeconds(30);

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
        // The instance lock of a running node cannot be opened on Linux (an exclusive flock, InstanceGuard). Nothing writes to it,
        // so an empty one holds nothing to find; a lock with content is still opened and scanned, and fails here on Linux.
        if (Path.GetFileName(path) == InstanceGuard.FileName && new FileInfo(path).Length == 0) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Scan(memory.ToArray(), canaries, path);
    }

    /// <summary>Every file below <paramref name="directory"/> (database, -wal, -shm, journals, logs, packages as raw bytes). Not the node's instance lock:
    /// it holds no data and a running host holds it exclusively (an exclusive flock on Linux and macOS: any other open fails).
    /// A file that disappears between the listing and the read is not skipped and not a finding: the whole folder is listed and read again for up to
    /// <see cref="RescanWindow"/>, and what is still there on the last try is judged. A failure that outlasts the window is thrown, never turned into an empty result.</summary>
    public static List<CanaryHit> ScanDirectory(string directory, IEnumerable<string> canaries)
    {
        var list = canaries.ToList();
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var deadline = DateTime.UtcNow + RescanWindow;
        while (true)
        {
            try
            {
                return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(f => Path.GetFileName(f) != ".instance.lock")
                    .SelectMany(f => ScanFile(f, list)).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
            {
                // A database that is still open replaces its -wal and -shm files, and a running host replaces staged packages: a file that was listed
                // can be gone (on Windows: pending delete) when it is read, which is not a finding. List and read the whole folder again; what is still there on the last try is judged.
                Thread.Sleep(100);
            }
        }
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
