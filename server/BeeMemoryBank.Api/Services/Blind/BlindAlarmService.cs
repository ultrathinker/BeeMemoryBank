using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Api.Services;

/// <summary>The kinds of <see cref="BlindAlarm"/> (plan 5.6, BMB-77 slice 1). The presenter words each one; an unknown kind is ignored.</summary>
public static class BlindAlarmKinds
{
    /// <summary>No contact with the blind node for longer than its kind of node allows.</summary>
    public const string Silent = "silent";

    /// <summary>The blind node declared a sync protocol below this build's: update the blind node.</summary>
    public const string OldProtocol = "old_protocol";

    /// <summary>The blind node declared a sync protocol above this build's: this PC cannot apply what it holds, update this PC.</summary>
    public const string PcTooOld = "pc_too_old";
}

/// <summary>
/// One alarm about one blind node. It carries no free text: the Blind nodes page and the desktop shell word it from
/// <paramref name="Kind"/>, so a node cannot put text into a notification, and nothing in it is a secret.
/// </summary>
/// <param name="Name">The node's display name; null while the vault is locked (the presenter then uses a generic text).</param>
/// <param name="Since">For <c>silent</c>: the last contact, or when the node was added if it never answered. Null for the protocol kinds.
/// With <paramref name="Kind"/>, <paramref name="NodeId"/> and <paramref name="Protocol"/> it names one episode: it does not move while the
/// condition lasts.</param>
/// <param name="Protocol">For the protocol kinds: the protocol the blind node declared.</param>
/// <param name="Notify">Worth a desktop notification: the threshold for this kind of node is met. A <c>silent</c> alarm with neither
/// <paramref name="Notify"/> nor <paramref name="Banner"/> is a server node this PC is failing to reach below its threshold: it keeps an
/// open episode open (a sleep of this PC restarts the failure streak), it is not shown anywhere itself.</param>
/// <param name="Banner">Shown as a banner on the Blind nodes page (whose own threshold for <c>silent</c> is three days for every node).</param>
public sealed record BlindAlarm(
    string Kind, Guid NodeId, string? Name, DateTime? Since, int? Protocol, bool Notify, bool Banner);

/// <summary>Makes the host build <see cref="BlindAlarmService"/> at start, so it listens to the sync loop from its first cycle. Does nothing else.</summary>
public sealed class BlindAlarmStarter(BlindAlarmService alarms) : Microsoft.Extensions.Hosting.IHostedService
{
    /// <summary>The service this one made the host build.</summary>
    public BlindAlarmService Alarms { get; } = alarms;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The answer of <c>GET /api/blind-nodes/alarms</c>.</summary>
/// <param name="State"><see cref="Judged"/>; <see cref="Invisible"/> (invisible mode: this PC contacts nobody, so nothing is judged);
/// <see cref="WarmingUp"/> (no sync cycle has completed since this node started or the computer woke, so the contact times are not fresh
/// yet). Only a judged report has alarms worth a notification, and only a judged report may clear one; the others carry just the protocol
/// alarms, for the page's banners.</param>
/// <param name="Locked">The vault is locked: the alarms carry no names.</param>
public sealed record BlindAlarmReport(string State, bool Locked, IReadOnlyList<BlindAlarm> Alarms)
{
    public const string Judged = "ok";
    public const string Invisible = "invisible";
    public const string WarmingUp = "warming_up";
}

/// <summary>
/// Which blind nodes need attention (plan 5.6, BMB-77 slice 1): the one list the Blind nodes page renders its banners from and the
/// desktop shell turns into notifications (<c>GET /api/blind-nodes/alarms</c>, through the node front's <c>/node/alarms</c>).
///
/// <para><b>Contact, not events.</b> "Silent" is judged on the last authenticated exchange with the node (<see cref="BlindNodeManager"/>),
/// never on whether the vault changed. The thresholds depend on what kind of node it is:</para>
/// <list type="bullet">
/// <item>A <b>server</b> blind node (it has an address; this PC calls it every cycle): a notification once this PC has failed to reach it
/// for <see cref="ServerNotifyAfter"/> while awake (the streak of <see cref="UnreachablePeers"/>, which does not count a night the PC slept
/// through), or once there was no contact for <see cref="BlindNodeManager.SilentAfter"/>. A night's sleep of the blind node's host or a
/// router reboot is not an emergency; a day is too slow, because its backups stop with it.</item>
/// <item>A blind <b>copy</b> (no address; it calls a node): <see cref="CopyNotifyAfter"/>, and only when this PC has heard from it at all.
/// A copy that calls another node (a hub, a blind node) never calls this PC, so this PC has no evidence about it either way.</item>
/// </list>
/// <para>The page banner stays at <see cref="BlindNodeManager.SilentAfter"/> for every node, as before.</para>
///
/// <para><b>When nothing is judged.</b> In invisible mode (no contact is made with anyone) and until a sync cycle has completed since this
/// process started or the computer woke (<see cref="CycleFresh"/>): contact times are only as fresh as the last cycle, and a PC that was
/// off for a week would otherwise call every blind node silent before it has tried to reach any of them.</para>
/// </summary>
public sealed class BlindAlarmService
{
    /// <summary>A server blind node this PC has failed to reach for this long, while awake, is worth a notification.</summary>
    public static readonly TimeSpan ServerNotifyAfter = TimeSpan.FromHours(6);

    /// <summary>
    /// A blind copy that calls this PC and has not for this long is worth a notification. One value for every copy: the whitelist does
    /// not tell a phone (which may lie in a drawer for a week) from a desktop copy.
    /// </summary>
    public static readonly TimeSpan CopyNotifyAfter = TimeSpan.FromDays(3);

    /// <summary>
    /// The last completed sync cycle must be at most this old for the contact times to be judged. Awake, a cycle completes every
    /// interval (a minute); a longer gap means the computer slept, or a cycle is still busy (a reseed), and either way the times are stale.
    /// </summary>
    public static readonly TimeSpan CycleFresh = TimeSpan.FromMinutes(20);

    private readonly TimeProvider _time;
    private readonly InvisibleModeService? _invisible;
    private readonly SessionService? _session;
    private readonly Func<IReadOnlyDictionary<Guid, DateTime>>? _unreachableSince;
    private long _lastCycleTicks; // 0 = no completed cycle counts yet

    /// <param name="time">The clock of every judgement.</param>
    /// <param name="invisible">Invisible mode; null means never invisible.</param>
    /// <param name="session">The vault session (names are left out while it is locked); null means unlocked.</param>
    /// <param name="scheduler">The sync loop: its completed cycles are what makes the contact times fresh, its unreachable peers the
    /// server-node streaks. Null (a host without the loop) judges at once and without streaks.</param>
    public BlindAlarmService(
        TimeProvider time, InvisibleModeService? invisible = null, SessionService? session = null, SyncScheduler? scheduler = null)
        : this(time, invisible, session, scheduler is null ? null : scheduler.UnreachableSince)
    {
        if (scheduler != null) scheduler.SyncCycleCompleted += (_, _) => NoteSyncCycleCompleted();
    }

    /// <param name="unreachableSince">The sync loop's failure streaks. Not null means there is a sync loop, and judging waits for one of
    /// its cycles to complete (<see cref="NoteSyncCycleCompleted"/>); null judges at once and without streaks.</param>
    public BlindAlarmService(
        TimeProvider time, InvisibleModeService? invisible, SessionService? session,
        Func<IReadOnlyDictionary<Guid, DateTime>>? unreachableSince)
    {
        _time = time;
        _invisible = invisible;
        _session = session;
        _unreachableSince = unreachableSince;
    }

    /// <summary>
    /// A sync cycle has completed. One that ran in invisible mode contacted nobody: it does not count, and the next judgement waits for a
    /// cycle that did.
    /// </summary>
    public void NoteSyncCycleCompleted() =>
        Interlocked.Exchange(ref _lastCycleTicks, _invisible?.IsInvisible == true ? 0 : UtcNow().Ticks);

    /// <summary>Judges <paramref name="nodes"/> (the rows of <see cref="BlindNodeManager.ListAsync"/>) now.</summary>
    public BlindAlarmReport Judge(IReadOnlyList<BlindNodeStatus> nodes)
    {
        var now = UtcNow();
        var locked = _session is { IsUnlocked: false };
        if (_invisible?.IsInvisible == true) return NotJudged(BlindAlarmReport.Invisible, nodes, locked);
        if (_unreachableSince != null)
        {
            var last = Interlocked.Read(ref _lastCycleTicks);
            if (last == 0 || now - new DateTime(last, DateTimeKind.Utc) > CycleFresh)
                return NotJudged(BlindAlarmReport.WarmingUp, nodes, locked);
        }

        var alarms = Evaluate(now, nodes, _unreachableSince?.Invoke() ?? new Dictionary<Guid, DateTime>());
        if (locked) alarms = alarms.Select(a => a with { Name = null }).ToList();
        return new BlindAlarmReport(BlindAlarmReport.Judged, locked, alarms);
    }

    /// <summary>
    /// A report of a time when the contact times cannot be judged. The protocol alarms still go in, for the page only
    /// (<see cref="BlindAlarm.Notify"/> false): they rest on what the blind node declared, not on how fresh a contact time is, and
    /// the "update the blind node" banner has always been there in invisible mode.
    /// </summary>
    private static BlindAlarmReport NotJudged(string state, IReadOnlyList<BlindNodeStatus> nodes, bool locked) =>
        new(state, locked, nodes.Select(n => ProtocolAlarmOf(n, notify: false))
            .OfType<BlindAlarm>().Select(a => locked ? a with { Name = null } : a).ToList());

    /// <summary>
    /// The alarms of <paramref name="nodes"/> at <paramref name="now"/>; a pure function of its arguments.
    /// </summary>
    /// <param name="unreachableSince">Per node this PC cannot reach now, when the streak began while this PC was awake.</param>
    public static IReadOnlyList<BlindAlarm> Evaluate(
        DateTime now, IReadOnlyList<BlindNodeStatus> nodes, IReadOnlyDictionary<Guid, DateTime> unreachableSince)
    {
        var alarms = new List<BlindAlarm>();
        foreach (var node in nodes)
        {
            if (ProtocolAlarmOf(node, notify: true) is { } protocolAlarm) alarms.Add(protocolAlarm);

            var quietSince = DateTime.SpecifyKind(node.LastContact ?? node.CreatedAt, DateTimeKind.Utc);
            var banner = now - quietSince > BlindNodeManager.SilentAfter;
            var failing = DateTime.MinValue;
            var inStreak = !string.IsNullOrEmpty(node.Address) && unreachableSince.TryGetValue(node.NodeId, out failing);
            var notify = string.IsNullOrEmpty(node.Address)
                // A copy: only its calls to THIS PC are evidence (they are what LastProtocolSeenAt records).
                ? node.ProtocolSeenAt is not null && now - quietSince > CopyNotifyAfter
                : banner || (inStreak && now - failing >= ServerNotifyAfter);
            // A server node this PC is failing to reach stays in the list below its threshold too (Notify and Banner both false): a sleep of
            // this PC restarts the streak, and the condition has not ended because of it. The desktop shell keeps an open episode for such an
            // alarm instead of ending it, so one outage is one episode however many times the PC sleeps; nothing else shows it.
            if (banner || notify || inStreak)
                alarms.Add(new BlindAlarm(BlindAlarmKinds.Silent, node.NodeId, node.DisplayName, quietSince, Protocol: null, notify, banner));
        }
        return alarms;
    }

    /// <summary>The protocol alarm of <paramref name="node"/>, if it declared a protocol other than this build's.</summary>
    private static BlindAlarm? ProtocolAlarmOf(BlindNodeStatus node, bool notify)
    {
        if (node.Protocol is not { } protocol || protocol == SyncProtocolVersion.Current) return null;
        var kind = protocol < SyncProtocolVersion.Current ? BlindAlarmKinds.OldProtocol : BlindAlarmKinds.PcTooOld;
        return new BlindAlarm(kind, node.NodeId, node.DisplayName, Since: null, protocol, notify, Banner: true);
    }

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;
}
