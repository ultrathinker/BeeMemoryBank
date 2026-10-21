using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>A local notification the phone schedules for itself.</summary>
public sealed record BlindSilenceNotice(DateTimeOffset FireAt, string Title, string Body);

/// <summary>What a <see cref="BlindSilencePlanner"/> found to do: notifications to (re)schedule, by request id, and request ids to withdraw.</summary>
public sealed record SilenceChanges(IReadOnlyList<(string RequestId, BlindSilenceNotice Notice)> Set, IReadOnlyList<string> Remove)
{
    public bool IsEmpty => Set.Count == 0 && Remove.Count == 0;
}

/// <summary>
/// "The node has been silent too long", without a push server, ONE notification per account: every time an account hears from its node (a successful sync), the app
/// moves that account's pending local notification to that moment plus <see cref="SilentAfter"/>. While the node answers, the notification keeps
/// moving ahead and never shows; when the node goes silent - or iOS stops giving the app background time, which looks the same from the
/// phone - nothing moves it any more and iOS shows it on time, even if the app never runs again. The desktop apps raise the same alarm
/// for a blind copy that has not called for three days.
///
/// <para>Account 1 keeps the request id the 2.6 app used (<see cref="RequestId"/>), so the notification an upgraded phone already has pending is simply moved on; another account's
/// id carries the account's id. An account that is paused (by a limit or by the person) is not silent, it was told to stop: its card says so and no warning is planned for it.</para>
/// </summary>
public static class BlindSilenceAlarm
{
    public const string RequestId = "bmb-blind-silent";

    /// <summary>How long without contact before the phone says so (the desktop alarm's three days).</summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromDays(3);

    /// <summary>When the silence has already lasted that long, the next reminder is not sooner than this.</summary>
    public static readonly TimeSpan RemindAfter = TimeSpan.FromDays(1);

    /// <summary>The request id of the account's notification: <see cref="RequestId"/> for account 1 (as in 2.6), <c>bmb-blind-silent.&lt;id&gt;</c> for the others.</summary>
    public static string RequestIdFor(BlindAccountEntry account) => account.IsLegacy ? RequestId : RequestId + "." + account.Id;

    /// <summary>
    /// The notification that should be pending now for <paramref name="account"/>, or null when none should: the account is not active, not paired (there is no node to miss) or held
    /// by a limit or by the person. A paired copy that has never synced counts from <paramref name="now"/>: the time it was last seen running.
    /// </summary>
    public static BlindSilenceNotice? Plan(BlindAccountSnapshot account, DateTimeOffset now)
    {
        var entry = account.Entry;
        if (entry.Phase != BlindAccountPhases.Active || account.Status is not { IsPaired: true } status) return null;
        if (entry.PausedByUser || (account.Pause is { } pause && !BlindPauseText.IsWaiting(pause) && !BlindPauseText.IsFault(pause))) return null;

        var heardAt = status.LastSyncAt ?? now;
        var fireAt = heardAt + SilentAfter;
        // Already silent that long (the app was opened, or woken, without reaching the node): say it again a day later, not at once and
        // not on every refresh.
        if (fireAt <= now) fireAt = now + RemindAfter;
        var days = Math.Max(3, (int)Math.Floor((fireAt - heardAt).TotalDays));
        var since = status.LastSyncAt is { } last
            ? $"since {last.ToLocalTime():dd.MM.yyyy HH:mm}"
            : "since it was paired";
        // An account that came from an invitation is named after its source ("Anna's server"); one paired the old way is "the node", as before.
        var title = entry.InviteId is not null
            ? $"No contact with {entry.Name}'s server for {days} days"
            : $"Blind copy: no contact with the node for {days} days";
        return new BlindSilenceNotice(fireAt, title,
            $"This iPhone has not synced \"{entry.Name}\" with its node {since}. Its copy may be out of date: open the app so it can sync, and check that the computer or server it calls is on.");
    }

    /// <summary>The pending requests of the app's silence notifications that belong to no listed account (an account was removed while the app was not running): to be withdrawn.</summary>
    public static IReadOnlyList<string> StaleRequestIds(IEnumerable<string> pendingIds, IEnumerable<BlindAccountEntry> accounts)
    {
        var live = accounts.Select(RequestIdFor).ToHashSet(StringComparer.Ordinal);
        return pendingIds
            .Where(id => id == RequestId || id.StartsWith(RequestId + ".", StringComparison.Ordinal))
            .Where(id => !live.Contains(id))
            .ToList();
    }
}

/// <summary>
/// Keeps the per-account notifications in step with the accounts: it moves a notification only when what it depends on changed (the account's phase, whether it is paired or held,
/// its name, the time of its last sync), so a screen that refreshes every few seconds does not keep pushing it away, and it withdraws the notification of an account that is gone or no
/// longer needs one. Pure: <c>IosSilenceNotifier</c> applies what it returns to the notification center.
/// </summary>
public sealed class BlindSilencePlanner
{
    private readonly Dictionary<string, PlanKey> _planned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _fireAt = new(StringComparer.Ordinal);

    private sealed record PlanKey(string Phase, bool Paired, bool Held, DateTimeOffset? LastSync, string Name);

    /// <summary>What to change for this snapshot, remembering it as planned.</summary>
    public SilenceChanges Update(BlindAccountsSnapshot snapshot, DateTimeOffset now)
    {
        var set = new List<(string, BlindSilenceNotice)>();
        var remove = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // A damaged list says nothing about the accounts: nothing is moved or withdrawn on its word.
        if (snapshot.RegistryProblem is not null) return new SilenceChanges(set, remove);

        foreach (var account in snapshot.Accounts)
        {
            var id = BlindSilenceAlarm.RequestIdFor(account.Entry);
            seen.Add(id);
            var notice = BlindSilenceAlarm.Plan(account, now);
            var key = new PlanKey(
                account.Entry.Phase,
                account.Status?.IsPaired == true,
                account.Entry.PausedByUser || account.Pause is not null,
                account.Status?.LastSyncAt,
                account.Entry.Name);
            if (_planned.TryGetValue(id, out var known) && known == key) continue;
            _planned[id] = key;
            if (notice is null)
            {
                _fireAt.Remove(id);
                remove.Add(id);
            }
            else
            {
                _fireAt[id] = notice.FireAt;
                set.Add((id, notice));
            }
        }

        foreach (var gone in _planned.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _planned.Remove(gone);
            _fireAt.Remove(gone);
            remove.Add(gone);
        }
        return new SilenceChanges(set, remove);
    }

    /// <summary>
    /// The notification center says which requests are still pending. A request that was planned for a moment that has come and is no longer pending has FIRED (iOS removes a request when it shows it):
    /// its account's planned key is dropped, so that the next update plans the next reminder - a silence that lasts is said again every <see cref="BlindSilenceAlarm.RemindAfter"/>, not once. A request
    /// whose time has not come is left alone (it is pending for iOS to show, or the person removed it). Returns the request ids that fired.
    /// </summary>
    public IReadOnlyList<string> NoteFired(IEnumerable<string> pendingIds, DateTimeOffset now)
    {
        var pending = pendingIds.ToHashSet(StringComparer.Ordinal);
        var fired = _fireAt.Where(p => p.Value <= now && !pending.Contains(p.Key)).Select(p => p.Key).ToList();
        foreach (var id in fired)
        {
            _planned.Remove(id);
            _fireAt.Remove(id);
        }
        return fired;
    }

    /// <summary>The add of this request failed: it is not pending after all. Its plan is forgotten, so that the next update plans it again even when nothing it depends on changed.</summary>
    public void Invalidate(string requestId)
    {
        _planned.Remove(requestId);
        _fireAt.Remove(requestId);
    }

    /// <summary>Forget what was planned: the next update plans everything again (alerts were just allowed, and nothing could be queued before).</summary>
    public void Forget()
    {
        _planned.Clear();
        _fireAt.Clear();
    }
}
