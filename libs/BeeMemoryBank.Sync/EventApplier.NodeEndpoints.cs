using System.Net;
using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Channels;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// The rules of a <c>node_endpoints</c> payload (docs/design/lan-peer-sync.md section 8), shared by the applier and the announcer that writes one.
/// </summary>
public static class NodeEndpointsRules
{
    /// <summary>The most endpoints one announcement may carry.</summary>
    public const int MaxEndpoints = 8;

    /// <summary>
    /// Test seam: loopback addresses count as LAN endpoints, so two nodes on one machine can announce themselves to each other. A shipped build never
    /// sets it: a node that announced 127.0.0.1 would only send its peers to their own loopback.
    /// </summary>
    internal static bool AllowLoopbackForTests { get; set; }

    /// <summary>
    /// The endpoints as they are stored: canonical, https, each matching its scope - <c>lan</c> a private-range address or a <c>.local</c> name,
    /// <c>vpn</c> a CGNAT (100.64/10) or unique-local IPv6 address. <c>internet</c> is reserved: such an endpoint must be an address the peer policy
    /// allows, and is then left out (no release announces one; a peer's internet address is its row's, which a person gave). An endpoint of a scope
    /// this build does not know is left out (a newer node may announce more); one that breaks its scope's rule refuses the whole payload.
    /// </summary>
    /// <returns>The endpoints, or null with <paramref name="refusal"/> when the payload is refused.</returns>
    public static List<NodeEndpointEntry>? Validate(IReadOnlyList<NodeEndpointEntry>? endpoints, out string? refusal)
    {
        refusal = null;
        var result = new List<NodeEndpointEntry>();
        if (endpoints is null) return result;
        if (endpoints.Count > MaxEndpoints)
        {
            refusal = $"more than {MaxEndpoints} endpoints";
            return null;
        }
        foreach (var e in endpoints)
        {
            if (e is null || string.IsNullOrWhiteSpace(e.Url) || string.IsNullOrWhiteSpace(e.Scope))
            {
                refusal = "an endpoint without an address or a scope";
                return null;
            }
            if (e.Scope is not (ChannelScopes.Lan or ChannelScopes.Vpn or ChannelScopes.Internet)) continue;
            var canonical = PeerAddressPolicy.Canonicalize(e.Url);
            if (!canonical.Ok || !canonical.Address!.StartsWith("https://", StringComparison.Ordinal))
            {
                refusal = "an endpoint that is not an https address a node may call";
                return null;
            }
            var uri = new Uri(canonical.Address!);
            if (uri.AbsolutePath != "/")
            {
                refusal = "an endpoint with a path";
                return null;
            }
            var ok = e.Scope switch
            {
                ChannelScopes.Lan => IsLanHost(uri),
                ChannelScopes.Vpn => IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && (ChannelScopes.IsCgnat(ip) || ChannelScopes.IsUniqueLocalV6(ip)),
                _ => true,
            };
            if (!ok)
            {
                refusal = $"an endpoint whose address does not belong to the scope '{e.Scope}'";
                return null;
            }
            if (e.Scope == ChannelScopes.Internet) continue;
            if (result.All(r => r.Url != canonical.Address)) result.Add(new NodeEndpointEntry(canonical.Address!, e.Scope));
        }
        return result;
    }

    private static bool IsLanHost(Uri uri)
    {
        if (uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return AllowLoopbackForTests;
        return ChannelScopes.IsPrivate(ip);
    }

    /// <summary>True for a CA key as an announcement carries it: base64url of an EC SubjectPublicKeyInfo.</summary>
    public static bool IsWellFormedCaKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        try
        {
            var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(key);
            return BeeMemoryBank.Crypto.LeafSignature.IsUsableIssuerKey(bytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public partial class EventApplier
{
    /// <summary>
    /// Writes one of this node's OWN node_endpoints events into tbl_peer_endpoint, right after the publisher signed it: a node never pulls its own events
    /// back through <see cref="ApplyAsync"/>, and the row must follow the same rules either way. The row is what a join snapshot of this node carries.
    /// </summary>
    public Task ApplyOwnNodeEndpointsAsync(SyncEvent evt) =>
        evt.EventType == EventTypes.NodeEndpoints
            ? ApplyNodeEndpointsAsync(evt)
            : throw new ArgumentException($"{evt.EventType} is not a node_endpoints event.", nameof(evt));

    private async Task ApplyNodeEndpointsAsync(SyncEvent evt)
    {
        var p = Deserialize<NodeEndpointsPayload>(evt.Payload);

        // AnyPeer event, so the whitelist gate lets every full node through: what keeps one node from speaking for another is that the record is
        // always the SIGNER's own (the rule of recovery_box_set).
        if (!Guid.TryParseExact(p.NodeId, "D", out var subject) || subject != evt.NodeId)
            throw new UnauthorizedAccessException(
                $"node_endpoints {evt.EventId}: node {evt.NodeId} may only announce its own endpoints, not '{p.NodeId}'.");

        var endpoints = NodeEndpointsRules.Validate(p.Endpoints, out var refusal)
            ?? throw new InvalidDataException($"node_endpoints {evt.EventId}: {refusal}.");
        if (p.TlsLeafSpki is not null && !Blind.Spki.IsWellFormed(p.TlsLeafSpki))
            throw new InvalidDataException($"node_endpoints {evt.EventId}: the leaf pin is not a pin.");
        if (p.TlsCaKey is not null && !NodeEndpointsRules.IsWellFormedCaKey(p.TlsCaKey))
            throw new InvalidDataException($"node_endpoints {evt.EventId}: the CA key is not a public key.");
        if (p.Port is { } port && port is < 1 or > 65535)
            throw new InvalidDataException($"node_endpoints {evt.EventId}: the port is out of range.");

        var json = JsonSerializer.Serialize(endpoints, NodeEndpointsJson);
        using var conn = connFactory.CreateConnection();
        using var tx = conn.BeginTransaction();
        var stored = await conn.QueryFirstOrDefaultAsync<(long LamportTs, string? EventId, string EndpointsJson)?>(
            "SELECT lamport_ts, event_id, endpoints_json FROM tbl_peer_endpoint WHERE node_id = @Id COLLATE NOCASE",
            new { Id = Id(subject) }, tx);

        // The newest of the author's own announcements wins, whatever order they arrive in (its clock only moves forward). A row learned at a join
        // has Lamport time 0 and loses to any announcement. A tie (the same event again) changes nothing.
        if (stored is { } row && (row.LamportTs > evt.LamportTs
                                  || (row.LamportTs == evt.LamportTs && string.CompareOrdinal(row.EventId, evt.EventId.ToString()) >= 0)))
        {
            tx.Commit();
            return;
        }

        await conn.ExecuteAsync(
            @"INSERT INTO tbl_peer_endpoint (node_id, endpoints_json, tls_leaf_spki, tls_ca_key, port, announce_seq, lamport_ts, event_id, updated_at)
              VALUES (@Id, @Json, @Leaf, @Ca, @Port, @Seq, @Ts, @EventId, @Now)
              ON CONFLICT(node_id) DO UPDATE SET endpoints_json = excluded.endpoints_json, tls_leaf_spki = excluded.tls_leaf_spki,
                  tls_ca_key = excluded.tls_ca_key, port = excluded.port, announce_seq = excluded.announce_seq, lamport_ts = excluded.lamport_ts,
                  event_id = excluded.event_id, updated_at = excluded.updated_at",
            new
            {
                Id = Id(subject), Json = json, Leaf = p.TlsLeafSpki, Ca = p.TlsCaKey, Port = p.Port, Seq = p.Seq, Ts = evt.LamportTs,
                EventId = evt.EventId.ToString(), Now = DateTime.UtcNow.ToString("O"),
            }, tx);
        tx.Commit();

        // New places to try: the loop asks the peer again at once instead of waiting out a pause it learned at the old ones.
        if (stored is not { } before || before.EndpointsJson != json)
        {
            logger.LogInformation("Node {NodeId} announced {Count} endpoint(s)", subject, endpoints.Count);
            peerState?.PeerChanged(subject);
        }
    }

    private static readonly JsonSerializerOptions NodeEndpointsJson = new(JsonSerializerDefaults.Web);
}
