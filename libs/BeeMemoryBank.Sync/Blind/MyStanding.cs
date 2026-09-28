using System.Text.Json.Serialization;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// Answer of <c>GET /api/sync/my-standing</c> (plan 4.2): how the responding node sees the caller,
/// and which protocol it last saw from each of its peers. The PC asks every reachable full peer
/// before adding a blind node, because "superadmin" is decided by each RECEIVER's whitelist and a
/// PC does not see its own row anywhere else.
/// </summary>
/// <param name="CallerIsSuperadmin">The caller's row here carries is_superadmin.</param>
/// <param name="ReseedNeeded">Only a blind responder sets it (plan 5.3): it wants a reseed.</param>
public sealed record MyStanding(
    [property: JsonPropertyName("responder_node_id")] Guid ResponderNodeId,
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("caller_is_superadmin")] bool CallerIsSuperadmin,
    [property: JsonPropertyName("reseed_needed")] bool ReseedNeeded,
    [property: JsonPropertyName("peers")] IReadOnlyList<PeerProtocolSeen> Peers,
    // Why this node's master DEK must be treated as exposed (BlindState.DekExposureKey), or null.
    [property: JsonPropertyName("dek_exposure")] string? DekExposure = null);

public sealed record PeerProtocolSeen(
    [property: JsonPropertyName("node_id")] Guid NodeId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("last_protocol_version")] int? LastProtocolVersion,
    [property: JsonPropertyName("last_protocol_seen_at")] DateTime? LastProtocolSeenAt,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt);
