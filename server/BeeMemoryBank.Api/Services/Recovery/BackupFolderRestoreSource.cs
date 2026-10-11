using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>
/// A Docker blind node's backup folder as a source of a restore from a baseline (docs/design/step-a-spec.md 9.8, 12): the operational
/// database copy (<c>beememorybank.db</c>, which after the first format-2 adoption holds the control rows, the signed log and the blobs
/// but no metadata) and the adopted baseline's four files (<c>baselines/current/</c>). The database's own <c>tbl_baseline</c> row is the
/// record of which baseline is current; its manifest bytes and signature are the signed ones, and the folder's files must match them.
/// Nothing here is signed by a serving node, as nothing in a database copy ever was: the restore checks the baseline's producer against
/// the copy's whitelist and its signature under that producer's key, and the anchor confirms the result under the master key.
/// </summary>
public sealed class BackupFolderRestoreSource : IRestoreSourceV2
{
    private readonly string _databasePath;
    private readonly long _localCut;

    private BackupFolderRestoreSource(string databasePath, BaselineFiles baseline, BlindManifestV2 manifest, long localCut)
    {
        _databasePath = databasePath;
        Baseline = baseline;
        Manifest = manifest;
        _localCut = localCut;
    }

    public BaselineFiles Baseline { get; }

    public BlindManifestV2 Manifest { get; }

    /// <summary>The copy itself: it holds every control table under its own name (<c>SnapshotTables.Control</c>).</summary>
    public string ControlDbPath => _databasePath;

    /// <summary>
    /// Opens the copy at <paramref name="databasePath"/> with the baseline files in <paramref name="baselineDirectory"/>; null when the copy
    /// has no current baseline (a backup of a node that never adopted one: the restore is the one from a database copy).
    /// </summary>
    public static async Task<BackupFolderRestoreSource?> OpenAsync(string databasePath, string baselineDirectory, CancellationToken ct = default)
    {
        // Read-only, never through DbConnectionFactory: that one switches the file to WAL, and a backup is not ours to modify.
        using var conn = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        var tables = (await conn.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table'")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!tables.Contains("tbl_baseline")) return null;
        var row = await conn.QuerySingleOrDefaultAsync<(string Id, long LocalCut, byte[] Manifest, byte[] Signature)?>(
            "SELECT baseline_id, local_cut, manifest, signature FROM tbl_baseline WHERE is_current = 1");
        if (row is not { } current) return null;

        if (!Directory.Exists(baselineDirectory))
            throw new InvalidDataException("The backup holds no baseline folder (baselines/current): it cannot be restored from. Back the node up again.");
        BaselineManifest manifest;
        try
        {
            manifest = BaselineManifest.Parse(current.Manifest);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            throw new InvalidDataException("The backup's baseline record cannot be read.", ex);
        }
        if (!manifest.BaselineId.ToString().Equals(current.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The backup's baseline record names another baseline than its signed manifest.");
        await BlindPackageV2Reader.VerifyBaselineFilesAsync(baselineDirectory, manifest, ct);

        var peers = await ReadWhitelistAsync(conn, tables);
        // The signed manifest is signed by a superadmin of this network the copy knows; nobody else's signature counts.
        if (peers.SingleOrDefault(p => p.NodeId == manifest.ProducerNodeId) is not { IsSuperadmin: true } producer
            || !Ed25519Signer.Verify(Convert.FromBase64String(producer.PublicKeyB64), BaselineManifest.SignaturePayload(current.Manifest), current.Signature))
            throw new InvalidDataException("The baseline in the backup is not signed by a superadmin of this network; it cannot be restored from.");

        var self = tables.Contains("tbl_node_identity")
            ? await conn.QuerySingleOrDefaultAsync<(string Id, string Name, byte[] Key)?>("SELECT node_id, display_name, ed25519_public_key FROM tbl_node_identity LIMIT 1")
            : null;
        if (self is not { } identity || !Guid.TryParse(identity.Id, out var selfId))
            throw new InvalidDataException("The backup holds no node identity.");
        if (peers.All(p => p.NodeId != selfId))
            peers.Add(new BlindManifestPeer(selfId, identity.Name, Convert.ToBase64String(identity.Key), null, false, null, 0, null, null));

        var positions = tables.Contains("tbl_sync_position")
            ? (await conn.QueryAsync<(string Id, long Seq)>("SELECT remote_node_id, last_sequence_num FROM tbl_sync_position"))
                .Where(r => Guid.TryParse(r.Id, out _)).Select(r => new BlindManifestPosition(Guid.Parse(r.Id), r.Seq)).ToList()
            : [];
        var head = tables.Contains("tbl_event") ? await conn.ExecuteScalarAsync<long?>("SELECT MAX(sequence_num) FROM tbl_event") ?? 0 : 0;
        var deltaCount = tables.Contains("tbl_event")
            ? (int)await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_event WHERE sequence_num > @cut", new { cut = current.LocalCut })
            : 0;
        BlindActivationRef? activation = null;
        if (tables.Contains("tbl_metadata_activation"))
        {
            var act = await conn.QueryFirstOrDefaultAsync<(string Id, string KeyId)?>("SELECT activation_id, key_id FROM tbl_metadata_activation LIMIT 1");
            if (act is { } a && Guid.TryParse(a.Id, out var activationId)) activation = new BlindActivationRef(activationId, a.KeyId);
        }

        var files = new BaselineFiles(baselineDirectory, manifest, current.Manifest, current.Signature);
        var blind = new BlindManifestV2(
            BlindManifestV2.CurrentFormat, BlindPackageKinds.Full, Guid.NewGuid(), selfId, head, IncludesUpTo: null, DateTime.UtcNow,
            peers, positions, manifest.BaselineId, current.LocalCut, deltaCount, ContainsBlobs: true, activation);
        return new BackupFolderRestoreSource(databasePath, files, blind, current.LocalCut);
    }

    private static async Task<List<BlindManifestPeer>> ReadWhitelistAsync(SqliteConnection conn, HashSet<string> tables)
    {
        var peers = new List<BlindManifestPeer>();
        if (!tables.Contains("tbl_whitelist")) return peers;
        var columns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('tbl_whitelist')")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string Col(string name) => columns.Contains(name) ? name : "NULL";
        var rows = await conn.QueryAsync<(string Id, string Name, byte[] Key, string? Address, long Super, string? Pin, long? Lamport, string? Source, string? Trust)>(
            $@"SELECT node_id, display_name, ed25519_public_key, api_address, is_superadmin, {Col("tls_spki")},
                      {Col("lamport_ts")}, {Col("source_node_id")}, {Col("tls_trust")}
               FROM tbl_whitelist WHERE status = 'A'");
        foreach (var r in rows.Where(r => Guid.TryParse(r.Id, out _) && r.Key is { Length: 32 }))
            peers.Add(new BlindManifestPeer(Guid.Parse(r.Id), r.Name, Convert.ToBase64String(r.Key), r.Address, r.Super != 0, r.Pin,
                Math.Max(0, r.Lamport ?? 0), Guid.TryParse(r.Source, out var s) ? s : null, r.Trust));
        return peers;
    }

    /// <summary>The copy's log after the baseline's local cut, in order, with its own sequence numbers (the replay's received positions).</summary>
    public IEnumerable<SyncEvent> ReadDeltas()
    {
        using var conn = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        var after = _localCut;
        while (true)
        {
            var page = conn.Query<SyncEvent>(
                @"SELECT sequence_num AS SequenceNum, event_id AS EventId, node_id AS NodeId, lamport_ts AS LamportTs, event_type AS EventType,
                         article_id AS ArticleId, CASE WHEN protocol_version >= 4 THEN NULL ELSE entity_id END AS EntityId, payload AS Payload,
                         signature AS Signature, protocol_version AS ProtocolVersion, created_at AS CreatedAt
                  FROM tbl_event WHERE sequence_num > @after ORDER BY sequence_num LIMIT 1000", new { after }).ToList();
            if (page.Count == 0) yield break;
            foreach (var evt in page) yield return evt;
            after = page[^1].SequenceNum;
        }
    }

    public Stream? OpenBlob(string hash)
    {
        if (!BeeMemoryBank.Sync.BlobReferences.IsWellFormedHash(hash)) return null;
        using var conn = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        var data = conn.QuerySingleOrDefault<byte[]?>("SELECT data FROM tbl_blob WHERE hash = @hash", new { hash });
        return data is null ? null : new MemoryStream(data, writable: false);
    }

    public void Dispose() { /* nothing is held open */ }
}
