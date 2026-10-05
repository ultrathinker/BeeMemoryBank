using System.Text.Json;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.MacOS;

/// <summary>
/// The blind app's small key/value state (<see cref="IBlindStateStore"/>) in one JSON file in its private folder. The Android counterpart
/// commits every write before it returns, because "Disconnect and wipe" clears the state and the process may end at once; this one keeps
/// that guarantee: each <see cref="Set"/> writes a temporary file in the same folder, flushes it to the disk and replaces the state file in
/// one rename, so a reader (or a crash) sees the old file or the new one and never half of one. Nothing secret lives here.
///
/// <para>A write that fails throws and leaves both the file and the in-memory state as they were. A state file that cannot be read is
/// kept next to the original (<c>.damaged-*</c>, never overwritten) and the store starts empty: nothing is made up from it, and the
/// identity and the secrets are recovered or refused by the core exactly as for any other missing state. Keeping that copy is a courtesy,
/// never a condition: if it cannot be made (no permission, a full disk, a read-only folder) the store still starts empty, so a damaged
/// file can never stop the start-up and with it the recovery of the identity.</para>
///
/// <para><see cref="Wipe"/> is the host's part of "Disconnect and wipe": it removes this file and its damaged copies (the core's own wipe
/// does not know them), and only those, only in this store's own folder.</para>
/// </summary>
public sealed class MacOsBlindStateStore : IBlindStateStore
{
    public const string FileName = "blind-state.json";

    private const int FormatVersion = 1;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _path;
    private readonly Action<string, string> _copyFile;
    private readonly Action<string> _deleteFile;
    private readonly object _gate = new();
    private Dictionary<string, string>? _values;

    public MacOsBlindStateStore(IBlindPaths paths) : this(Path.Combine(paths.DataDirectory, FileName)) { }

    public MacOsBlindStateStore(string filePath) : this(filePath, null, null) { }

    /// <summary>The file operations of the recovery and of the wipe are replaceable so that tests can make them fail.</summary>
    internal MacOsBlindStateStore(string filePath, Action<string, string>? copyFile, Action<string>? deleteFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _path = Path.GetFullPath(filePath);
        _copyFile = copyFile ?? ((from, to) => File.Copy(from, to, overwrite: false));
        _deleteFile = deleteFile ?? File.Delete;
    }

    public string FilePath => _path;

    /// <summary>
    /// What the last start-up had to work around, in words (no values from the file, nothing secret): a damaged state file and where its
    /// copy went, or that the copy could not be made. Null when the file was fine or absent. For the host's log.
    /// </summary>
    public string? LoadWarning { get; private set; }

    public string? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate) return Values().TryGetValue(key, out var value) ? value : null;
    }

    public void Set(string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            var current = Values();
            var next = new Dictionary<string, string>(current, StringComparer.Ordinal);
            if (value is null)
            {
                if (!next.Remove(key)) return;
            }
            else
            {
                if (next.TryGetValue(key, out var existing) && existing == value) return;
                next[key] = value;
            }
            Persist(next);
            _values = next;
        }
    }

    private Dictionary<string, string> Values() => _values ??= Load();

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = File.OpenRead(_path);
            using var document = JsonDocument.Parse(stream);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("values", out var map) || map.ValueKind != JsonValueKind.Object)
                throw new JsonException("The state file has no values object.");
            foreach (var property in map.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String) throw new JsonException("A state value is not a string.");
                values[property.Name] = property.Value.GetString()!;
            }
            return values;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            LoadWarning = KeepDamagedCopy();
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Sets the damaged file aside. A failed copy - of any kind that a file operation can fail with - is only reported (in the returned
    /// text), never thrown: the recovery that follows must not depend on it.
    /// </summary>
    private string KeepDamagedCopy()
    {
        var copy = _path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            if (!File.Exists(copy)) _copyFile(_path, copy);
            return $"The state file could not be read; a copy was kept as {Path.GetFileName(copy)} and the state starts empty.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException
                                       or ArgumentException or InvalidOperationException)
        {
            return $"The state file could not be read and no copy of it could be kept ({ex.GetType().Name}); the state starts empty.";
        }
    }

    /// <summary>
    /// Forgets the state and removes its files from this store's own folder: the state file, the copies of damaged state files
    /// (<c>blind-state.json.damaged-*</c>) and temporary files a crashed write left (<c>blind-state.json.*.tmp</c>). Nothing else in the
    /// folder is touched, no sub-folder is entered and no other folder is looked at. Every file is tried; failures are reported together
    /// afterwards. The store is empty (in memory) in any case and writes a new file when something is set again.
    /// </summary>
    public void Wipe()
    {
        WipeFiles();
    }

    /// <summary>
    /// <see cref="IBlindStateStore.Erase"/>: Blind.AppCore's "Disconnect and wipe" calls it after it set every key to null, so that the file
    /// and the copies of damaged ones do not stay behind. The same as <see cref="Wipe"/>.
    /// </summary>
    public void Erase() => WipeFiles();

    private void WipeFiles()
    {
        lock (_gate)
        {
            _values = new Dictionary<string, string>(StringComparer.Ordinal);
            LoadWarning = null;
            var directory = Path.GetDirectoryName(_path)!;
            if (!Directory.Exists(directory)) return;
            var name = Path.GetFileName(_path);
            var failures = new List<Exception>();
            foreach (var file in Directory.EnumerateFiles(directory, name + "*", SearchOption.TopDirectoryOnly).ToList())
            {
                if (!IsOwnFile(Path.GetFileName(file), name)) continue;
                try { _deleteFile(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failures.Add(new IOException($"{Path.GetFileName(file)}: {ex.Message}", ex));
                }
            }
            if (failures.Count > 0)
                throw new AggregateException("Not every state file could be removed: " + string.Join("; ", failures.Select(f => f.Message)), failures);
        }
    }

    private static bool IsOwnFile(string leaf, string stateFileName) =>
        string.Equals(leaf, stateFileName, StringComparison.Ordinal) ||
        leaf.StartsWith(stateFileName + ".damaged-", StringComparison.Ordinal) ||
        (leaf.StartsWith(stateFileName + ".", StringComparison.Ordinal) && leaf.EndsWith(".tmp", StringComparison.Ordinal));

    private void Persist(Dictionary<string, string> values)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
            using (var stream = new FileStream(temporary, options))
            {
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("version", FormatVersion);
                    writer.WriteStartObject("values");
                    foreach (var pair in values.OrderBy(p => p.Key, StringComparer.Ordinal))
                        writer.WriteString(pair.Key, pair.Value);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
            throw;
        }
    }
}
