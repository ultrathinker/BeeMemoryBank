using System.Globalization;
using System.Text.RegularExpressions;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// The few things the usage files (<c>usage.json</c>, <c>device-usage.json</c>) ask of the file system, behind one seam so that a test can make any of them fail the way a full disk or a
/// permission does. Every method throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> for a failure; nothing here swallows one.
/// </summary>
internal interface IBlindUsageFiles
{
    /// <summary>The bytes of the file, or null when there is no such file. A file that is there and cannot be read now throws: that is not "absent".</summary>
    byte[]? Read(string path);

    /// <summary>True when something is at <paramref name="path"/>. Found by OPENING it, not by asking whether it exists: a file that is there and cannot be opened (access denied, held by another process) exists.</summary>
    bool Exists(string path);

    /// <summary>Writes <paramref name="bytes"/> so that <paramref name="path"/> holds all of them or what it held before: a temporary file beside it, flushed to the disk, renamed over it.</summary>
    void Write(string path, byte[] bytes);

    /// <summary>Creates <paramref name="path"/> with <paramref name="bytes"/> and fails with <see cref="IOException"/> when anything is already there; nothing is ever replaced. A copy that could not be completed is not left behind.</summary>
    void CreateNew(string path, byte[] bytes);

    IReadOnlyList<string> List(string directory, string searchPattern);

    void Delete(string path);
}

/// <summary>The real file system.</summary>
internal sealed class BlindUsageFiles : IBlindUsageFiles
{
    public static readonly BlindUsageFiles Default = new();

    public byte[]? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public bool Exists(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // there, and it cannot be opened: a file held by someone, a folder, a name that is denied to us. Taken, all the same.
        }
    }

    public void Write(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public void CreateNew(string path, byte[] bytes)
    {
        var created = false;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            if (created)
            {
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    public IReadOnlyList<string> List(string directory, string searchPattern) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, searchPattern) : [];

    public void Delete(string path) => File.Delete(path);
}

/// <summary>
/// What a repair keeps of a usage file that could not be trusted: a verified COPY of its bytes under a name of its own (<c>usage.json.damaged-&lt;utc&gt;</c>, a number added when the name is
/// taken), created with a create-new that never replaces anything. A copy, not a move: the damaged file stays where it is until the new, valid file has been written over it in one step, so the
/// name is never empty in between and a restart that finds the repair half done still finds a file it cannot trust, not a fresh install. The newest <see cref="Keep"/> copies of one file are kept;
/// older ones are retired after a repair has succeeded, and the newest is never retired, so the only damaged copy is never lost.
/// </summary>
internal static partial class BlindUsageEvidence
{
    /// <summary>How many kept-aside copies of one usage file stay.</summary>
    public const int Keep = 5;

    private const int MaxNames = 10_000;

    /// <summary>Copies the file's bytes aside. The name of the copy, or null when there is no file to keep. Throws when the file cannot be read or the copy cannot be made and verified.</summary>
    public static string? KeepAside(IBlindUsageFiles files, string path, TimeProvider time)
    {
        var bytes = files.Read(path);
        if (bytes is null) return null;

        var stamp = time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        // Numbered after the highest number of this second that exists, never into a gap an earlier retirement left: the number is what orders copies made within one second.
        var first = 0;
        foreach (var existing in Existing(files, path))
            if (existing.Stamp == stamp) first = Math.Max(first, existing.Number + 1);
        for (var n = first; n < first + MaxNames; n++)
        {
            var target = path + ".damaged-" + stamp + (n == 0 ? "" : "-" + n.ToString(CultureInfo.InvariantCulture));
            if (files.Exists(target)) continue;
            try
            {
                files.CreateNew(target, bytes);
            }
            catch (IOException) when (files.Exists(target))
            {
                continue; // somebody took the name between the probe and the create: the next name
            }

            var back = files.Read(target);
            if (back is null || !back.AsSpan().SequenceEqual(bytes))
                throw new IOException("The kept-aside copy of the damaged file could not be verified.");
            return target;
        }
        throw new IOException("No free name was found for the kept-aside copy of the damaged file.");
    }

    /// <summary>Retires the oldest copies beyond <see cref="Keep"/>. Best effort: a copy that cannot be removed stays. Names this class did not make are left alone.</summary>
    public static void Prune(IBlindUsageFiles files, string path)
    {
        try
        {
            var copies = Existing(files, path);
            foreach (var old in copies.OrderByDescending(c => c.Stamp, StringComparer.Ordinal).ThenByDescending(c => c.Number).Skip(Keep))
            {
                try { files.Delete(old.Path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retiring old copies is housekeeping: it never fails a repair that has already succeeded.
        }
    }

    /// <summary>The kept-aside copies of <paramref name="path"/> that are there (names this class made; anything else beside it is left alone).</summary>
    private static List<(string Path, string Stamp, int Number)> Existing(IBlindUsageFiles files, string path)
    {
        var copies = new List<(string Path, string Stamp, int Number)>();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var prefix = Path.GetFileName(path) + ".damaged-";
        IReadOnlyList<string> found;
        try { found = files.List(directory, prefix + "*"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return copies; }
        foreach (var name in found)
        {
            var match = Suffix().Match(Path.GetFileName(name)[prefix.Length..]);
            if (!match.Success) continue;
            copies.Add((name, match.Groups["stamp"].Value, match.Groups["n"].Success ? int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) : 0));
        }
        return copies;
    }

    [GeneratedRegex(@"^(?<stamp>\d{8}T\d{6}Z)(?:-(?<n>\d{1,5}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Suffix();
}
