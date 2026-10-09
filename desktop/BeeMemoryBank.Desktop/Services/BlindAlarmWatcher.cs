using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>Where the watcher keeps which alarm episodes it has already notified, per profile, so a restart does not notify them again.</summary>
public interface IBlindAlarmEpisodeStore
{
    /// <summary>Episode key -> when it was last notified (UTC).</summary>
    IReadOnlyDictionary<string, DateTime> Load(string profileId);

    void Save(string profileId, IReadOnlyDictionary<string, DateTime> notified);
}

/// <summary>The episodes in desktop-settings.json, under one key, one object per profile.</summary>
public sealed class DesktopSettingsEpisodeStore(DesktopSettingsStore settings) : IBlindAlarmEpisodeStore
{
    public const string Key = "blindAlarmEpisodes";

    public IReadOnlyDictionary<string, DateTime> Load(string profileId)
    {
        var result = new Dictionary<string, DateTime>();
        try
        {
            if (settings.GetNode(Key)?[profileId] is JsonObject episodes)
                foreach (var (key, value) in episodes)
                    if (value is JsonValue v && v.TryGetValue<DateTime>(out var at))
                        result[key] = DateTime.SpecifyKind(at, DateTimeKind.Utc);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // An unreadable entry is a forgotten one: at worst an alarm is notified once more.
        }
        return result;
    }

    public void Save(string profileId, IReadOnlyDictionary<string, DateTime> notified)
    {
        var all = settings.GetNode(Key) as JsonObject ?? new JsonObject();
        var episodes = new JsonObject();
        foreach (var (key, at) in notified) episodes[key] = at.ToString("O", CultureInfo.InvariantCulture);
        if (episodes.Count == 0) all.Remove(profileId);
        else all[profileId] = episodes;
        settings.SetNode(Key, all.Count == 0 ? null : all);
    }
}

/// <summary>
/// Turns the node's blind-node alarms (<c>GET /node/alarms</c>, BMB-77) into notifications: one per episode, a reminder at most every
/// <see cref="RemindEvery"/>, and the tray's "N blind nodes need attention".
///
/// <para><b>Episodes.</b> An alarm is one episode for as long as its kind, node and time stay the same (the node makes them stable; the
/// protocol a blind node declares is not part of it). It is raised after <see cref="PollsToRaise"/> polls in a row that report it, and
/// cleared after <see cref="PollsToClear"/> judged polls in a row that do not, so one odd answer neither notifies nor clears anything. An
/// alarm the node reports as not worth a notification (the PC slept, and its failure streak restarted) holds an open episode as it is.
/// The episodes notified are kept per profile (<see cref="IBlindAlarmEpisodeStore"/>), so a restart of the app does not notify them again.</para>
///
/// <para><b>Quiet.</b> Nothing changes on a poll that failed, or that the node did not judge (invisible mode; no sync cycle completed
/// yet since the node started or the computer woke): no answer is never an alarm, and never a clearing either. No notification is shown
/// for <see cref="StartGrace"/> after the watcher starts, the profile changes or the computer wakes (a gap of <see cref="ResumeGap"/>
/// between two polls); the episodes are still tracked, so the tray already says what is wrong. At most <see cref="MaxNoticesPerBurst"/>
/// notices go out at once; the rest are summed up in the last one.</para>
///
/// <para><b>Texts.</b> Fixed sentences per kind, written here; the node only names the kind. A locked vault reports no names, and the
/// texts then say "a blind node".</para>
/// </summary>
public sealed class BlindAlarmWatcher : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan StartGrace = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RemindEvery = TimeSpan.FromHours(24);

    /// <summary>How long a notice that could not be shown waits before it is tried again (the shell may be restarting).</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ResumeGap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The pause between the notices of one burst. A second balloon on the same icon within moments replaces the first on screen, so
    /// notices sent back to back would show only the last; this lets each one be read.
    /// </summary>
    public static readonly TimeSpan NoticeGap = TimeSpan.FromSeconds(6);
    public const int PollsToRaise = 2;
    public const int PollsToClear = 2;
    public const int MaxNoticesPerBurst = 3;

    private static readonly HashSet<string> KnownKinds = ["silent", "old_protocol", "pc_too_old"];

    private sealed class Episode
    {
        public required BlindAlarmEntry Alarm;
        public int Seen;
        public int Clean;
        public DateTime? NotifiedAt;

        /// <summary>Not stored: after a notice that could not be shown, the earliest time to try it again.</summary>
        public DateTime? RetryAt;
    }

    private readonly Func<CancellationToken, Task<NodeAlarmsPoll>> _poll;
    private readonly IUserNotifier? _notifier;
    private readonly IBlindAlarmEpisodeStore _store;
    private readonly Func<string?> _profileId;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly TimeSpan _noticeGap;
    private readonly Dictionary<string, Episode> _episodes = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private CancellationTokenSource? _loop;
    private string? _profile;
    private DateTime _graceUntil;
    private DateTime? _lastPollAt;
    private volatile int _attention;

    /// <param name="poll">One poll of the node (<see cref="NodeAlarmsRequest.PollAsync"/>); it never throws.</param>
    /// <param name="notifier">The system's notice (<see cref="IShellPlatform.CreateNotifier"/>); null: the tray tooltip only.</param>
    /// <param name="store">The notified episodes, per profile.</param>
    /// <param name="profileId">The open profile, read at each poll.</param>
    /// <param name="time">The clock; null means the system clock.</param>
    /// <param name="log">Where a failed notice is logged; null means the standard error stream.</param>
    /// <param name="noticeGap">The pause between the notices of one burst; null means <see cref="NoticeGap"/>.</param>
    public BlindAlarmWatcher(
        Func<CancellationToken, Task<NodeAlarmsPoll>> poll, IUserNotifier? notifier, IBlindAlarmEpisodeStore store,
        Func<string?> profileId, TimeProvider? time = null, Action<string>? log = null, TimeSpan? noticeGap = null)
    {
        _noticeGap = noticeGap ?? NoticeGap;
        _poll = poll ?? throw new ArgumentNullException(nameof(poll));
        _notifier = notifier;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _profileId = profileId ?? throw new ArgumentNullException(nameof(profileId));
        _time = time ?? TimeProvider.System;
        _log = log ?? Console.Error.WriteLine;
    }

    /// <summary>How many blind nodes have a raised alarm now (for the tray).</summary>
    public int AttentionCount => _attention;

    /// <summary>Raised when <see cref="AttentionCount"/> changes; on the polling thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The tray tooltip's addition, or null when nothing needs attention.</summary>
    public static string? TooltipSuffix(int count) => count switch
    {
        <= 0 => null,
        1 => "1 blind node needs attention",
        _ => $"{count} blind nodes need attention",
    };

    /// <summary>Starts polling every <see cref="PollInterval"/> in the background (a no-op when it already runs).</summary>
    public void Start()
    {
        if (_loop != null) return;
        _loop = new CancellationTokenSource();
        var token = _loop.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(PollInterval, _time);
            try
            {
                do
                {
                    try { await PollOnceAsync(token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Log($"An alarms poll failed ({ex.GetType().Name})."); }
                }
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) { /* stopped */ }
        }, token);
    }

    /// <summary>One poll and what follows from it. The background loop calls it; tests call it directly.</summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = UtcNow();
            var profile = _profileId() ?? "";
            if (profile != _profile)
            {
                // Another profile is another network: its own episodes, and a fresh start.
                _profile = profile;
                _episodes.Clear();
                foreach (var (key, at) in _store.Load(profile))
                    _episodes[key] = new Episode { Alarm = Restored(key), NotifiedAt = at };
                _graceUntil = now + StartGrace;
                SetAttention(0);
            }
            else if (_lastPollAt is { } last && now - last > ResumeGap)
            {
                _graceUntil = now + StartGrace; // the computer slept (or the app hung): start quietly again
            }
            _lastPollAt = now;

            var result = await _poll(cancellationToken).ConfigureAwait(false);
            if (result.Answer is not { State: BlindAlarmsAnswer.Judged } answer) return;

            var changed = Track(answer.Alarms.Where(a => KnownKinds.Contains(a.Kind)));
            var raised = _episodes.Values.Where(e => e.Seen >= PollsToRaise).ToList();
            SetAttention(raised.Select(e => e.Alarm.NodeId).Distinct().Count());

            if (now >= _graceUntil)
            {
                var due = raised.Where(e => (e.NotifiedAt is not { } at || now - at >= RemindEvery) && (e.RetryAt is not { } retry || now >= retry))
                    .OrderBy(e => e.Alarm.Kind, StringComparer.Ordinal).ThenBy(e => e.Alarm.NodeId).ToList();
                if (due.Count > 0)
                {
                    // Marked before the notices go out: a poll cut short in the pause between two of them must not send the first again.
                    // Then the mark is taken back from every notice that was not shown (it could not be, or the poll was cut short before
                    // it): that one is not "notified", and is tried again after RetryEvery.
                    var before = due.ToDictionary(e => e, e => e.NotifiedAt);
                    foreach (var episode in due) episode.NotifiedAt = now;
                    Save();
                    var shown = new HashSet<Episode>();
                    try
                    {
                        await ShowAsync(due, shown, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        foreach (var episode in due.Where(e => !shown.Contains(e)))
                        {
                            episode.NotifiedAt = before[episode];
                            episode.RetryAt = now + RetryEvery;
                        }
                        foreach (var episode in shown) episode.RetryAt = null;
                        Save();
                    }
                    changed = false;
                }
            }
            if (changed) Save();
        }
        finally
        {
            _pollGate.Release();
        }
    }

    /// <summary>
    /// Counts the polls each episode was seen and not seen; returns whether the set of notified episodes changed. An alarm that is not
    /// worth a notification (<see cref="BlindAlarmEntry.Notify"/> false) is the node saying "still failing, below the threshold again"
    /// (a sleep of the PC restarts the failure streak): it holds an episode that is open, neither counting it as seen nor letting it
    /// clear, and starts none.
    /// </summary>
    private bool Track(IEnumerable<BlindAlarmEntry> alarms)
    {
        var present = new HashSet<string>();
        foreach (var alarm in alarms)
        {
            var key = KeyOf(alarm);
            if (!alarm.Notify)
            {
                if (_episodes.TryGetValue(key, out var open))
                {
                    open.Alarm = alarm;
                    open.Clean = 0;
                    present.Add(key);
                }
                continue;
            }
            if (!present.Add(key)) continue;
            if (!_episodes.TryGetValue(key, out var episode)) _episodes[key] = episode = new Episode { Alarm = alarm };
            episode.Alarm = alarm; // the name comes and goes with the lock
            episode.Seen++;
            episode.Clean = 0;
        }

        var changed = false;
        foreach (var (key, episode) in _episodes.ToList())
        {
            if (present.Contains(key)) continue;
            episode.Seen = 0;
            if (++episode.Clean < PollsToClear) continue;
            _episodes.Remove(key);
            changed |= episode.NotifiedAt is not null;
        }
        return changed;
    }

    /// <summary>Shows the notices of one burst; <paramref name="shown"/> gets the episodes whose notice was shown (all of a summary's, when it was).</summary>
    private async Task ShowAsync(IReadOnlyList<Episode> due, ISet<Episode> shown, CancellationToken cancellationToken)
    {
        var notices = due.Count <= MaxNoticesPerBurst
            ? due.Select(e => (Text: TextFor(e.Alarm), Covers: (IReadOnlyList<Episode>)[e])).ToList()
            : due.Take(MaxNoticesPerBurst - 1).Select(e => (Text: TextFor(e.Alarm), Covers: (IReadOnlyList<Episode>)[e]))
                .Append((Text: (Title: "Blind nodes need attention", Message:
                        $"{due.Count - (MaxNoticesPerBurst - 1)} more blind-node alarms. Open Bee Memory Bank, Blind nodes, to see them."),
                    Covers: (IReadOnlyList<Episode>)due.Skip(MaxNoticesPerBurst - 1).ToList()))
                .ToList();
        for (var i = 0; i < notices.Count; i++)
        {
            if (i > 0 && _noticeGap > TimeSpan.Zero) await Task.Delay(_noticeGap, cancellationToken).ConfigureAwait(false);
            // No notifier: the tray tooltip is all there is, and that counts as the notice (nothing to try again).
            var wasShown = true;
            try { wasShown = _notifier?.TryNotify(notices[i].Text.Title, notices[i].Text.Message) ?? true; }
            catch (Exception ex)
            {
                wasShown = false;
                Log($"A notice could not be shown ({ex.GetType().Name}).");
            }
            if (wasShown)
                foreach (var episode in notices[i].Covers) shown.Add(episode);
        }
    }

    /// <summary>The fixed title and text of an alarm's notice. Names only when the node gave one (it does not while the vault is locked).</summary>
    public static (string Title, string Message) TextFor(BlindAlarmEntry alarm)
    {
        var name = string.IsNullOrWhiteSpace(alarm.Name) ? null : $"\"{alarm.Name.Trim()}\"";
        return alarm.Kind switch
        {
            "old_protocol" => ("A blind node needs an update",
                $"{name ?? "A blind node"} runs an older version of Bee Memory Bank and receives nothing until it is updated."),
            "pc_too_old" => ("Update Bee Memory Bank on this computer",
                $"{name ?? "A blind node"} runs a newer version. Until this computer is updated, it takes nothing from that blind node."),
            _ => ("A blind node is not in contact",
                name is null
                    ? "A blind node has not been in contact for a long time. Open Bee Memory Bank, Blind nodes, to see which."
                    : alarm.Since is { } since
                        ? $"No contact with {name} since {since.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}. Its copies and backups stop while it is away."
                        : $"No contact with {name} for a long time. Its copies and backups stop while it is away."),
        };
    }

    // The protocol the node declared is not part of the key: a blind node that keeps changing its answer would otherwise start a new
    // episode, and with it a new notice, every few polls. One "needs an update" per node and kind, until it is gone for good.
    private static string KeyOf(BlindAlarmEntry alarm) => string.Join('|',
        alarm.Kind, alarm.NodeId.ToString("D"),
        alarm.Since?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "");

    /// <summary>A stand-in for an episode known only from the store: enough to be counted and cleared, never shown as it is.</summary>
    private static BlindAlarmEntry Restored(string key)
    {
        var parts = key.Split('|');
        var nodeId = parts.Length > 1 && Guid.TryParse(parts[1], out var id) ? id : Guid.Empty;
        return new BlindAlarmEntry(parts[0], nodeId, null, null, null, Notify: true, Banner: false);
    }

    private void Save()
    {
        try
        {
            _store.Save(_profile ?? "", _episodes.Where(e => e.Value.NotifiedAt is not null)
                .ToDictionary(e => e.Key, e => e.Value.NotifiedAt!.Value));
        }
        catch (Exception ex)
        {
            Log($"The notified blind-node alarms could not be saved ({ex.GetType().Name}).");
        }
    }

    private void SetAttention(int count)
    {
        if (_attention == count) return;
        _attention = count;
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log($"A tray update failed ({ex.GetType().Name})."); }
    }

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    private void Log(string line)
    {
        try { _log($"[BlindAlarmWatcher] {line}"); }
        catch { /* nothing to do */ }
    }

    public void Dispose()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
    }
}
