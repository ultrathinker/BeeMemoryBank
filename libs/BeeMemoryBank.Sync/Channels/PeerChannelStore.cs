using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Sync.Channels;

/// <summary>Where a way to reach a peer came from (docs/design/lan-peer-sync.md section 7).</summary>
public static class ChannelSources
{
    /// <summary>The peer's replicated address (<c>tbl_whitelist.api_address</c>): the join URL, or what an administrator set.</summary>
    public const string Primary = "primary";
    /// <summary>What the peer announced about itself (<c>node_endpoints</c>).</summary>
    public const string Announced = "announced";
    /// <summary>An mDNS record of the peer seen on this network.</summary>
    public const string Mdns = "mdns";
    /// <summary>Typed for the peer on this node by a person (this node only).</summary>
    public const string Manual = "manual";
}

/// <summary>Where an address leads, by its form.</summary>
public static class ChannelScopes
{
    public const string Lan = "lan";
    public const string Vpn = "vpn";
    public const string Internet = "internet";

    /// <summary>The scope of an address by its host: a private-range literal, a loopback one or a <c>.local</c> name is LAN, CGNAT or unique-local IPv6 is VPN, the rest internet.</summary>
    public static string Of(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return Internet;
        if (uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return Lan;
        if (!IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)) return Internet;
        if (IPAddress.IsLoopback(ip) || IsPrivate(ip)) return Lan;
        if (IsCgnat(ip) || IsUniqueLocalV6(ip)) return Vpn;
        return Internet;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    public static bool IsCgnat(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    public static bool IsUniqueLocalV6(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetworkV6 && (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
}

/// <summary>Why a channel failed last, from a closed set (shown on the device page, and the one-hour pause of a wrong peer).</summary>
public static class ChannelFailures
{
    /// <summary>No connection, a timeout, a refused port.</summary>
    public const string NoAnswer = "no_answer";
    /// <summary>Something answers, but not a sync server (404, a page).</summary>
    public const string NotSyncServer = "not_sync_server";
    /// <summary>A sync server with another node id, another key, or a TLS key the peer's anchor does not accept: not this peer. Paused for an hour.</summary>
    public const string WrongPeer = "wrong_peer";
    /// <summary>It answered with an error.</summary>
    public const string Error = "error";
}

/// <summary>What a node announced about itself (a row of <c>tbl_peer_endpoint</c>).</summary>
public sealed record PeerEndpointRecord(
    Guid NodeId, IReadOnlyList<NodeEndpointEntry> Endpoints, string? TlsLeafSpki, string? TlsCaKey, int? Port, long LamportTs, DateTime UpdatedAt)
{
    /// <summary>The CA key as bytes, or null.</summary>
    public byte[]? CaKeyBytes => NodeEndpointsRules.IsWellFormedCaKey(TlsCaKey) ? System.Buffers.Text.Base64Url.DecodeFromChars(TlsCaKey!) : null;
}

/// <summary>A row of <c>tbl_peer_channel</c>: one way to reach a peer, and how it went.</summary>
public sealed record PeerChannelRow(
    Guid PeerNodeId, string Address, string Source, string Scope, DateTime AddedAt, DateTime? LastSeenAt, DateTime? LastOkAt,
    DateTime? LastFailAt, string? LastFailKind, int FailStreak);

/// <summary>Reads <c>tbl_peer_endpoint</c> (written only by the event applier) and writes the rows a join learns before any announcement.</summary>
public sealed class PeerEndpointStore(IDbConnectionFactory db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PeerEndpointRecord?> GetAsync(Guid nodeId)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<Row>(
            @"SELECT node_id AS NodeId, endpoints_json AS EndpointsJson, tls_leaf_spki AS TlsLeafSpki, tls_ca_key AS TlsCaKey, port AS Port,
                     lamport_ts AS LamportTs, updated_at AS UpdatedAt
              FROM tbl_peer_endpoint WHERE node_id = @Id COLLATE NOCASE", new { Id = nodeId.ToString("D") });
        return row is null ? null : ToRecord(row);
    }

    public async Task<IReadOnlyList<PeerEndpointRecord>> GetAllAsync()
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<Row>(
            @"SELECT node_id AS NodeId, endpoints_json AS EndpointsJson, tls_leaf_spki AS TlsLeafSpki, tls_ca_key AS TlsCaKey, port AS Port,
                     lamport_ts AS LamportTs, updated_at AS UpdatedAt FROM tbl_peer_endpoint");
        return rows.Select(ToRecord).OfType<PeerEndpointRecord>().ToList();
    }

    /// <summary>
    /// What a join learned about the other side before it announced anything (the joiner's endpoints in its request, the host's in its answer): a row
    /// with Lamport time 0, which any announcement supersedes, and which never replaces one. Validated by the same rules as an announcement; what does
    /// not pass is left out.
    /// </summary>
    public async Task NoteFromJoinAsync(Guid nodeId, IReadOnlyList<NodeEndpointEntry>? endpoints, string? tlsLeafSpki, string? tlsCaKey, int? port)
    {
        var valid = NodeEndpointsRules.Validate(endpoints ?? [], out _) ?? [];
        var leaf = Blind.Spki.IsWellFormed(tlsLeafSpki) ? tlsLeafSpki : null;
        var ca = NodeEndpointsRules.IsWellFormedCaKey(tlsCaKey) ? tlsCaKey : null;
        if (valid.Count == 0 && leaf is null && ca is null) return;
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_peer_endpoint (node_id, endpoints_json, tls_leaf_spki, tls_ca_key, port, announce_seq, lamport_ts, event_id, updated_at)
              VALUES (@Id, @Json, @Leaf, @Ca, @Port, 0, 0, NULL, @Now)
              ON CONFLICT(node_id) DO UPDATE SET endpoints_json = excluded.endpoints_json, tls_leaf_spki = excluded.tls_leaf_spki,
                  tls_ca_key = excluded.tls_ca_key, port = excluded.port, updated_at = excluded.updated_at
              WHERE tbl_peer_endpoint.lamport_ts = 0",
            new
            {
                Id = nodeId.ToString("D"), Json = JsonSerializer.Serialize(valid, Json), Leaf = leaf, Ca = ca,
                Port = port is >= 1 and <= 65535 ? port : null, Now = DateTime.UtcNow.ToString("O"),
            });
    }

    private static PeerEndpointRecord? ToRecord(Row r)
    {
        if (!Guid.TryParse(r.NodeId, out var id)) return null;
        List<NodeEndpointEntry> endpoints;
        try { endpoints = JsonSerializer.Deserialize<List<NodeEndpointEntry>>(r.EndpointsJson, Json) ?? []; }
        catch (JsonException) { endpoints = []; }
        return new PeerEndpointRecord(id, endpoints, r.TlsLeafSpki, r.TlsCaKey, r.Port, r.LamportTs, ParseTime(r.UpdatedAt) ?? DateTime.MinValue);
    }

    /// <summary>A stored time (ISO 8601, UTC) as UTC, or null.</summary>
    public static DateTime? ParseTimeOrNull(string? s) => ParseTime(s);

    internal static DateTime? ParseTime(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private sealed class Row
    {
        public string NodeId { get; set; } = "";
        public string EndpointsJson { get; set; } = "[]";
        public string? TlsLeafSpki { get; set; }
        public string? TlsCaKey { get; set; }
        public int? Port { get; set; }
        public long LamportTs { get; set; }
        public string? UpdatedAt { get; set; }
    }
}

/// <summary>
/// This node's view of the ways to reach each peer (<c>tbl_peer_channel</c>): local, never replicated. The manual addresses are written only through
/// <see cref="AddManualAsync"/>, which takes an address <see cref="PeerAddressPolicy"/> has made canonical; the other rows are the bookkeeping of the
/// dialler (seen, worked, failed).
/// </summary>
public sealed class PeerChannelStore(IDbConnectionFactory db)
{
    /// <summary>The most manual addresses one peer may have.</summary>
    public const int MaxManualPerPeer = 8;

    public async Task<IReadOnlyList<PeerChannelRow>> GetAsync(Guid peer)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<Row>(
            @"SELECT peer_node_id AS PeerNodeId, address AS Address, source AS Source, scope AS Scope, added_at AS AddedAt, last_seen_at AS LastSeenAt,
                     last_ok_at AS LastOkAt, last_fail_at AS LastFailAt, last_fail_kind AS LastFailKind, fail_streak AS FailStreak
              FROM tbl_peer_channel WHERE peer_node_id = @Peer COLLATE NOCASE", new { Peer = peer.ToString("D") });
        return rows.Select(ToRow).OfType<PeerChannelRow>().ToList();
    }

    /// <summary>
    /// Adds a manual address for a peer. <paramref name="canonicalAddress"/> MUST come from <see cref="PeerAddressPolicy.Canonicalize"/>. False when the
    /// peer already has <see cref="MaxManualPerPeer"/> of them (the same address again is no new one).
    /// </summary>
    public async Task<bool> AddManualAsync(Guid peer, string canonicalAddress, DateTime nowUtc)
    {
        var canonical = PeerAddressPolicy.Canonicalize(canonicalAddress);
        if (!canonical.Ok || canonical.Address != canonicalAddress)
            throw new ArgumentException("The address must be the canonical form PeerAddressPolicy gives.", nameof(canonicalAddress));
        using var conn = db.CreateConnection();
        var count = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM tbl_peer_channel WHERE peer_node_id = @Peer COLLATE NOCASE AND source = @Source AND address <> @Address",
            new { Peer = peer.ToString("D"), Source = ChannelSources.Manual, Address = canonicalAddress });
        if (count >= MaxManualPerPeer) return false;
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_peer_channel (peer_node_id, address, source, scope, added_at, fail_streak)
              VALUES (@Peer, @Address, @Source, @Scope, @Now, 0)
              ON CONFLICT(peer_node_id, address, source) DO NOTHING",
            new { Peer = peer.ToString("D"), Address = canonicalAddress, Source = ChannelSources.Manual, Scope = ChannelScopes.Of(canonicalAddress), Now = nowUtc.ToString("O") });
        return true;
    }

    /// <summary>Removes a manual address. True when there was one.</summary>
    public async Task<bool> RemoveManualAsync(Guid peer, string address)
    {
        using var conn = db.CreateConnection();
        return await conn.ExecuteAsync(
            "DELETE FROM tbl_peer_channel WHERE peer_node_id = @Peer COLLATE NOCASE AND source = @Source AND address = @Address",
            new { Peer = peer.ToString("D"), Source = ChannelSources.Manual, Address = address }) > 0;
    }

    /// <summary>One attempt at <paramref name="address"/> went through: it is the one tried first next time. Every row of that address is updated; a missing one is made for <paramref name="source"/>.</summary>
    public Task NoteOkAsync(Guid peer, string address, string source, string scope, DateTime nowUtc) =>
        NoteAsync(peer, address, source, scope, nowUtc,
            "UPDATE tbl_peer_channel SET last_ok_at = @Now, last_seen_at = @Now, fail_streak = 0, last_fail_kind = NULL WHERE peer_node_id = @Peer COLLATE NOCASE AND address = @Address",
            failKind: null);

    /// <summary>One attempt at <paramref name="address"/> failed, for <paramref name="kind"/> (<see cref="ChannelFailures"/>).</summary>
    public Task NoteFailAsync(Guid peer, string address, string source, string scope, string kind, DateTime nowUtc) =>
        NoteAsync(peer, address, source, scope, nowUtc,
            "UPDATE tbl_peer_channel SET last_fail_at = @Now, last_fail_kind = @Kind, fail_streak = fail_streak + 1 WHERE peer_node_id = @Peer COLLATE NOCASE AND address = @Address",
            failKind: kind);

    private async Task NoteAsync(Guid peer, string address, string source, string scope, DateTime nowUtc, string update, string? failKind)
    {
        using var conn = db.CreateConnection();
        var args = new { Peer = peer.ToString("D"), Address = address, Source = source, Scope = scope, Now = nowUtc.ToString("O"), Kind = failKind };
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_peer_channel (peer_node_id, address, source, scope, added_at, fail_streak)
              VALUES (@Peer, @Address, @Source, @Scope, @Now, 0)
              ON CONFLICT(peer_node_id, address, source) DO NOTHING", args);
        await conn.ExecuteAsync(update, args);
    }

    /// <summary>
    /// Keeps the bookkeeping bounded: the rows of a peer that is gone, and the automatic rows (not manual ones) that nothing produced for
    /// <paramref name="olderThan"/>, are deleted.
    /// </summary>
    public async Task PruneAsync(IReadOnlySet<Guid> activePeers, TimeSpan olderThan, DateTime nowUtc)
    {
        using var conn = db.CreateConnection();
        var peers = (await conn.QueryAsync<string>("SELECT DISTINCT peer_node_id FROM tbl_peer_channel")).ToList();
        foreach (var p in peers)
            if (!Guid.TryParse(p, out var id) || !activePeers.Contains(id))
                await conn.ExecuteAsync("DELETE FROM tbl_peer_channel WHERE peer_node_id = @Peer", new { Peer = p });
        var cutoff = (nowUtc - olderThan).ToString("O");
        await conn.ExecuteAsync(
            @"DELETE FROM tbl_peer_channel WHERE source <> @Manual
              AND COALESCE(last_ok_at, '') < @Cutoff AND COALESCE(last_seen_at, '') < @Cutoff AND added_at < @Cutoff",
            new { Manual = ChannelSources.Manual, Cutoff = cutoff });
    }

    private static PeerChannelRow? ToRow(Row r) =>
        Guid.TryParse(r.PeerNodeId, out var id)
            ? new PeerChannelRow(id, r.Address, r.Source, r.Scope, PeerEndpointStore.ParseTime(r.AddedAt) ?? DateTime.MinValue,
                PeerEndpointStore.ParseTime(r.LastSeenAt), PeerEndpointStore.ParseTime(r.LastOkAt), PeerEndpointStore.ParseTime(r.LastFailAt),
                r.LastFailKind, (int)r.FailStreak)
            : null;

    private sealed class Row
    {
        public string PeerNodeId { get; set; } = "";
        public string Address { get; set; } = "";
        public string Source { get; set; } = "";
        public string Scope { get; set; } = "";
        public string? AddedAt { get; set; }
        public string? LastSeenAt { get; set; }
        public string? LastOkAt { get; set; }
        public string? LastFailAt { get; set; }
        public string? LastFailKind { get; set; }
        public long FailStreak { get; set; }
    }
}
