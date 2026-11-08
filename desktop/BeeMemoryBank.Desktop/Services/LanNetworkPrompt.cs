using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>A network the person has been asked about and has not answered: an item of the tray menu until they do.</summary>
public sealed record LanQuestion(string Fingerprint, string Label);

/// <summary>What the tray menu shows of LAN sync.</summary>
/// <param name="Status">"trusted", "not trusted", "off", or a count when the networks differ; null when there is nothing to say (no answer from the node yet, an older node, no network).</param>
/// <param name="Questions">The networks the person was asked about and has not answered.</param>
public sealed record LanTrayState(string? Status, IReadOnlyList<LanQuestion> Questions)
{
    public static readonly LanTrayState Empty = new(null, []);

    /// <summary>The tray item's text, "Sync on this network: trusted (Change...)"; null when there is no status.</summary>
    public string? Header => Status is null ? null : $"Sync on this network: {Status} (Change...)";
}

/// <summary>
/// LAN sync as the shell shows it (docs/design/lan-peer-sync.md 6, 13 D1 and D9): it asks the person ONCE per network whether to sync with
/// the paired devices there, and shows the one-time notice that direct sync is on. It polls the node's <c>GET /node/lan</c> every minute (at
/// once after a network change, and within a few seconds of a wake: the loop watches the wall clock).
///
/// <para><b>The question.</b> A network is asked about when LAN sync is on and the node says so: <c>canAsk</c>, not trusted, reason
/// <c>unknown</c>, kind <c>lan</c>. A Windows network is never one (its category decides, reasons <c>windows-*</c>), and neither is a
/// VPN, a virtual adapter, a network already answered or whose router is not known yet. Nothing is asked in invisible mode or while the vault
/// is locked (the alarms report tells; no answer from it is no question either). One network at a time, each fingerprint at most once per run
/// of the shell and never again after an answer. The question is a notice with the buttons <b>Yes</b> and <b>Not this network</b>
/// (<see cref="IActionNotifier"/>) where the system has them, else a plain notice; either way it is also an item of the tray menu
/// (<see cref="TrayState"/>) until it is answered, so a banner that was dismissed or never shown does not lose the question. The answer is
/// posted to the node, which rebinds at once.</para>
///
/// <para><b>The notice (D9).</b> While the node says <c>noticePending</c>, once per run, after the questions and while at least one network
/// is trusted (it says "on this network"): "Direct sync with your devices on this network is on...", with <b>Got it</b> (posted to the node)
/// and <b>Settings</b> (opens the card). Where there are no buttons it is shown plain and dismissed at once.</para>
/// </summary>
public sealed class LanNetworkPrompt : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    /// <summary>How long a notice that could not be shown waits before it is tried again.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(5);

    /// <summary>After a network change: the pause that lets the node rebind and look at its networks first.</summary>
    public static readonly TimeSpan SettleAfterChange = TimeSpan.FromSeconds(5);

    /// <summary>How often the loop looks at the wall clock; a gap longer than <see cref="PollInterval"/> means the computer slept.</summary>
    public static readonly TimeSpan ClockCheckEvery = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The card with the networks: Admin &gt; Devices &gt; Network settings, "Sync with your devices on this network". Not the old
    /// <c>/Admin/DevicesOnMyNetwork</c>: that address redirects to the card of the web page on the network, a different one.
    /// </summary>
    public const string SettingsPath = "/Admin/NetworkSettings#lan-sync";

    public const string YesId = "lan.yes";
    public const string NoId = "lan.no";
    public const string GotItId = "lan.gotit";
    public const string SettingsId = "lan.settings";

    public const string QuestionTitle = "Sync on this network?";
    public const string NoticeTitle = "Direct sync is on";
    public const string NoticeText = "Direct sync with your devices on this network is on. Only the sync connection is opened, never the web page.";

    private static readonly NoticeAction[] QuestionActions = [new(YesId, "Yes"), new(NoId, "Not this network")];
    private static readonly NoticeAction[] NoticeActions = [new(GotItId, "Got it"), new(SettingsId, "Settings", OpensApp: true)];

    /// <summary>The question's text; the label is the node's name of the network ("en0 (192.168.1.20)").</summary>
    public static string QuestionText(string label) => $"Sync with your paired devices on this network? ({label})";

    private readonly ILanNode _node;
    private readonly Func<CancellationToken, Task<NodeAlarmsPoll>> _condition;
    private readonly IActionNotifier? _actions;
    private readonly IUserNotifier? _notifier;
    private readonly Action _openSettings;
    private readonly Func<string?> _profileId;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private readonly HashSet<string> _asked = [];
    private readonly HashSet<string> _shown = [];
    private readonly HashSet<string> _answered = [];
    private LanSyncAnswer? _last;
    private string? _profile;
    private bool _noticeShown;
    private DateTime _questionRetryAt;
    private DateTime _noticeRetryAt;
    private string _raised = "";

    private CancellationTokenSource? _loop;
    private volatile TaskCompletionSource _nudge = NewNudge();
    private NetworkAddressChangedEventHandler? _onAddressChanged;
    private NetworkAvailabilityChangedEventHandler? _onAvailabilityChanged;

    /// <param name="node">The node's LAN calls (<see cref="NodeLanRequest"/>); they never throw.</param>
    /// <param name="condition">One poll of the node's alarms report (<see cref="NodeAlarmsRequest.PollAsync"/>), asked only when there is something to say: it tells invisible mode and a locked vault.</param>
    /// <param name="actions">The notice with buttons (<see cref="IShellPlatform.CreateActionNotifier"/>); null: a plain notice and the tray item.</param>
    /// <param name="notifier">The plain notice (<see cref="IShellPlatform.CreateNotifier"/>); null: the tray item only.</param>
    /// <param name="openSettings">Brings the card of the networks to the front; called from any thread.</param>
    /// <param name="profileId">The open profile, read at each poll: another profile is another node, with its own answers.</param>
    /// <param name="time">The clock; null means the system clock.</param>
    /// <param name="log">Where a failure is logged; null means the standard error stream.</param>
    public LanNetworkPrompt(
        ILanNode node, Func<CancellationToken, Task<NodeAlarmsPoll>> condition, IActionNotifier? actions, IUserNotifier? notifier,
        Action openSettings, Func<string?> profileId, TimeProvider? time = null, Action<string>? log = null)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _condition = condition ?? throw new ArgumentNullException(nameof(condition));
        _actions = actions;
        _notifier = notifier;
        _openSettings = openSettings ?? throw new ArgumentNullException(nameof(openSettings));
        _profileId = profileId ?? throw new ArgumentNullException(nameof(profileId));
        _time = time ?? TimeProvider.System;
        _log = log ?? Console.Error.WriteLine;
    }

    /// <summary>Raised, on the polling thread (or the thread of an answer), when <see cref="TrayState"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>What the tray menu shows now.</summary>
    public LanTrayState TrayState
    {
        get
        {
            lock (_stateLock) return StateOf(_last);
        }
    }

    // ── the loop ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Starts polling in the background (a no-op when it already runs).</summary>
    public void Start()
    {
        if (_loop != null) return;
        _loop = new CancellationTokenSource();
        var token = _loop.Token;
        try
        {
            _onAddressChanged = (_, _) => RequestPoll();
            _onAvailabilityChanged = (_, _) => RequestPoll();
            NetworkChange.NetworkAddressChanged += _onAddressChanged;
            NetworkChange.NetworkAvailabilityChanged += _onAvailabilityChanged;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NetworkInformationException)
        {
            // No change events here: the minute poll and the clock check are left.
            Log($"Network changes cannot be watched ({ex.GetType().Name}).");
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    try { await PollOnceAsync(token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Log($"A LAN poll failed ({ex.GetType().Name})."); }
                    var polledAt = _time.GetUtcNow();
                    // Every few seconds, so a wake is noticed by the wall clock (a timer of one minute may count only the time awake).
                    while (true)
                    {
                        var nudged = _nudge.Task;
                        await Task.WhenAny(nudged, Task.Delay(ClockCheckEvery, _time, token)).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (nudged.IsCompleted)
                        {
                            _nudge = NewNudge();
                            await Task.Delay(SettleAfterChange, _time, token).ConfigureAwait(false);
                            break;
                        }
                        if (_time.GetUtcNow() - polledAt >= PollInterval) break;
                    }
                }
            }
            catch (OperationCanceledException) { /* stopped */ }
        }, token);
    }

    /// <summary>Asks for a poll soon (after a network change); the loop lets the node settle first.</summary>
    public void RequestPoll() => _nudge.TrySetResult();

    private static TaskCompletionSource NewNudge() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ── one poll ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One poll and what follows from it. The background loop calls it; tests call it directly.</summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResetForProfile();
            var poll = await _node.PollAsync(cancellationToken).ConfigureAwait(false);
            // No answer changes nothing: the tray keeps what it had, and nobody is asked on a guess.
            if (poll.Answer is not { } answer) return;
            lock (_stateLock) _last = answer;

            if (answer.Setting == LanSyncAnswer.On) await AskAsync(answer, cancellationToken).ConfigureAwait(false);
            RaiseIfChanged();
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task AskAsync(LanSyncAnswer answer, CancellationToken cancellationToken)
    {
        var now = UtcNow();
        var candidates = CandidatesOf(answer);
        List<LanNetworkEntry> open;
        LanNetworkEntry? toAsk;
        LanNetworkEntry? unshown;
        lock (_stateLock)
        {
            open = candidates.Where(n => _asked.Contains(n.Fingerprint!) && !_answered.Contains(n.Fingerprint!)).ToList();
            toAsk = open.Count == 0 ? candidates.FirstOrDefault(n => !_asked.Contains(n.Fingerprint!) && !_answered.Contains(n.Fingerprint!)) : null;
            unshown = open.FirstOrDefault(n => !_shown.Contains(n.Fingerprint!));
        }

        // The notice comes after the questions, and says "on this network", so it waits for a network that is trusted.
        var noticeDue = !_noticeShown && answer.NoticePending && answer.Networks.Any(n => n.Trusted)
            && open.Count == 0 && toAsk is null && now >= _noticeRetryAt;

        // A question that was offered and whose notice was not shown (the system refused it): again after a while.
        if (unshown is not null && now >= _questionRetryAt)
        {
            await ShowQuestionAsync(unshown, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (toAsk is null && !noticeDue) return;
        if (!await QuietAsync(cancellationToken).ConfigureAwait(false)) return;

        if (toAsk is not null)
        {
            lock (_stateLock) _asked.Add(toAsk.Fingerprint!);
            await ShowQuestionAsync(toAsk, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ShowNoticeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>True when nothing stands in the way of asking: not invisible mode, the vault unlocked. No answer from the node is "no".</summary>
    private async Task<bool> QuietAsync(CancellationToken cancellationToken)
    {
        try
        {
            var poll = await _condition(cancellationToken).ConfigureAwait(false);
            return poll.Answer is { Locked: false } report && report.State != BlindAlarmsAnswer.Invisible;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"The vault state could not be read ({ex.GetType().Name}).");
            return false;
        }
    }

    // ── the question ─────────────────────────────────────────────────────────────────────────────────────

    private async Task ShowQuestionAsync(LanNetworkEntry network, CancellationToken cancellationToken)
    {
        var fingerprint = network.Fingerprint!;
        var text = QuestionText(network.Label);
        var shown = false;
        if (_actions is not null)
            shown = await TryAsync(() => _actions.TryNotifyAsync(QuestionTitle, text, QuestionActions, id => OnQuestionAction(fingerprint, id), cancellationToken)).ConfigureAwait(false);
        if (!shown && _notifier is not null)
            shown = TryPlain(QuestionTitle, text + " Choose an answer in the menu of the Bee Memory Bank icon.");
        // Nobody to show it to: the tray item is all there is, and that counts as asked (nothing to try again).
        if (_actions is null && _notifier is null) shown = true;

        lock (_stateLock)
        {
            if (shown) _shown.Add(fingerprint);
            else _questionRetryAt = UtcNow() + RetryEvery;
        }
    }

    private void OnQuestionAction(string fingerprint, string id)
    {
        // Called by the system on a thread of its own: the answer is posted from the pool.
        _ = Task.Run(async () =>
        {
            try
            {
                switch (id)
                {
                    case YesId: await AnswerAsync(fingerprint, trusted: true).ConfigureAwait(false); break;
                    case NoId: await AnswerAsync(fingerprint, trusted: false).ConfigureAwait(false); break;
                    default: _openSettings(); break; // a click on the notice itself: the card has the whole question
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"The answer about a network failed ({ex.GetType().Name}).");
            }
        });
    }

    /// <summary>
    /// Posts the person's answer about a network ("Yes" is <paramref name="trusted"/> true). The notice's buttons and the tray's items call it.
    /// True when the node took it; a network that was answered is not asked about again. The status is read again at once.
    /// </summary>
    public async Task<bool> AnswerAsync(string fingerprint, bool trusted, CancellationToken cancellationToken = default)
    {
        if (!await _node.AnswerNetworkAsync(fingerprint, trusted, cancellationToken).ConfigureAwait(false)) return false;
        lock (_stateLock) _answered.Add(fingerprint);
        RaiseIfChanged();
        await PollOnceAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    // ── the notice ───────────────────────────────────────────────────────────────────────────────────────

    private async Task ShowNoticeAsync(CancellationToken cancellationToken)
    {
        if (_actions is not null
            && await TryAsync(() => _actions.TryNotifyAsync(NoticeTitle, NoticeText, NoticeActions, OnNoticeAction, cancellationToken)).ConfigureAwait(false))
        {
            _noticeShown = true; // dismissed by "Got it"; "Settings" leaves the card's own button to do it
            return;
        }
        if (_notifier is not null)
        {
            if (TryPlain(NoticeTitle, NoticeText))
            {
                _noticeShown = true;
                // No buttons to press: it was shown, and that is the notice.
                await _node.DismissNoticeAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _noticeRetryAt = UtcNow() + RetryEvery;
            }
        }
        // Nobody to show it to: the card of the Devices section has it, with its own "Got it".
    }

    private void OnNoticeAction(string id)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (id == GotItId) await _node.DismissNoticeAsync(CancellationToken.None).ConfigureAwait(false);
                else _openSettings();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"The LAN notice's action failed ({ex.GetType().Name}).");
            }
        });
    }

    // ── what is asked about, and what the tray says ──────────────────────────────────────────────────────

    /// <summary>
    /// The networks the person may be asked about, one per fingerprint, in a stable order. The node's own rule (<c>canAsk</c>, not trusted,
    /// <c>unknown</c>, a LAN): a Windows network (<c>windows-*</c>) is decided by its category and never is one.
    /// </summary>
    public static IReadOnlyList<LanNetworkEntry> CandidatesOf(LanSyncAnswer answer) => answer.Networks
        .Where(n => n.CanAsk && !n.Trusted && n.Kind == "lan" && n.Reason == "unknown" && !string.IsNullOrWhiteSpace(n.Fingerprint))
        .GroupBy(n => n.Fingerprint!, StringComparer.Ordinal).Select(g => g.First())
        .OrderBy(n => n.Label, StringComparer.Ordinal).ThenBy(n => n.Fingerprint, StringComparer.Ordinal).ToList();

    /// <summary>What the tray says of LAN sync for this answer (see <see cref="LanTrayState.Status"/>).</summary>
    public static string? StatusOf(LanSyncAnswer? answer)
    {
        if (answer is null || answer.Setting is not (LanSyncAnswer.On or LanSyncAnswer.Off)) return null; // "environment": the environment decides
        if (answer.Setting == LanSyncAnswer.Off) return "off";
        var lan = answer.Networks.Where(n => n.Kind == "lan" && n.Reason != "virtual").ToList();
        if (lan.Count == 0) return null;
        var trusted = lan.Count(n => n.Trusted);
        return trusted == lan.Count ? "trusted"
            : trusted == 0 ? "not trusted"
            : $"trusted on {trusted} of {lan.Count} networks";
    }

    // Called with _stateLock held.
    private LanTrayState StateOf(LanSyncAnswer? answer)
    {
        if (answer is null) return LanTrayState.Empty;
        var questions = answer.Setting != LanSyncAnswer.On ? [] : CandidatesOf(answer)
            .Where(n => _asked.Contains(n.Fingerprint!) && !_answered.Contains(n.Fingerprint!))
            .Select(n => new LanQuestion(n.Fingerprint!, n.Label)).ToList();
        return new LanTrayState(StatusOf(answer), questions);
    }

    private void RaiseIfChanged()
    {
        string key;
        lock (_stateLock)
        {
            var state = StateOf(_last);
            key = state.Status + "|" + string.Join(",", state.Questions.Select(q => q.Fingerprint + "=" + q.Label));
            if (key == _raised) return;
            _raised = key;
        }
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log($"A tray update failed ({ex.GetType().Name})."); }
    }

    private void ResetForProfile()
    {
        var profile = _profileId() ?? "";
        bool changed;
        lock (_stateLock)
        {
            changed = profile != _profile;
            if (!changed) return;
            // Another profile is another node, with its own answers and its own networks.
            _profile = profile;
            _asked.Clear();
            _shown.Clear();
            _answered.Clear();
            _last = null;
            _noticeShown = false;
            _questionRetryAt = default;
            _noticeRetryAt = default;
        }
        RaiseIfChanged();
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private bool TryPlain(string title, string message)
    {
        try { return _notifier?.TryNotify(title, message) ?? false; }
        catch (Exception ex)
        {
            Log($"A notice could not be shown ({ex.GetType().Name}).");
            return false;
        }
    }

    private async Task<bool> TryAsync(Func<Task<bool>> show)
    {
        try { return await show().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"A notice with buttons could not be shown ({ex.GetType().Name}).");
            return false;
        }
    }

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    private void Log(string line)
    {
        try { _log($"[LanNetworkPrompt] {line}"); }
        catch { /* nothing to do */ }
    }

    public void Dispose()
    {
        if (_onAddressChanged != null) NetworkChange.NetworkAddressChanged -= _onAddressChanged;
        if (_onAvailabilityChanged != null) NetworkChange.NetworkAvailabilityChanged -= _onAvailabilityChanged;
        _onAddressChanged = null;
        _onAvailabilityChanged = null;
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
    }
}
