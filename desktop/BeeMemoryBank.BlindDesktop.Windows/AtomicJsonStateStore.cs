using System.Text;
using System.Text.Json;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Windows;

/// <summary>
/// The blind app's small persistent state as one JSON object (<c>{"key":"value"}</c>, UTF-8 without a byte-order mark), the
/// desktop counterpart of the Android SharedPreferences: node id, name, call code, load/sync/backup times, schedule. Nothing
/// secret lives here. Every change is written at once and atomically (temp file, flush, replace), like Android's committed
/// preferences, so a crash never leaves half a file.
///
/// <para>The file is read once. If it cannot be read or parsed it counts as empty: the identity, which is checked against the
/// database and the DPAPI secrets, is then recovered, and the user pairs again - nothing is guessed.</para>
/// </summary>
public sealed class AtomicJsonStateStore : IBlindStateStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly SortedDictionary<string, string> _values = new(StringComparer.Ordinal);

    public AtomicJsonStateStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>True when an existing file could not be read and was treated as empty.</summary>
    public bool WasUnreadable { get; private set; }

    public string? Get(string key)
    {
        lock (_gate) return _values.GetValueOrDefault(key);
    }

    public void Set(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            if (value is null)
            {
                if (!_values.Remove(key)) return;
            }
            else
            {
                if (_values.TryGetValue(key, out var current) && current == value) return;
                _values[key] = value;
            }
            AtomicFile.WriteAllBytes(_path, Utf8NoBom.GetBytes(JsonSerializer.Serialize(_values)));
        }
    }

    /// <summary>
    /// After "Disconnect and wipe": every key was already set to null; the file itself goes too (and any leftover of an interrupted atomic
    /// write next to it), so that nothing of the blind copy remains. A later <see cref="Set"/> starts a new file.
    /// </summary>
    public void Erase()
    {
        lock (_gate)
        {
            _values.Clear();
            if (File.Exists(_path)) File.Delete(_path);
            var folder = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (folder is not null && Directory.Exists(folder))
                foreach (var leftover in Directory.GetFiles(folder, Path.GetFileName(_path) + ".*.tmp"))
                    File.Delete(leftover);
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(_path));
            if (stored is null) throw new JsonException("The state file is not an object.");
            foreach (var (key, value) in stored)
                if (value is not null) _values[key] = value;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _values.Clear();
            WasUnreadable = true;
        }
    }
}
