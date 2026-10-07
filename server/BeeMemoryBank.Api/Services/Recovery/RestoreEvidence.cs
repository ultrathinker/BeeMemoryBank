using BeeMemoryBank.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>A node the restored device learns about from the material it restored from.</summary>
/// <param name="LamportTs">The row's LWW version as the source held it, with <paramref name="SourceNodeId"/>:
/// written into the restored row, so an older signed whitelist_update arriving later still loses to it. A row
/// the source held without a version (0, null — e.g. the producer's own row in its package) stays unversioned:
/// then there is nothing newer to protect.</param>
public sealed record RestorePeer(Guid NodeId, string DisplayName, byte[] PublicKey, string? ApiAddress, bool IsSuperadmin, string? TlsSpki,
    long LamportTs = 0, Guid? SourceNodeId = null, string? TlsTrust = null);

/// <summary>
/// What a restore takes over besides the replicated state (CONTRACTS §2 blind-manifest.json): who the
/// restored node trusts — with the superadmin flag and TLS pin of each — how far it has already pulled
/// from each of them, and the package's coverage. The one seam every source feeds: a blind package's
/// signed <c>blind-manifest.json</c> (<see cref="RestoreEvidenceFromManifest"/>) and a database copy
/// from a backup (<see cref="DatabaseRestoreEvidence"/>).
/// </summary>
public sealed record RestoreManifest(
    IReadOnlyList<RestorePeer> Whitelist,
    IReadOnlyDictionary<Guid, long> Positions,
    long? IncludesUpTo = null,
    string? SeedId = null)
{
    public IReadOnlyDictionary<Guid, byte[]> Keys =>
        Whitelist.GroupBy(p => p.NodeId).ToDictionary(g => g.Key, g => g.First().PublicKey);

    public IReadOnlySet<Guid> Superadmins =>
        Whitelist.Where(p => p.IsSuperadmin).Select(p => p.NodeId).ToHashSet();
}

/// <summary>The manifest (null when the source carries none) and the signed events it came with.</summary>
public sealed record RestoreEvidence(RestoreManifest? Manifest, IReadOnlyList<SyncEvent> Events)
{
    public static readonly RestoreEvidence None = new(null, []);
}

/// <summary>The evidence in a blind package's signed manifest (as trustworthy as the package signature).</summary>
public static class RestoreEvidenceFromManifest
{
    public static RestoreManifest Of(BeeMemoryBank.Sync.Blind.BlindManifest manifest)
    {
        if (manifest.Whitelist.Any(p => p.LamportTs < 0))
            throw new InvalidDataException("The package's manifest carries a whitelist row with a negative version.");
        return Build(manifest);
    }

    private static RestoreManifest Build(BeeMemoryBank.Sync.Blind.BlindManifest manifest) => new(
        manifest.Whitelist
            .Select(p => (p, Key: TryBase64(p.PublicKeyB64)))
            .Where(x => x.Key is { Length: 32 })
            .Select(x => new RestorePeer(x.p.NodeId, x.p.DisplayName, x.Key!, x.p.ApiAddress, x.p.IsSuperadmin, x.p.TlsSpki,
                x.p.LamportTs, x.p.SourceNodeId, x.p.TlsTrust))
            .ToList(),
        manifest.Positions.GroupBy(p => p.RemoteNodeId).ToDictionary(g => g.Key, g => g.Max(p => p.LastSequence)),
        manifest.IncludesUpTo,
        manifest.SeedId.ToString());

    private static byte[]? TryBase64(string? value)
    {
        try { return value == null ? null : Convert.FromBase64String(value); }
        catch (FormatException) { return null; }
    }
}

/// <summary>
/// Reads a restore's evidence from a database copy (a blind node's backup, plan 6.8): its active
/// whitelist with superadmin flags, the node that wrote the copy (a node never lists itself), its pull
/// positions, and the signed events it still held.
/// </summary>
public static class DatabaseRestoreEvidence
{
    private const int MaxEvents = 200_000;

    public static async Task<RestoreEvidence> ReadAsync(string databasePath)
    {
        var peers = new List<RestorePeer>();
        Dictionary<Guid, long> positions;
        using (var conn = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False"))
        {
            conn.Open();
            var tables = (await conn.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table'"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (tables.Contains("tbl_whitelist"))
            {
                var columns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('tbl_whitelist')"))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                string Col(string name) => columns.Contains(name) ? name : "NULL";
                peers.AddRange((await conn.QueryAsync<(string Id, string Name, byte[] Key, string? Address, long Super, string? Pin, long? Lamport, string? Source, string? Trust)>(
                        $@"SELECT node_id, display_name, ed25519_public_key, api_address, is_superadmin, {Col("tls_spki")},
                                  {Col("lamport_ts")}, {Col("source_node_id")}, {Col("tls_trust")}
                           FROM tbl_whitelist WHERE status = 'A'"))
                    .Where(r => Guid.TryParse(r.Id, out _))
                    .Select(r => new RestorePeer(Guid.Parse(r.Id), r.Name, r.Key, r.Address, r.Super != 0, r.Pin,
                        Math.Max(0, r.Lamport ?? 0), Guid.TryParse(r.Source, out var s) ? s : null, r.Trust)));
            }

            if (tables.Contains("tbl_node_identity"))
            {
                var self = await conn.QuerySingleOrDefaultAsync<(string Id, string Name, byte[] Key)?>(
                    "SELECT node_id, display_name, ed25519_public_key FROM tbl_node_identity LIMIT 1");
                if (self is { } s && Guid.TryParse(s.Id, out var selfId) && peers.All(p => p.NodeId != selfId))
                    peers.Add(new RestorePeer(selfId, s.Name, s.Key, null, IsSuperadmin: false, null));
            }

            positions = tables.Contains("tbl_sync_position")
                ? (await conn.QueryAsync<(string Id, long Seq)>("SELECT remote_node_id, last_sequence_num FROM tbl_sync_position"))
                    .Where(r => Guid.TryParse(r.Id, out _))
                    .ToDictionary(r => Guid.Parse(r.Id), r => r.Seq)
                : [];

            // Read-only, never through DbConnectionFactory: that one switches the file to WAL, and a
            // backup is not ours to modify.
            var events = tables.Contains("tbl_event")
                ? (await conn.QueryAsync<SyncEvent>(
                    @"SELECT event_id AS EventId, node_id AS NodeId, lamport_ts AS LamportTs, event_type AS EventType,
                             article_id AS ArticleId, entity_id AS EntityId, payload AS Payload, signature AS Signature,
                             protocol_version AS ProtocolVersion, created_at AS CreatedAt
                      FROM tbl_event ORDER BY sequence_num LIMIT @Max", new { Max = MaxEvents })).ToList()
                : [];
            return new RestoreEvidence(new RestoreManifest(peers, positions), events);
        }
    }
}
