using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.BlindMobile.Services.Blind;
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

    /// <summary>
    /// The accepted "where to call" code; null until paired. A refusal remembered (<see cref="SourceRefusedAt"/>) was given by the computer this code named: a new code is a new pairing, and its
    /// first refusal is a first one, so setting a code forgets the remembered time.
    /// </summary>
    public BlindCallCode? CallCode
    {
        get => BlindCallCode.TryParse(store.Get(P + "call_code"), out var c) ? c : null;
        set
        {
            store.Set(P + "call_code", value?.ToString());
            store.Set(P + "source_refused_at", null);
        }
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

    /// <summary>When the source was last told that the person paused this copy (a pause the source is told about); null when no such pause is going on.</summary>
    public DateTimeOffset? PauseReportedAt { get => Time("pause_reported_at"); set => SetTime("pause_reported_at", value); }

    /// <summary>The reason of the pause the source was last told about.</summary>
    public string? PauseReportedReason
    {
        get => store.Get(P + "pause_reported_reason");
        set => store.Set(P + "pause_reported_reason", value);
    }

    /// <summary>
    /// When the source first answered this device with a refusal (401 or 403) and the answer has not been followed by a successful contact since; null when none is remembered. ONE refusal is not
    /// "removed by the owner" (a proxy or a firewall in front of a healthy source can say no for a moment): <see cref="BlindSourceRemoval.Judge"/> decides from this time and the next refusal's.
    /// An additive key: 2.6.4 never reads it, and a state written before it existed simply has none.
    /// </summary>
    public DateTimeOffset? SourceRefusedAt { get => Time("source_refused_at"); set => SetTime("source_refused_at", value); }

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
                     "last_sync", "last_backup", "schedule", "pending_backup", "pause_reported_at", "pause_reported_reason", "source_refused_at" })
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

    /// <param name="Version">
    /// The stamp of the code that wrote the line (<see cref="BlindFailureText.StoredVersion"/>, stored as <c>v</c>). A line without it was written by 2.6.4 or earlier, which stored the raw message of an
    /// exception: only its time and kind are shown. An entry made in memory by this code is stamped; one read from the file is stamped only if the file says so.
    /// </param>
    public sealed record Entry(DateTimeOffset At, string Kind, string Message, [property: JsonPropertyName("v")] int Version = BlindFailureText.StoredVersion)
    {
        /// <summary>
        /// The line as a screen may show it: a line this code did not stamp shows the fixed sentence (<see cref="BlindFailureText.ForDisplay(string?, int)"/>); a stamped line goes through the cheap
        /// filter of <see cref="BlindFailureText.ForDisplay(string?)"/>; a stamped <see cref="PairingKind"/> line is shown only if it has the exact shape this code writes (<see cref="BlindPairingLine"/>).
        /// Idempotent.
        /// </summary>
        public Entry ForDisplay() =>
            Kind == PairingKind && Version == BlindFailureText.StoredVersion
                ? this with { Message = BlindPairingLine.IsOwn(Message) ? Message : BlindFailureText.StoredTextHidden }
                : this with { Message = BlindFailureText.ForDisplay(Message, Version) };
    }

    /// <summary>What a file holds for one line: the stamp is absent in everything 2.6.4 wrote, and absent must not read as "written by this version".</summary>
    private sealed record StoredLine(DateTimeOffset At, string? Kind, string? Message, [property: JsonPropertyName("v")] int? Version);

    public void Add(string kind, string message)
    {
        lock (_gate)
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
            lines.Add(JsonSerializer.Serialize(new Entry(time.GetUtcNow(), kind, message)));
            if (lines.Count > MaxEntries) lines.RemoveRange(0, lines.Count - MaxEntries);
            // A name of its own each time: a leftover of a crash, or a second writer, never shares it.
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(tmp, lines);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(tmp); } catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
    }

    /// <summary>
    /// The kind of the lines that say who this copy is paired with: written by this code from its own data (the address of the computer it calls), never from an exception. The kind is stored
    /// data and proves nothing by itself: such a line is shown only when it is stamped by this version and has the exact shape of <see cref="BlindPairingLine"/>.
    /// </summary>
    public const string PairingKind = "pairing";

    /// <summary>
    /// The newest <paramref name="count"/> entries, newest first. A damaged line is skipped. What is read back is made fit for a screen by PROVENANCE, not by guessing at the text: a line that
    /// carries the stamp of this version (<see cref="BlindFailureText.StoredVersion"/>) is shown after the cheap filter; a line without it (everything 2.6.4 wrote: a path, a key alias, a request
    /// address, a library's words) keeps its time and kind and shows <see cref="BlindFailureText.StoredTextHidden"/>. The file is not rewritten.
    /// </summary>
    public IReadOnlyList<Entry> Latest(int count) => Read(count, forDisplay: true);

    /// <summary>The newest entries exactly as they are stored: for the code that compares a line it is about to write with the last one (a screen never gets these).</summary>
    internal IReadOnlyList<Entry> LatestStored(int count) => Read(count, forDisplay: false);

    private IReadOnlyList<Entry> Read(int count, bool forDisplay)
    {
        lock (_gate)
        {
            if (!File.Exists(path)) return [];
            var result = new List<Entry>();
            foreach (var line in File.ReadAllLines(path).Reverse())
            {
                if (result.Count >= count) break;
                try
                {
                    if (JsonSerializer.Deserialize<StoredLine>(line) is not { } stored) continue;
                    var (at, kind, message, version) = stored;
                    var entry = new Entry(at, kind ?? "", message ?? "", version ?? 0);
                    result.Add(forDisplay ? (HasOnlyKnownPropertiesOnce(line) ? entry : entry with { Version = 0 }).ForDisplay() : entry);
                }
                catch (JsonException) { }
            }
            return result;
        }
    }

    /// <summary>
    /// A line with an unknown or repeated member cannot prove that its stamp means what this version writes. JSON object members are otherwise last-wins, which would let a later duplicate
    /// <c>v</c> turn an untrusted line into one that appears current. Keep the stored shape deliberately closed for display; <see cref="LatestStored"/> still returns the raw line for write
    /// comparisons.
    /// </summary>
    private static bool HasOnlyKnownPropertiesOnce(string line)
    {
        using var document = JsonDocument.Parse(line);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (property.Name is not ("At" or "Kind" or "Message" or "v") || !seen.Add(property.Name)) return false;
        return true;
    }
}
