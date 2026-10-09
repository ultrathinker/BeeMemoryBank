using System.Globalization;
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
/// <para>The file is read on first use and kept. A file that is DAMAGED (not JSON, not an object) counts as empty and a copy of it is
/// kept next to it (<c>.damaged-*</c>, best effort) before the first write replaces it: the identity, which is checked against the
/// database and the DPAPI secrets, is then recovered, and the user pairs again - nothing is guessed. A file that cannot be READ for now
/// (an antivirus or a sync client has it open, access denied) is not damage: it is tried again a few times, and then the read throws
/// <see cref="IOException"/> and the next call tries again. The store never starts empty over a file it could not read, because the
/// next write would replace the real state with it.</para>
///
/// <para>A write that fails throws and leaves the in-memory state as it was.</para>
/// </summary>
public sealed class AtomicJsonStateStore : IBlindStateStore
{
    private const int ReadAttempts = 5;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan DefaultReadRetryDelay = TimeSpan.FromMilliseconds(200);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeSpan _readRetryDelay;
    private SortedDictionary<string, string>? _values;
    private bool _wasUnreadable;

    /// <param name="path">The state file.</param>
    /// <param name="readRetryDelay">The wait between the attempts to read a file that another process holds (tests make it short).</param>
    public AtomicJsonStateStore(string path, TimeSpan? readRetryDelay = null)
    {
        _path = path;
        _readRetryDelay = readRetryDelay ?? DefaultReadRetryDelay;
    }

    /// <summary>True when an existing file was damaged and was treated as empty (a copy of it was kept when possible).</summary>
    /// <exception cref="IOException">The file cannot be read at the moment.</exception>
    public bool WasUnreadable
    {
        get
        {
            lock (_gate)
            {
                Values();
                return _wasUnreadable;
            }
        }
    }

    public string? Get(string key)
    {
        lock (_gate) return Values().GetValueOrDefault(key);
    }

    public void Set(string key, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            var next = new SortedDictionary<string, string>(Values(), StringComparer.Ordinal);
            if (value is null)
            {
                if (!next.Remove(key)) return;
            }
            else
            {
                if (next.TryGetValue(key, out var current) && current == value) return;
                next[key] = value;
            }
            AtomicFile.WriteAllBytes(_path, Utf8NoBom.GetBytes(JsonSerializer.Serialize(next)));
            _values = next;
        }
    }

    /// <summary>
    /// After "Disconnect and wipe": every key was already set to null; the file itself goes too (and any leftover of an interrupted atomic
    /// write and any copy of a damaged file next to it), so that nothing of the blind copy remains. A later <see cref="Set"/> starts a new file.
    /// </summary>
    public void Erase()
    {
        lock (_gate)
        {
            _values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            _wasUnreadable = false;
            if (File.Exists(_path)) File.Delete(_path);
            var folder = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (folder is not null && Directory.Exists(folder))
            {
                var name = Path.GetFileName(_path);
                foreach (var leftover in Directory.GetFiles(folder, name + ".*.tmp")) File.Delete(leftover);
                foreach (var copy in Directory.GetFiles(folder, name + ".damaged-*")) File.Delete(copy);
            }
        }
    }

    private SortedDictionary<string, string> Values() => _values ??= Load();

    private SortedDictionary<string, string> Load()
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(_path)) return values;
        var bytes = ReadWithRetry();
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes);
            if (stored is null) throw new JsonException("The state file is not an object.");
            foreach (var (key, value) in stored)
                if (value is not null) values[key] = value;
            return values;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            KeepDamagedCopy();
            _wasUnreadable = true;
            return new SortedDictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Reads the file; a file another process holds is tried again a few times, then the error is thrown (never an empty state).</summary>
    private byte[] ReadWithRetry()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllBytes(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= ReadAttempts)
                {
                    if (ex is IOException) throw;
                    throw new IOException("The state file cannot be read at the moment (access denied).", ex);
                }
                if (_readRetryDelay > TimeSpan.Zero) Thread.Sleep(_readRetryDelay);
            }
        }
    }

    /// <summary>A courtesy, never a condition: a copy that cannot be made must not stop the start (the recovery of the identity depends on it).</summary>
    private void KeepDamagedCopy()
    {
        try
        {
            File.Copy(_path, _path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture), overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            // Not kept; the store starts empty all the same.
        }
    }
}
