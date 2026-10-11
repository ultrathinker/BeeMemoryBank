using System.Globalization;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Boot;

/// <summary>
/// Events, blobs and position reports a closed sealed node receives (docs/design/step-b-spec.md 6). Rows live in the boot store, are served
/// with this node's sequence numbers (6.3) and are replayed at open (6.6). Only sealed and control events are ever appended: the clear
/// content of protocol 1..3 is refused by the relay before it gets here.
/// </summary>
public sealed class RelayInbox : IRelayInbox
{
    private const string EventColumns =
        "relay_seq, event_id, node_id, lamport_ts, event_type, article_id, payload, signature, protocol_version, created_at, origin_peer, origin_seq, received_at";

    private readonly BootStoreConnectionFactory _factory;

    public RelayInbox(BootStoreConnectionFactory factory)
    {
        DapperConfig.Configure();
        _factory = factory;
    }

    public async Task<long> AppendAsync(SyncEvent evt, Guid? originPeer, long? originSeq, CancellationToken ct = default)
    {
        using var connection = _factory.Open();
        // IMMEDIATE: the sequence is computed from the table, two writers must not compute the same one.
        using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var eventId = evt.EventId.ToString("D");
            var existing = await connection.ExecuteScalarAsync<long?>("SELECT relay_seq FROM tbl_relay_event WHERE event_id = @eventId", new { eventId }, transaction);
            if (existing is { } already)
            {
                transaction.Commit();
                return already;
            }

            var floor = long.TryParse(await connection.ExecuteScalarAsync<string?>(
                "SELECT value FROM tbl_boot_meta WHERE key = @key", new { key = BootMetaKeys.SeqFloor }, transaction), NumberStyles.None,
                CultureInfo.InvariantCulture, out var f) ? f : 1;
            var max = await connection.ExecuteScalarAsync<long?>("SELECT MAX(relay_seq) FROM tbl_relay_event", transaction: transaction) ?? 0;
            var sequence = Math.Max(floor, max + 1);

            await connection.ExecuteAsync(
                $@"INSERT INTO tbl_relay_event ({EventColumns}, bytes)
                   VALUES (@sequence, @eventId, @nodeId, @LamportTs, @EventType, @articleId, @Payload, @Signature, @ProtocolVersion, @createdAt,
                           @originPeer, @originSeq, @receivedAt, @bytes)",
                new
                {
                    sequence, eventId, nodeId = evt.NodeId.ToString("D"), evt.LamportTs, evt.EventType,
                    articleId = evt.ArticleId?.ToString("D"), evt.Payload, evt.Signature, evt.ProtocolVersion,
                    createdAt = new DateTime(evt.CreatedAt.Ticks, DateTimeKind.Utc).ToString("o"),
                    originPeer = originPeer?.ToString("D"), originSeq,
                    receivedAt = DateTime.UtcNow.ToString("o"), bytes = Encoding.UTF8.GetByteCount(evt.Payload),
                }, transaction);
            transaction.Commit();
            return sequence;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>True when an event with this id is in the inbox (the receipt counts a repeat as received, spec 6.2 rule 2).</summary>
    public async Task<bool> ContainsAsync(Guid eventId, CancellationToken ct = default)
    {
        using var connection = _factory.Open();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT EXISTS (SELECT 1 FROM tbl_relay_event WHERE event_id = @id)", new { id = eventId.ToString("D") }) == 1;
    }

    /// <summary>The subset of <paramref name="hashes"/> the inbox holds a blob for (spec 6.5).</summary>
    public async Task<IReadOnlySet<string>> ExistingBlobHashesAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (hashes.Count == 0) return found;
        using var connection = _factory.Open();
        foreach (var chunk in hashes.Distinct(StringComparer.Ordinal).Chunk(500))
            foreach (var hash in await connection.QueryAsync<string>("SELECT hash FROM tbl_relay_blob WHERE hash IN @chunk", new { chunk }))
                found.Add(hash);
        return found;
    }

    public async Task<IReadOnlyList<RelayEventRow>> ReadAfterAsync(long afterRelaySeq, int limit, CancellationToken ct = default)
    {
        using var connection = _factory.Open();
        var rows = await connection.QueryAsync<EventRecord>(
            $@"SELECT relay_seq AS RelaySeq, event_id AS EventId, node_id AS NodeId, lamport_ts AS LamportTs, event_type AS EventType,
                      article_id AS ArticleId, payload AS Payload, signature AS Signature, protocol_version AS ProtocolVersion,
                      created_at AS CreatedAt, origin_peer AS OriginPeer, origin_seq AS OriginSeq, received_at AS ReceivedAt
               FROM tbl_relay_event WHERE relay_seq > @afterRelaySeq ORDER BY relay_seq LIMIT @limit",
            new { afterRelaySeq, limit });
        return rows.Select(r => new RelayEventRow(
            r.RelaySeq,
            new SyncEvent
            {
                SequenceNum = r.RelaySeq,
                EventId = Guid.Parse(r.EventId),
                NodeId = Guid.Parse(r.NodeId),
                LamportTs = r.LamportTs,
                EventType = r.EventType,
                ArticleId = r.ArticleId is null ? null : Guid.Parse(r.ArticleId),
                Payload = r.Payload,
                Signature = r.Signature,
                ProtocolVersion = r.ProtocolVersion,
                CreatedAt = ParseUtc(r.CreatedAt),
            },
            r.OriginPeer is null ? null : Guid.Parse(r.OriginPeer),
            r.OriginSeq,
            ParseUtc(r.ReceivedAt))).ToList();
    }

    public async Task DeleteAsync(IReadOnlyCollection<Guid> eventIds, CancellationToken ct = default)
    {
        if (eventIds.Count == 0) return;
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var id in eventIds)
            await connection.ExecuteAsync("DELETE FROM tbl_relay_event WHERE event_id = @id", new { id = id.ToString("D") }, transaction);
        transaction.Commit();
    }

    public async Task<RelayInboxSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        using var connection = _factory.Open();
        var events = await connection.QuerySingleAsync<(long Count, long Bytes, string? Oldest, long Max)>(
            "SELECT COUNT(*), COALESCE(SUM(bytes), 0), MIN(received_at), COALESCE(MAX(relay_seq), 0) FROM tbl_relay_event");
        var blobs = await connection.QuerySingleAsync<(long Count, long Bytes, string? Oldest)>(
            "SELECT COUNT(*), COALESCE(SUM(size), 0), MIN(received_at) FROM tbl_relay_blob");
        var oldest = new[] { events.Oldest, blobs.Oldest }.Where(s => s is not null).Select(s => ParseUtc(s!)).DefaultIfEmpty().Min();
        return new RelayInboxSummary(events.Count, events.Bytes, blobs.Count, blobs.Bytes,
            events.Oldest is null && blobs.Oldest is null ? null : oldest, events.Max);
    }

    public async Task StoreBlobAsync(string hash, byte[] ciphertext, CancellationToken ct = default)
    {
        using var connection = _factory.Open();
        await connection.ExecuteAsync(
            "INSERT OR IGNORE INTO tbl_relay_blob (hash, data, size, received_at) VALUES (@hash, @ciphertext, @size, @now)",
            new { hash, ciphertext, size = ciphertext.LongLength, now = DateTime.UtcNow.ToString("o") });
    }

    public async Task<byte[]?> GetBlobAsync(string hash, CancellationToken ct = default)
    {
        using var connection = _factory.Open();
        return await connection.ExecuteScalarAsync<byte[]?>("SELECT data FROM tbl_relay_blob WHERE hash = @hash", new { hash });
    }

    public async Task<IReadOnlyList<string>> ListBlobHashesAsync(CancellationToken ct = default)
    {
        using var connection = _factory.Open();
        return (await connection.QueryAsync<string>("SELECT hash FROM tbl_relay_blob ORDER BY received_at, hash")).ToList();
    }

    public async Task DeleteBlobsAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default)
    {
        if (hashes.Count == 0) return;
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var hash in hashes)
            await connection.ExecuteAsync("DELETE FROM tbl_relay_blob WHERE hash = @hash", new { hash }, transaction);
        transaction.Commit();
    }

    public async Task RecordReportAsync(Guid peer, long sequence, CancellationToken ct = default)
    {
        using var connection = _factory.Open();
        await connection.ExecuteAsync(
            @"INSERT INTO tbl_relay_report (remote_node_id, reported_seq, updated_at) VALUES (@peer, @sequence, @now)
              ON CONFLICT(remote_node_id) DO UPDATE SET reported_seq = MAX(reported_seq, excluded.reported_seq), updated_at = excluded.updated_at",
            new { peer = peer.ToString("D"), sequence, now = DateTime.UtcNow.ToString("o") });
    }

    public async Task<IReadOnlyList<(Guid Peer, long Sequence)>> ListReportsAsync(CancellationToken ct = default)
    {
        using var connection = _factory.Open();
        var rows = await connection.QueryAsync<(string Peer, long Sequence)>("SELECT remote_node_id, reported_seq FROM tbl_relay_report ORDER BY remote_node_id");
        return rows.Select(r => (Guid.Parse(r.Peer), r.Sequence)).ToList();
    }

    public async Task ClearReportsAsync(CancellationToken ct = default)
    {
        using var connection = _factory.Open();
        await connection.ExecuteAsync("DELETE FROM tbl_relay_report");
    }

    private static DateTime ParseUtc(string text)
    {
        var parsed = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return parsed.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : parsed.ToUniversalTime();
    }

    private sealed class EventRecord
    {
        public long RelaySeq { get; set; }
        public string EventId { get; set; } = "";
        public string NodeId { get; set; } = "";
        public long LamportTs { get; set; }
        public string EventType { get; set; } = "";
        public string? ArticleId { get; set; }
        public string Payload { get; set; } = "";
        public byte[] Signature { get; set; } = [];
        public int ProtocolVersion { get; set; }
        public string CreatedAt { get; set; } = "";
        public string? OriginPeer { get; set; }
        public long? OriginSeq { get; set; }
        public string ReceivedAt { get; set; } = "";
    }
}
