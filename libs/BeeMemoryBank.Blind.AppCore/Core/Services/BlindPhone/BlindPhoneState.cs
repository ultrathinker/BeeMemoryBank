using System.Buffers.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services.BlindPhone;

/// <summary>How often the phone makes a backup on its own.</summary>
public enum BlindBackupSchedule { Off, Daily, Weekly }

/// <summary>
/// What the Android blind node remembers between runs, typed over <see cref="IBlindPhoneStore"/>.
/// Nothing secret lives here — the keys are in <see cref="IBlindPhoneKeys"/>.
/// </summary>
public sealed class BlindPhoneState(IBlindPhoneStore store)
{
    private const string P = "bmb.blind.";

    public Guid? NodeId
    {
        get => Guid.TryParse(store.Get(P + "node_id"), out var id) ? id : null;
        set => store.Set(P + "node_id", value?.ToString("D"));
    }

    public byte[]? PublicKey
    {
        get => store.Get(P + "public_key") is { } k ? Base64Url.DecodeFromChars(k) : null;
        set => store.Set(P + "public_key", value is null ? null : Base64Url.EncodeToString(value));
    }

    public string? DisplayName
    {
        get => store.Get(P + "name");
        set => store.Set(P + "name", value);
    }

    /// <summary>The accepted "where to call" code; null until paired.</summary>
    public BlindCallCode? CallCode
    {
        get => BlindCallCode.TryParse(store.Get(P + "call_code"), out var c) ? c : null;
        set => store.Set(P + "call_code", value?.ToString());
    }

    public bool InitialLoadDone
    {
        get => store.Get(P + "initial_load_done") == "1";
        set => store.Set(P + "initial_load_done", value ? "1" : null);
    }

    public DateTimeOffset? LastSyncAt { get => Time("last_sync"); set => SetTime("last_sync", value); }
    public DateTimeOffset? LastBackupAt { get => Time("last_backup"); set => SetTime("last_backup", value); }

    public BlindBackupSchedule Schedule
    {
        get => Enum.TryParse<BlindBackupSchedule>(store.Get(P + "schedule"), out var s) ? s : BlindBackupSchedule.Weekly;
        set => store.Set(P + "schedule", value.ToString());
    }

    /// <summary>File name of the backup being written, so an interrupted run resumes the same file.</summary>
    public string? PendingBackupName
    {
        get => store.Get(P + "pending_backup");
        set => store.Set(P + "pending_backup", value);
    }

    /// <summary>True when a scheduled backup is due at <paramref name="now"/>.</summary>
    public bool BackupDue(DateTimeOffset now) => Schedule switch
    {
        BlindBackupSchedule.Off => false,
        BlindBackupSchedule.Daily => LastBackupAt is not { } d || now - d >= TimeSpan.FromDays(1),
        _ => LastBackupAt is not { } w || now - w >= TimeSpan.FromDays(7),
    };

    /// <summary>Forgets everything (Disconnect and wipe).</summary>
    public void Clear()
    {
        foreach (var key in new[] { "node_id", "public_key", "name", "call_code", "initial_load_done",
                     "last_sync", "last_backup", "schedule", "pending_backup" })
            store.Set(P + key, null);
    }

    private DateTimeOffset? Time(string key) =>
        DateTimeOffset.TryParse(store.Get(P + key), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;

    private void SetTime(string key, DateTimeOffset? value) => store.Set(P + key, value?.ToString("O"));
}

/// <summary>
/// The Android blind node's own log for its screen (plan section 10): one JSON line per event, newest
/// last, capped so it never grows without bound.
/// </summary>
public sealed class BlindPhoneLog(string path, TimeProvider time)
{
    public const int MaxEntries = 500;
    private readonly object _gate = new();

    public sealed record Entry(DateTimeOffset At, string Kind, string Message);

    public void Add(string kind, string message)
    {
        lock (_gate)
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
            lines.Add(JsonSerializer.Serialize(new Entry(time.GetUtcNow(), kind, message)));
            if (lines.Count > MaxEntries) lines.RemoveRange(0, lines.Count - MaxEntries);
            var tmp = path + ".tmp";
            File.WriteAllLines(tmp, lines);
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>The newest <paramref name="count"/> entries, newest first. A damaged line is skipped.</summary>
    public IReadOnlyList<Entry> Latest(int count)
    {
        lock (_gate)
        {
            if (!File.Exists(path)) return [];
            var result = new List<Entry>();
            foreach (var line in File.ReadAllLines(path).Reverse())
            {
                if (result.Count >= count) break;
                try { if (JsonSerializer.Deserialize<Entry>(line) is { } e) result.Add(e); }
                catch (JsonException) { }
            }
            return result;
        }
    }
}
