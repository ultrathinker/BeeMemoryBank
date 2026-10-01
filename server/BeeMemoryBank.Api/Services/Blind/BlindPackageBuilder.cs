using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace BeeMemoryBank.Api.Services;

/// <summary>A built blind package on disk (the embedded manifest signature is inside it).</summary>
public sealed record BlindPackage(string FilePath, string Sha256, long Length, BlindManifest Manifest);

/// <summary>
/// Builds the blind package (CONTRACTS §2) — one format for seed, reseed, replica and restore: the
/// signed peer snapshot (secrets filtered; DB not encrypted, the receiver has no DEK) plus
/// tbl_hard_delete_audit, with tbl_article.embedding_projection nulled (plan 3.6), and
/// blind-manifest.json.
/// </summary>
public sealed class BlindPackageBuilder(
    SnapshotService snapshots,
    INodeIdentityRepository nodeRepo,
    IWhitelistRepository whitelist,
    ISyncPositionRepository positions,
    IEventLogRepository events,
    IDbConnectionFactory db,
    IConfiguration config,
    BlindTlsIdentity? tls = null)
{
    /// <summary>
    /// Kept on top of <c>SnapshotTables.Replicated</c>. The hard-delete audit is what lets a node
    /// restored from the blind node refuse a late event about something deleted for good
    /// (plan 3.6). A joining full node gets it from the network instead; a blind node has only this.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ExtraTables = ["tbl_hard_delete_audit"];

    /// <param name="producerIsSuperadmin">How the producer's own row reads in the package. Only the
    /// caller knows: a PC seeding after its pre-flight (plan 4.2) is superadmin in the network's
    /// eyes, a blind node serving a replica never is.</param>
    public async Task<BlindPackage> BuildAsync(
        Guid seedId, long? includesUpTo, bool producerIsSuperadmin, CancellationToken ct = default,
        string? fileNamePrefix = null)
    {
        var self = await nodeRepo.GetAsync()
            ?? throw new InvalidOperationException("Node is not initialized.");
        var cp = await events.GetMaxSequenceAsync();
        var (producerAddress, producerSpki) = ProducerEndpoint();

        var peers = (await whitelist.GetAllActiveAsync())
            .Select(r => new BlindManifestPeer(r.NodeId, r.DisplayName, Convert.ToBase64String(r.Ed25519PublicKey),
                r.ApiAddress, r.IsSuperadmin, r.TlsSpki, r.LamportTs, r.SourceNodeId))
            .Prepend(new BlindManifestPeer(self.NodeId, self.DisplayName, Convert.ToBase64String(self.Ed25519PublicKey),
                producerAddress, producerIsSuperadmin, producerSpki, LamportTs: 0, SourceNodeId: null))
            .ToList();
        var pulled = (await positions.GetAllAsync())
            .Select(p => new BlindManifestPosition(p.RemoteNodeId, p.LastSequenceNum))
            .ToList();

        var manifest = new BlindManifest(BlindManifest.CurrentFormat, seedId, self.NodeId, cp, includesUpTo,
            DateTime.UtcNow, peers, pulled, await StandingEventsAsync(self.NodeId));

        var info = await snapshots.CreateAsync(
            filterSecrets: true, sign: true, cpSequenceNum: cp, encryptDb: false,
            additions: new SnapshotAdditions(ExtraTables, NullEmbeddingProjections,
                new Dictionary<string, byte[]> { [BlindManifest.FileName] = manifest.ToBytes() }),
            fileNamePrefix: fileNamePrefix);

        var path = snapshots.GetSnapshotPath(info.FileName);
        string sha256;
        await using (var file = File.OpenRead(path))
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        return new BlindPackage(path, sha256, new FileInfo(path).Length, manifest);
    }

    /// <summary>
    /// The signed whitelist events about this node still in its log — its admission, updates AND revokes,
    /// in order (review L-merge round 3 #2: without the revokes, a revoked producer's old promotion
    /// would be replayed alone). The receiver runs them through its ordinary applier before it has any
    /// row for this node, so what the stream cannot prove it does not grant: compaction removes a prefix
    /// of the log, so where the admission is gone the updates and revokes after it apply to nothing. A
    /// revoke this node never received (it is cut off once revoked) is not here either; the receiver
    /// learns it from any other peer, as every node does.
    /// </summary>
    private async Task<IReadOnlyList<SyncEvent>> StandingEventsAsync(Guid self)
    {
        using var conn = db.CreateConnection();
        return (await conn.QueryAsync<SyncEvent>(
            @"SELECT event_id AS EventId, node_id AS NodeId, lamport_ts AS LamportTs, event_type AS EventType,
                     article_id AS ArticleId, entity_id AS EntityId, payload AS Payload, signature AS Signature,
                     protocol_version AS ProtocolVersion, created_at AS CreatedAt
              FROM tbl_event
              WHERE event_type IN (@Add, @Update, @Revoke) AND lower(json_extract(payload, '$.node_id')) = @Self
              ORDER BY lamport_ts, sequence_num",
            new { Add = EventTypes.WhitelistAdd, Update = EventTypes.WhitelistUpdate, Revoke = EventTypes.WhitelistRevoke, Self = self.ToString("D") })).ToList();
    }

    /// <summary>
    /// Where the receiver can dial this node, and the pin it checks there (review L-merge #3): without
    /// them the producer's row on a blind node seeded from its only hub is not dialable, and not
    /// pinned. A blind node knows both (the address its pair code gives, its own certificate). A full
    /// node that is reachable says so in configuration — BMB_PUBLIC_ADDRESS and BMB_PUBLIC_TLS_SPKI —
    /// and one that is not (a PC behind NAT) dials the blind node itself and leaves both empty.
    /// </summary>
    private (string? Address, string? Spki) ProducerEndpoint()
    {
        var address = config["BMB_PUBLIC_ADDRESS"]?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(address)) return (null, null);
        var spki = tls?.Spki ?? config["BMB_PUBLIC_TLS_SPKI"]?.Trim();
        return (address, string.IsNullOrEmpty(spki) ? null : spki);
    }

    /// <summary>
    /// Plan 3.6: an embedding projection is derived from the article's plaintext, so it says
    /// something about the content — which is exactly what a blind node must not hold.
    /// </summary>
    private static void NullEmbeddingProjections(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE tbl_article SET embedding_projection = NULL";
        cmd.ExecuteNonQuery();
    }
}
