namespace BeeMemoryBank.Web.Models;

/// <summary>
/// One node of <c>GET /api/sync/network</c> (the Api's <c>NetworkNode</c>), as far as the Devices pages read it. <c>Kind</c> is <c>full</c> (it has an address),
/// <c>device</c> (it has none: it calls in) or <c>blind</c>; <c>Platform</c> is <c>windows</c>, <c>macos</c>, <c>linux</c>, <c>android</c>, <c>ios</c> or null.
/// <c>State</c> is the old contact state (<c>answering</c>, <c>not_answering</c>, <c>calls_us</c>, <c>away</c>, <c>unknown</c>, <c>self</c>); <c>Reachability</c>
/// and <c>ReachabilityText</c> are the LAN states and their sentence (docs/design/lan-peer-sync.md 10), null for a node that has none (a blind node).
/// </summary>
public record NetworkNodeDto(
    Guid NodeId,
    string DisplayName,
    bool IsSelf,
    string Kind,
    string? Platform,
    bool InContainer,
    string? AppVersion,
    int? Protocol,
    string? Scheme,
    string? Host,
    int? Port,
    string State,
    bool Online,
    DateTime? LastContactAt,
    string? LastContactDirection,
    DateTime? LastPushAt,
    int UnsyncedCount,
    bool IsSynced,
    DateTime? NotAnsweringSince,
    string? NotAnsweringReason,
    bool IsSuperadmin,
    string? TlsTrust,
    DateTime? AddedAt,
    string? PausedText = null,
    DateTime? LeftAt = null,
    string? Reachability = null,
    string? ReachabilityText = null)
{
    /// <summary>The scheme, host and port of its address on record, or null when it has none.</summary>
    public string? Address => string.IsNullOrEmpty(Host) ? null : (string.IsNullOrEmpty(Scheme) ? "" : Scheme + "://") + Host + (Port is { } p ? ":" + p : "");
}

/// <summary>The network as this node knows it (<c>GET /api/sync/network</c>): this node, its peers, and whether Invisible Mode is on.</summary>
public record NetworkDto(NetworkNodeDto Self, List<NetworkNodeDto> Peers, bool IsInvisible);

/// <summary>
/// One peer of <c>GET /api/sync/delivery-status</c>. The Devices pages read from it what the network map does not carry: the peer that last saw this one
/// (<c>ReachableVia</c>).
/// </summary>
public record DeliveryNodeDto(Guid NodeId, string DisplayName, string State, bool CanBeCalled, string? Reachability = null, string? ReachabilityText = null, string? ReachableVia = null);

/// <summary><c>GET /api/sync/delivery-status</c>.</summary>
public record DeliveryStatusDto(Guid? NodeId, bool IsInvisible, List<DeliveryNodeDto> Nodes);
