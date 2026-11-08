using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Channels;
using Dapper;

namespace BeeMemoryBank.Api.Services.Lan;

/// <summary>
/// The reachability of a full peer as the status pages show it (docs/design/lan-peer-sync.md section 10): what the sync loop learned of its channels
/// (<see cref="PeerReachability"/>), the persisted traces (the channel that last worked, the newest event it authored), what other peers report about it,
/// and the version it last showed. A blind node is left to its own status and alarms (null).
/// </summary>
public sealed class LanStatusFactsSource(IServiceProvider services) : PeerContactView.ILanFactsSource
{
    public Task<PeerContactView.LanFacts?> ForAsync(WhitelistEntry node, PeerContactLog.Contact contact, DateTime? pulledAt, TimeSpan? syncInterval, DateTime nowUtc) =>
        LanStatusFacts.ForAsync(services, node, contact, pulledAt, syncInterval, nowUtc);
}

public static class LanStatusFacts
{
    public static async Task<PeerContactView.LanFacts?> ForAsync(
        IServiceProvider services, WhitelistEntry node, PeerContactLog.Contact contact, DateTime? pulledAt, TimeSpan? interval, DateTime nowUtc)
    {
        if (BlindNodeId.IsBlind(node.NodeId)) return null;
        var reachability = services.GetService<PeerReachability>();
        if (reachability is null) return null;

        var plan = reachability.PlanOf(node.NodeId);
        var hasChannels = plan?.HasCandidates ?? !string.IsNullOrEmpty(node.ApiAddress);

        DateTime? directAt = null;
        string? directScope = null;
        if (reachability.DirectOf(node.NodeId) is { } direct)
            (directAt, directScope) = direct;
        DateTime? authoredAt = null;
        try
        {
            if (directAt is null && services.GetService<PeerChannelStore>() is { } channels
                && (await channels.GetAsync(node.NodeId)).Where(c => c.LastOkAt is not null).MaxBy(c => c.LastOkAt) is { } ok)
                (directAt, directScope) = (ok.LastOkAt, ok.Scope);
            if (services.GetService<IDbConnectionFactory>() is { } db)
            {
                using var conn = db.CreateConnection();
                // The newest row of that author by sequence (the index idx_event_node_seq answers it without a scan): the status is polled often.
                var newest = await conn.ExecuteScalarAsync<string?>(
                    "SELECT created_at FROM tbl_event WHERE node_id = @Id ORDER BY sequence_num DESC LIMIT 1", new { Id = node.NodeId });
                authoredAt = PeerEndpointStore.ParseTimeOrNull(newest) is { } t && t <= nowUtc ? t : null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A closed database or a table this node does not have yet: the status is drawn without these traces.
        }

        var callsUs = !hasChannels;
        bool? hasLanSync = node.LastCapabilitiesSeenAt is null
            ? null
            : BeeMemoryBank.Sync.Metadata.MetadataCapabilities.HasPeerEndpointsV1(BeeMemoryBank.Sync.Metadata.MetadataCapabilities.Parse(node.LastCapabilities));
        var inputs = new PeerReachability.Inputs(
            PeerContactView.OnlineWindowFor(interval, callsUs),
            directAt, directScope,
            InboundOf(contact, node),
            contact.OutboundFailingSince,
            reachability.RelayOf(node.NodeId),
            authoredAt,
            plan,
            hasLanSync,
            pulledAt);
        var state = PeerReachability.Compute(inputs, nowUtc);
        var relay = reachability.RelayOf(node.NodeId);
        return new PeerContactView.LanFacts(hasChannels, state, Text(state, node, relay, inputs, nowUtc), state == ReachabilityStates.ViaRelay ? relay?.ViaName : null);
    }

    private static DateTime? InboundOf(PeerContactLog.Contact contact, WhitelistEntry node)
    {
        var a = contact.InboundAt;
        var b = node.LastProtocolSeenAt is { } seen ? DateTime.SpecifyKind(seen, DateTimeKind.Utc) : (DateTime?)null;
        return a is null ? b : b is null ? a : a > b ? a : b;
    }

    /// <summary>The sentence for a state, in this node's own words (nothing a peer said is quoted).</summary>
    public static string? Text(string state, WhitelistEntry node, PeerReachability.RelaySighting? relay, PeerReachability.Inputs inputs, DateTime nowUtc)
    {
        var name = string.IsNullOrWhiteSpace(node.DisplayName) ? "it" : node.DisplayName;
        return state switch
        {
            ReachabilityStates.DirectLan => "Direct, on this network",
            ReachabilityStates.DirectInternet => "Direct, over the internet",
            ReachabilityStates.CallsUs => "Calls this device",
            ReachabilityStates.ViaRelay => relay is { } r
                ? $"Not on this network; reachable through {(string.IsNullOrWhiteSpace(r.ViaName) ? "another device" : r.ViaName)} ({Ago(r.SeenAt, nowUtc)})"
                : "Not on this network; its changes arrive through another device",
            ReachabilityStates.Away => "Not on this network",
            ReachabilityStates.OldVersion => $"Direct sync on this network needs an update on {name}",
            ReachabilityStates.Silent => new[] { inputs.DirectAt, inputs.InboundAt, inputs.Relay?.SeenAt, inputs.AuthoredAt, inputs.OlderEvidence }.Max() is { } last
                ? $"Not heard from for {(int)(nowUtc - last).TotalDays} days"
                : "Not heard from",
            _ => null,
        };
    }

    private static string Ago(DateTime at, DateTime now)
    {
        var span = now - at;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        return $"{(int)span.TotalDays} d ago";
    }
}
