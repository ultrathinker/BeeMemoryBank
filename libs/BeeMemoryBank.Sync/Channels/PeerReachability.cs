using System.Collections.Concurrent;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Sync.Channels;

/// <summary>
/// The reachability states of a peer (docs/design/lan-peer-sync.md section 10). One per peer, computed from its channels, from what this node saw of it
/// and from what other peers report about it, so a laptop that is simply elsewhere is "not on this network", not "not answering" (goal G5).
/// </summary>
public static class ReachabilityStates
{
    /// <summary>A LAN (or VPN) channel answered recently: "Direct, on this network".</summary>
    public const string DirectLan = "direct_lan";
    /// <summary>An internet channel answered recently: "Direct, over the internet".</summary>
    public const string DirectInternet = "direct_internet";
    /// <summary>It called this node recently: "Calls this device".</summary>
    public const string CallsUs = "calls_us";
    /// <summary>No direct channel answered, but another peer saw it recently: "Not on this network; reachable through X".</summary>
    public const string ViaRelay = "via_relay";
    /// <summary>Only LAN channels, none on a network this computer is on, and no other evidence: "Not on this network" (grey, no alarm).</summary>
    public const string Away = "away";
    /// <summary>Its LAN channels fail and the version it last showed has no LAN sync: "Direct LAN sync needs an update on X".</summary>
    public const string OldVersion = "old_version";
    /// <summary>A channel on a current network or the internet fails and nothing else was heard of it: "Not answering since ...".</summary>
    public const string NotAnswering = "not_answering";
    /// <summary>No direct contact and no evidence from other peers for <see cref="PeerReachability.SilentAfter"/>: an alarm.</summary>
    public const string Silent = "silent";
    /// <summary>Not known yet in this process.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// What the sync loop learned about reaching each peer, in memory (like <see cref="PeerContactLog"/>): the plan of its channels at the last cycle, the last
/// direct session and its scope, and the evidence of other peers - their <c>my-standing</c> says when THEY last saw it (2.7.5 peers answer it too).
/// </summary>
public sealed class PeerReachability
{
    /// <summary>No direct contact and no relay evidence for this long is the full-peer alarm (as long as a blind node's).</summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromDays(3);

    /// <summary>How often one peer is asked for its <c>my-standing</c> after a session (its view of the others).</summary>
    public static readonly TimeSpan StandingEvery = TimeSpan.FromMinutes(5);

    /// <summary>The shortest window in which another peer's sighting counts as "reachable through it".</summary>
    public static readonly TimeSpan RelayWindow = TimeSpan.FromMinutes(15);

    /// <summary>What the last plan of a peer's channels said.</summary>
    public sealed record PlanSummary(bool HasCandidates, bool HasDialable, bool OnlyLan, bool AnyLanOnCurrentNetwork, DateTime At);

    /// <summary>A sighting of a peer by another one.</summary>
    public sealed record RelaySighting(Guid ViaNodeId, string? ViaName, DateTime SeenAt);

    private readonly ConcurrentDictionary<Guid, PlanSummary> _plans = new();
    private readonly ConcurrentDictionary<Guid, (DateTime At, string Scope)> _direct = new();
    private readonly ConcurrentDictionary<Guid, RelaySighting> _relayed = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _standingAsked = new();

    public void NotePlan(Guid peer, ChannelPlan plan, DateTime nowUtc) =>
        _plans[peer] = new PlanSummary(plan.All.Count > 0, plan.HasDialable, plan.OnlyLan, plan.AnyLanOnCurrentNetwork, nowUtc);

    public void NoteDirect(Guid peer, string scope, DateTime nowUtc) => _direct[peer] = (nowUtc, scope);

    public PlanSummary? PlanOf(Guid peer) => _plans.TryGetValue(peer, out var p) ? p : null;

    public (DateTime At, string Scope)? DirectOf(Guid peer) => _direct.TryGetValue(peer, out var d) ? d : null;

    public RelaySighting? RelayOf(Guid peer) => _relayed.TryGetValue(peer, out var r) ? r : null;

    /// <summary>True when it is time to ask <paramref name="responder"/> for its view again (and notes that it is asked now).</summary>
    public bool ShouldAskStanding(Guid responder, DateTime nowUtc)
    {
        if (_standingAsked.TryGetValue(responder, out var at) && nowUtc - at < StandingEvery) return false;
        _standingAsked[responder] = nowUtc;
        return true;
    }

    /// <summary>
    /// What <paramref name="responder"/> said about the others: for each peer, the newer of when it last authenticated there and when the responder last
    /// read its capabilities. A sighting in the future (another clock) is taken as now; the newest sighting of each peer is kept.
    /// </summary>
    public void NoteStanding(Guid responder, string? responderName, MyStanding standing, Guid self, DateTime nowUtc)
    {
        foreach (var p in standing.Peers)
        {
            if (p.NodeId == self || p.NodeId == responder) continue;
            var seen = Latest(p.LastProtocolSeenAt, p.CapabilitiesSeenAt);
            if (seen is not { } at) continue;
            at = DateTime.SpecifyKind(at, DateTimeKind.Utc);
            if (at > nowUtc) at = nowUtc;
            _relayed.AddOrUpdate(p.NodeId, new RelaySighting(responder, responderName, at),
                (_, old) => old.SeenAt >= at ? old : new RelaySighting(responder, responderName, at));
        }
    }

    /// <summary>Drops what is known about peers that are no longer on the whitelist.</summary>
    public void Retain(IReadOnlySet<Guid> active)
    {
        foreach (var id in _plans.Keys.Where(k => !active.Contains(k)).ToList()) _plans.TryRemove(id, out _);
        foreach (var id in _direct.Keys.Where(k => !active.Contains(k)).ToList()) _direct.TryRemove(id, out _);
        foreach (var id in _relayed.Keys.Where(k => !active.Contains(k)).ToList()) _relayed.TryRemove(id, out _);
        foreach (var id in _standingAsked.Keys.Where(k => !active.Contains(k)).ToList()) _standingAsked.TryRemove(id, out _);
    }

    private static DateTime? Latest(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>Everything the state of one peer is computed from.</summary>
    /// <param name="Window">How long a direct contact counts (the status' online window).</param>
    /// <param name="DirectAt">The last direct session this node called (any scope), from this process or a persisted trace.</param>
    /// <param name="DirectScope">Its scope.</param>
    /// <param name="InboundAt">The last time the peer called this node.</param>
    /// <param name="OutboundFailingSince">The start of the streak of failed calls, or null.</param>
    /// <param name="Relay">The newest sighting of the peer by another peer.</param>
    /// <param name="AuthoredAt">The newest event the peer authored that this node holds (it came by some route).</param>
    /// <param name="Plan">The last plan of its channels.</param>
    /// <param name="HasLanSync">What the peer showed the last time it was seen: true with the LAN-sync capability, false without it (an older version
    /// has no LAN listener), null when it was never seen.</param>
    /// <param name="OlderEvidence">The newest persisted trace of any contact that the fields above do not carry (a pull position's time), or null.</param>
    public sealed record Inputs(
        TimeSpan Window, DateTime? DirectAt, string? DirectScope, DateTime? InboundAt, DateTime? OutboundFailingSince, RelaySighting? Relay,
        DateTime? AuthoredAt, PlanSummary? Plan, bool? HasLanSync, DateTime? OlderEvidence = null);

    /// <summary>The state (<see cref="ReachabilityStates"/>) of one peer now.</summary>
    public static string Compute(Inputs i, DateTime nowUtc)
    {
        bool Fresh(DateTime? t, TimeSpan window) => t is { } at && nowUtc - at < window;

        if (Fresh(i.DirectAt, i.Window) && i.OutboundFailingSince is null)
            return i.DirectScope == ChannelScopes.Internet ? ReachabilityStates.DirectInternet : ReachabilityStates.DirectLan;
        if (Fresh(i.InboundAt, i.Window)) return ReachabilityStates.CallsUs;

        // The alarm: nothing at all for three days, by any route.
        var newest = new[] { i.DirectAt, i.InboundAt, i.Relay?.SeenAt, i.AuthoredAt, i.OlderEvidence }.Max();
        if (newest is { } last && nowUtc - last >= SilentAfter) return ReachabilityStates.Silent;

        // A desktop of an older version on this very network: its LAN port is closed (a timeout, or 404 while its join door is open). Said as what it
        // is, with no alarm: updating it is the cure.
        if (i.OutboundFailingSince is not null && i.HasLanSync == false && i.Plan is { OnlyLan: true, AnyLanOnCurrentNetwork: true })
            return ReachabilityStates.OldVersion;

        var relayWindow = i.Window > RelayWindow ? i.Window : RelayWindow;
        if (Fresh(i.Relay?.SeenAt, relayWindow) || Fresh(i.AuthoredAt, relayWindow)) return ReachabilityStates.ViaRelay;
        if (i.Plan is { HasCandidates: true, OnlyLan: true, AnyLanOnCurrentNetwork: false }) return ReachabilityStates.Away;
        if (i.OutboundFailingSince is not null) return ReachabilityStates.NotAnswering;
        return ReachabilityStates.Unknown;
    }
}
