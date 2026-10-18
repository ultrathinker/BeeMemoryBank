using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;

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

    /// <summary>
    /// The blind node's current baseline is older than <see cref="BlindAlarmService.StaleBaselineAfter"/> while it holds deltas
    /// (docs/design/step-a-spec.md 10.4): its restore would replay a week of changes, and its log only grows until a newer baseline covers it.
    /// </summary>
    public const string StaleBaseline = "stale_baseline";

    /// <summary>
    /// This PC calls the blind node and the calls fail (a door that is closed to it, another node at the address, a node that answers badly) since
    /// <c>Since</c>, and nothing has been heard from it since: the failure the contact status of the node shows. A banner of the Blind nodes page, from
    /// <see cref="BlindAlarmService.ServerNotifyAfter"/> on; the unreachable streak keeps its own <c>silent</c> alarm.
    /// </summary>
    public const string NotAnswering = "not_answering";
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
public sealed class BlindAlarmStarter(BlindAlarmService alarms, ISyncProtocolPolicy? policy = null) : Microsoft.Extensions.Hosting.IHostedService
{
    /// <summary>
    /// The service this one made the host build, judging against the protocol the host declares (docs/design/step-a-spec.md 5.6): the host's
    /// registration of the service predates the policy and passes none, so the policy is handed over here, once, as the host starts.
    /// </summary>
    public BlindAlarmService Alarms { get; } = alarms.UsePolicy(policy);

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
/// <item>A blind <b>copy</b> (no address; it calls a node): <see cref="CopyNotifyAfter"/>, and only when this PC has heard from it at all,
/// and only once this PC has been awake for <see cref="CopyGrace"/> (a copy cannot call a PC that is off).
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
    /// <summary>A blind node whose baseline is older than this, while it holds deltas, is flagged on the Blind nodes page (spec 10.4).</summary>
    public static readonly TimeSpan StaleBaselineAfter = TimeSpan.FromDays(7);

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

    /// <summary>
    /// A blind copy is judged only once this PC has been awake this long since it started or woke (the first cycle after a gap longer than
    /// <see cref="CycleFresh"/> begins the count). A copy's contact time moves only when it calls this PC, so right after the PC was off it
    /// has had no chance to call yet, and a copy last heard 4 days ago is not silent for that reason. A server node is not held: this PC
    /// calls it, and the cycle that just completed was the call.
    /// </summary>
    public static readonly TimeSpan CopyGrace = TimeSpan.FromHours(1);

    private readonly TimeProvider _time;
    private readonly InvisibleModeService? _invisible;
    private readonly SessionService? _session;
    private readonly Func<IReadOnlyDictionary<Guid, DateTime>>? _unreachableSince;
    private ISyncProtocolPolicy? _policy;
    private long _lastCycleTicks; // 0 = no completed cycle counts yet
    private long _awakeSinceTicks; // the first completed cycle after this process started or the computer woke; 0 = none counts yet

    /// <param name="time">The clock of every judgement.</param>
    /// <param name="invisible">Invisible mode; null means never invisible.</param>
    /// <param name="session">The vault session (names are left out while it is locked); null means unlocked.</param>
    /// <param name="scheduler">The sync loop: its completed cycles are what makes the contact times fresh, its unreachable peers the
    /// server-node streaks. Null (a host without the loop) judges at once and without streaks.</param>
    public BlindAlarmService(
        TimeProvider time, InvisibleModeService? invisible = null, SessionService? session = null, SyncScheduler? scheduler = null,
        ISyncProtocolPolicy? policy = null)
        : this(time, invisible, session, scheduler is null ? null : scheduler.UnreachableSince, policy)
    {
        if (scheduler != null) scheduler.SyncCycleCompleted += (_, _) => NoteSyncCycleCompleted();
    }

    /// <param name="unreachableSince">The sync loop's failure streaks. Not null means there is a sync loop, and judging waits for one of
    /// its cycles to complete (<see cref="NoteSyncCycleCompleted"/>); null judges at once and without streaks.</param>
    /// <param name="policy">The protocol this node declares (docs/design/step-a-spec.md 5.6); null judges against <see cref="SyncProtocolVersion.Current"/>.</param>
    public BlindAlarmService(
        TimeProvider time, InvisibleModeService? invisible, SessionService? session,
        Func<IReadOnlyDictionary<Guid, DateTime>>? unreachableSince, ISyncProtocolPolicy? policy = null)
    {
        _policy = policy;
        _time = time;
        _invisible = invisible;
        _session = session;
        _unreachableSince = unreachableSince;
    }

    /// <summary>Judges the protocol alarms against <paramref name="policy"/> from now on; null keeps <see cref="SyncProtocolVersion.Current"/>.</summary>
    public BlindAlarmService UsePolicy(ISyncProtocolPolicy? policy)
    {
        if (policy is not null) _policy = policy;
        return this;
    }

    /// <summary>
    /// A sync cycle has completed. One that ran in invisible mode contacted nobody: it does not count, and the next judgement waits for a
    /// cycle that did.
    /// </summary>
    public void NoteSyncCycleCompleted()
    {
        if (_invisible?.IsInvisible == true)
        {
            Interlocked.Exchange(ref _lastCycleTicks, 0);
            Interlocked.Exchange(ref _awakeSinceTicks, 0);
            return;
        }
        var now = UtcNow().Ticks;
        var previous = Interlocked.Exchange(ref _lastCycleTicks, now);
        // No cycle before, or one so long ago that the computer was off or asleep (or a cycle ran for most of an hour): a new stretch awake.
        if (previous == 0 || now - previous > CycleFresh.Ticks) Interlocked.Exchange(ref _awakeSinceTicks, now);
    }

    /// <summary>Judges <paramref name="nodes"/> (the rows of <see cref="BlindNodeManager.ListAsync"/>) now.</summary>
    public BlindAlarmReport Judge(IReadOnlyList<BlindNodeStatus> nodes)
    {
        var now = UtcNow();
        var locked = _session is { IsUnlocked: false };
        if (_invisible?.IsInvisible == true) return NotJudged(BlindAlarmReport.Invisible, nodes, locked, _policy);
        if (_unreachableSince != null)
        {
            var last = Interlocked.Read(ref _lastCycleTicks);
            if (last == 0 || now - new DateTime(last, DateTimeKind.Utc) > CycleFresh)
                return NotJudged(BlindAlarmReport.WarmingUp, nodes, locked, _policy);
        }

        var awakeSince = Interlocked.Read(ref _awakeSinceTicks);
        var copiesHeard = _unreachableSince == null || (awakeSince != 0 && now - new DateTime(awakeSince, DateTimeKind.Utc) >= CopyGrace);
        var alarms = Evaluate(now, nodes, _unreachableSince?.Invoke() ?? new Dictionary<Guid, DateTime>(), copiesHeard, _policy);
        if (locked) alarms = alarms.Select(a => a with { Name = null }).ToList();
        return new BlindAlarmReport(BlindAlarmReport.Judged, locked, alarms);
    }

    /// <summary>
    /// A report of a time when the contact times cannot be judged. The protocol alarms still go in, for the page only
    /// (<see cref="BlindAlarm.Notify"/> false): they rest on what the blind node declared, not on how fresh a contact time is, and
    /// the "update the blind node" banner has always been there in invisible mode.
    /// </summary>
    private static BlindAlarmReport NotJudged(string state, IReadOnlyList<BlindNodeStatus> nodes, bool locked, ISyncProtocolPolicy? policy) =>
        new(state, locked, nodes.Select(n => ProtocolAlarmOf(n, notify: false, policy))
            .OfType<BlindAlarm>().Select(a => locked ? a with { Name = null } : a).ToList());

    /// <summary>
    /// The alarms of <paramref name="nodes"/> at <paramref name="now"/>; a pure function of its arguments.
    /// </summary>
    /// <param name="unreachableSince">Per node this PC cannot reach now, when the streak began while this PC was awake.</param>
    /// <param name="copiesHeard">This PC has been awake for <see cref="CopyGrace"/>, so a copy that calls it has had its chance to. Until
    /// then a copy that would be silent is kept in the list without a banner or a notification (an open episode stays open, a new one
    /// does not begin).</param>
    public static IReadOnlyList<BlindAlarm> Evaluate(
        DateTime now, IReadOnlyList<BlindNodeStatus> nodes, IReadOnlyDictionary<Guid, DateTime> unreachableSince, bool copiesHeard = true,
        ISyncProtocolPolicy? policy = null)
    {
        var alarms = new List<BlindAlarm>();
        foreach (var node in nodes)
        {
            if (ProtocolAlarmOf(node, notify: true, policy) is { } protocolAlarm) alarms.Add(protocolAlarm);
            if (StaleBaselineAlarmOf(now, node) is { } staleAlarm) alarms.Add(staleAlarm);

            var quietSince = DateTime.SpecifyKind(node.LastContact ?? node.CreatedAt, DateTimeKind.Utc);
            var isCopy = string.IsNullOrEmpty(node.Address);
            var quiet = now - quietSince > BlindNodeManager.SilentAfter;
            var failing = DateTime.MinValue;
            var inStreak = !isCopy && unreachableSince.TryGetValue(node.NodeId, out failing);
            // The contact status of the node says this PC's calls to it fail (BMB-313): the same input as the card, with the same "since". A node in the
            // unreachable streak has its silent alarm below; this is for the failures that streak does not follow (a closed door, another identity).
            if (!isCopy && !inStreak && node.Contact is { State: PeerContactView.NotAnswering, NotAnsweringSince: { } failingSince }
                && now - failingSince >= ServerNotifyAfter)
                alarms.Add(new BlindAlarm(BlindAlarmKinds.NotAnswering, node.NodeId, node.DisplayName, DateTime.SpecifyKind(failingSince, DateTimeKind.Utc),
                    Protocol: null, Notify: false, Banner: true));
            // A copy: only its calls to THIS PC are evidence (they are what LastProtocolSeenAt records).
            var copyQuiet = isCopy && node.ProtocolSeenAt is not null && now - quietSince > CopyNotifyAfter;
            // A copy cannot have called while this PC was off: until it has been awake for a while, it is not judged (see CopyGrace).
            var held = isCopy && !copiesHeard && (quiet || copyQuiet);
            var banner = quiet && !held;
            var notify = isCopy ? copyQuiet && !held : banner || (inStreak && now - failing >= ServerNotifyAfter);
            // A server node this PC is failing to reach stays in the list below its threshold too (Notify and Banner both false): a sleep of
            // this PC restarts the streak, and the condition has not ended because of it. The desktop shell keeps an open episode for such an
            // alarm instead of ending it, so one outage is one episode however many times the PC sleeps; nothing else shows it.
            if (banner || notify || inStreak || held)
                alarms.Add(new BlindAlarm(BlindAlarmKinds.Silent, node.NodeId, node.DisplayName, quietSince, Protocol: null, notify, banner));
        }
        return alarms;
    }

    /// <summary>
    /// The protocol alarm of <paramref name="node"/>, if it declared a protocol this node cannot work with. Without a policy that is any
    /// protocol other than this build's; with one (docs/design/step-a-spec.md 5.6) it is what the policy refuses to talk to (an old node
    /// once this one has activated, unless it declares 3 with the envelope capability) or cannot apply (above 4).
    /// </summary>
    private static BlindAlarm? ProtocolAlarmOf(BlindNodeStatus node, bool notify, ISyncProtocolPolicy? policy = null)
    {
        if (node.Protocol is not { } protocol) return null;
        string? kind;
        if (policy is null)
            kind = protocol == SyncProtocolVersion.Current ? null : protocol < SyncProtocolVersion.Current ? BlindAlarmKinds.OldProtocol : BlindAlarmKinds.PcTooOld;
        else if (!policy.ShouldPullFrom(protocol))
            kind = BlindAlarmKinds.PcTooOld;
        else
            kind = policy.IsCompatiblePeer(protocol, node.Capabilities) ? null : BlindAlarmKinds.OldProtocol;
        return kind is null ? null : new BlindAlarm(kind, node.NodeId, node.DisplayName, Since: null, protocol, notify, Banner: true);
    }

    /// <summary>
    /// A blind node whose current baseline is older than <see cref="StaleBaselineAfter"/> while it holds deltas (spec 10.4). A node that
    /// told this PC nothing about its store, or holds no baseline or no deltas, has none: the page does not guess.
    /// </summary>
    private static BlindAlarm? StaleBaselineAlarmOf(DateTime now, BlindNodeStatus node) =>
        node.Store is { BaselineCreatedAt: { } created, DeltaCount: > 0 } && now - DateTime.SpecifyKind(created, DateTimeKind.Utc) > StaleBaselineAfter
            ? new BlindAlarm(BlindAlarmKinds.StaleBaseline, node.NodeId, node.DisplayName, DateTime.SpecifyKind(created, DateTimeKind.Utc),
                Protocol: null, Notify: false, Banner: true)
            : null;

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;
}
