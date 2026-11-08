using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Channels;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Mobile.Services;

/// <summary>
/// A look around the Wi-Fi for the paired peers, before a sync round (NSD on Android: <c>Platforms/Android/NsdPeerDiscovery.cs</c>). It runs only while the
/// app's sync runs, notes what it sees in the <see cref="PeerDiscoveryCache"/> and never throws: a phone without it syncs over the addresses it has.
/// </summary>
public interface ILanPeerScanner
{
    /// <summary>Looks for at most <paramref name="lookFor"/>, and stops earlier when every paired peer has been seen.</summary>
    Task ScanAsync(TimeSpan lookFor, CancellationToken ct);
}

/// <summary>
/// Which peers a sync round of the phone calls, and what the Status page says about each one (docs/design/lan-peer-sync.md 7, 10, 16). A peer is called
/// when it has a way to be reached now (<see cref="ChannelPlan.HasDialable"/>) - its primary address, what it announced, what NSD saw, an address typed on
/// this phone - not only when its row has an address: a desktop that only announced itself is callable too.
/// </summary>
public static class PhonePeers
{
    /// <summary>How long the Sync now button and the background worker look around the Wi-Fi before they plan.</summary>
    public static readonly TimeSpan LookFor = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The peers of <paramref name="peers"/> that have a way to be reached now, those on a network the phone is on first (a home Wi-Fi is served before a
    /// slow internet hop). A peer whose addresses are all on networks the phone is not on is left out: it is away, not failing.
    /// </summary>
    public static async Task<IReadOnlyList<WhitelistEntry>> CallableAsync(IServiceProvider sp, IEnumerable<WhitelistEntry> peers)
    {
        var planned = new List<(WhitelistEntry Node, bool Near)>();
        foreach (var node in peers)
        {
            var (plan, _) = await PeerSessions.PlanAsync(sp, node);
            if (plan.HasDialable) planned.Add((node, plan.Parallel.Count + plan.First.Count(c => c.IsLanLike) > 0));
        }
        return planned.OrderBy(p => p.Near ? 0 : 1).Select(p => p.Node).ToList();
    }

    /// <summary>Whether the phone knows any way to reach one of <paramref name="peers"/>, dialled now or not (the Status page's "no peer" banner).</summary>
    public static async Task<bool> AnyChannelAsync(IServiceProvider sp, IEnumerable<WhitelistEntry> peers)
    {
        foreach (var node in peers)
            if ((await PeerSessions.PlanAsync(sp, node)).Plan.All.Count > 0) return true;
        return false;
    }

    /// <summary>
    /// The line under a peer's name on the Status page, in the phone's own words (nothing a peer said is quoted), or null when there is nothing to say yet.
    /// </summary>
    /// <param name="syncInterval">How often the phone syncs (5 minutes while the foreground service runs, else 15): a direct contact counts for 2.5 of them.</param>
    /// <param name="ownCycleRecent">The phone itself synced lately. Without it "not heard from for N days" would blame the peer for a phone that was off.</param>
    public static string? StatusText(
        WhitelistEntry node, ChannelPlan plan, IReadOnlyList<PeerChannelRow> rows, PeerReachability? reachability, PeerContactLog.Contact? contact,
        TimeSpan syncInterval, bool ownCycleRecent, DateTime nowUtc)
    {
        DateTime? directAt = null;
        string? directScope = null;
        if (reachability?.DirectOf(node.NodeId) is { } direct) (directAt, directScope) = direct;
        else if (rows.Where(r => r.LastOkAt is not null).MaxBy(r => r.LastOkAt) is { } ok) (directAt, directScope) = (ok.LastOkAt, ok.Scope);

        var relay = reachability?.RelayOf(node.NodeId);
        var summary = new PeerReachability.PlanSummary(plan.All.Count > 0, plan.HasDialable, plan.OnlyLan, plan.AnyLanOnCurrentNetwork, nowUtc);
        bool? hasLanSync = node.LastCapabilitiesSeenAt is null
            ? null
            : MetadataCapabilities.HasPeerEndpointsV1(MetadataCapabilities.Parse(node.LastCapabilities));
        var inputs = new PeerReachability.Inputs(
            TimeSpan.FromTicks((long)(syncInterval.Ticks * 2.5)), directAt, directScope, null, contact?.OutboundFailingSince, relay, null, summary, hasLanSync);
        var state = PeerReachability.Compute(inputs, nowUtc);
        var name = string.IsNullOrWhiteSpace(node.DisplayName) ? "it" : node.DisplayName;

        return state switch
        {
            ReachabilityStates.DirectLan => "Direct, on this network",
            ReachabilityStates.DirectInternet => "Direct, over the internet",
            ReachabilityStates.ViaRelay => relay is { } r && !string.IsNullOrWhiteSpace(r.ViaName)
                ? $"Not on this network; reachable through {r.ViaName} ({Ago(r.SeenAt, nowUtc)})"
                : "Not on this network; its changes arrive through another device",
            ReachabilityStates.Away => "Not on this network",
            ReachabilityStates.OldVersion => $"Direct sync on this network needs an update on {name}",
            ReachabilityStates.NotAnswering => contact?.OutboundFailingSince is { } since ? $"Not answering since {Ago(since, nowUtc)}" : "Not answering",
            ReachabilityStates.Silent when ownCycleRecent => new[] { directAt, relay?.SeenAt }.Max() is { } last
                ? $"Not heard from for {(int)(nowUtc - last).TotalDays} days"
                : "Not heard from",
            _ => null,
        };
    }

    /// <summary>"just now", "5 min ago", "3 h ago", "2 d ago".</summary>
    public static string Ago(DateTime atUtc, DateTime nowUtc)
    {
        var span = nowUtc - atUtc;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        return $"{(int)span.TotalDays} d ago";
    }
}
