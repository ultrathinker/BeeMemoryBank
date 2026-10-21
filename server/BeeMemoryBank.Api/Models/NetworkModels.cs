namespace BeeMemoryBank.Api.Models;

/// <summary>
/// The network as this node knows it, for the network map page (<c>GET /api/sync/network</c>): this node, every active node of the whitelist, and who calls whom.
/// Nothing here is a secret of the data: names, addresses, versions and times, like the sync status it is opened from (superadmin only).
/// </summary>
/// <param name="Links">One entry per direction of a call: <c>From</c> calls <c>To</c>. Both directions between two nodes make a two-way link.</param>
public record NetworkResponse(NetworkNode Self, List<NetworkNode> Peers, List<NetworkLink> Links, bool IsInvisible);

/// <summary>One node of the map.</summary>
/// <param name="Kind"><c>full</c> (a node with an address: called by the others), <c>device</c> (no address: a phone or a node behind a NAT, it calls in) or <c>blind</c> (a blind node: an encrypted copy that never sees the notes).</param>
/// <param name="Platform"><c>windows</c>, <c>macos</c>, <c>linux</c>, <c>android</c> or <c>ios</c> as the node said it (<c>NodePlatform</c>); null when it never said (a build before 2.6.4, or not heard since this node started).</param>
/// <param name="InContainer">The node said it runs in a container (Docker).</param>
/// <param name="AppVersion">The version the node last showed.</param>
/// <param name="Protocol">The sync protocol the node last declared.</param>
/// <param name="Scheme">The scheme, host and port of the node's address, as configured (never a path, a query or credentials): what an arrow to it points at.</param>
/// <param name="State"><c>self</c> for this node; otherwise <c>answering</c>, <c>not_answering</c>, <c>calls_us</c> or <c>unknown</c> (see <c>PeerContactView</c>).</param>
/// <param name="SeenFrom">The address the node's last call to this node came from, as this node's server saw it (in memory only, never logged); null when it has not called since this node started.</param>
/// <param name="PausedText">A blind copy that said its owner's limits stop it: "Paused by the device's owner: daily traffic limit (until 00:00 device time)", made by this node from a closed set of reasons; null otherwise.</param>
/// <param name="LeftAt">When the device's own owner removed this copy from the device; the owner of the network revokes it.</param>
/// <param name="Notes">This node only: how many notes it holds (null when it could not be counted, for example while the vault is closed).</param>
/// <param name="Folders">This node only: how many folders.</param>
/// <param name="Changes">This node only: the head of its event log (how many changes it has made and received).</param>
public record NetworkNode(
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
    long LastPushedSeq,
    long HeadSeq,
    DateTime? NotAnsweringSince,
    string? NotAnsweringReason,
    string? SeenFrom,
    bool IsSuperadmin,
    bool CanGenerateEmbeddings,
    string? TlsTrust,
    DateTime? AddedAt,
    int? Notes = null,
    int? Folders = null,
    long? Changes = null,
    string? PausedText = null,
    DateTime? LeftAt = null);

/// <summary>One direction of a call: <c>From</c> calls <c>To</c>, at the address of <c>To</c>.</summary>
/// <param name="Basis"><c>observed</c> (this node is one end of the call and measured it) or <c>expected</c> (between two other nodes: the network's rule is that every node calls every node that has an address; this node cannot see those calls).</param>
/// <param name="State">Observed calls of this node: <c>answering</c>, <c>not_answering</c> or <c>unknown</c>; observed calls to this node: <c>heard</c> (within the time a call is expected in) or <c>stale</c>. Expected calls: <c>expected</c>.</param>
/// <param name="Host">The address the call goes to (the address of <c>To</c>): scheme, host, port.</param>
/// <param name="SeenFrom">For a call to this node: where it came from, as the server saw it.</param>
/// <param name="LastOkAt">The last time the call went well (this node's call) or was heard (a call to this node).</param>
public record NetworkLink(
    Guid From,
    Guid To,
    string Basis,
    string State,
    string? Scheme,
    string? Host,
    int? Port,
    string? SeenFrom,
    DateTime? LastOkAt,
    DateTime? FailingSince,
    string? Reason);
