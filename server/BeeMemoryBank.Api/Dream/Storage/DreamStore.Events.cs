using System.Text.Json.Nodes;
using Dapper;

namespace BeeMemoryBank.Api.Dream;

// dream_run_event: Resealed, kept seven days. Never an article's text.
public sealed partial class DreamStore
{
    private const string EventColumns =
        """
        id AS Id, seq AS Seq, run_id AS RunId, at AS At, kind AS Kind,
        payload_ciphertext AS Cipher, payload_iv AS Iv, payload_key_v AS KeyVersion
        """;

    public async Task<long> InsertEventAsync(Guid runId, DreamEventKind kind, DreamEventPayload payload, DateTime nowUtc)
    {
        var id = Guid.NewGuid();
        var (cipher, iv) = await SealAsync(payload, DreamAad.Event(id));
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO dream_run_event (id, seq, run_id, at, kind, payload_ciphertext, payload_iv, payload_key_v)
            SELECT @id, COALESCE(MAX(seq), 0) + 1, @runId, @at, @kind, @cipher, @iv, @keyVersion FROM dream_run_event
            RETURNING seq
            """,
            new
            {
                id = Id(id),
                runId = Id(runId),
                at = Time(nowUtc),
                kind = DreamWire.ToWire(kind),
                cipher,
                iv,
                keyVersion = Services.ChatDataProtector.ChatKeyVersion,
            });
    }

    /// <summary>The journal of a run after <paramref name="afterSeq"/>, oldest first.</summary>
    public async Task<IReadOnlyList<DreamRunEventItem>> ListEventsAsync(Guid runId, long afterSeq, int limit)
    {
        using var lease = await protector.TryAcquireForReadAsync();
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<EventRow>(
            $"SELECT {EventColumns} FROM dream_run_event WHERE run_id = @runId AND seq > @afterSeq ORDER BY seq LIMIT @limit",
            new { runId = Id(runId), afterSeq, limit });
        var items = new List<DreamRunEventItem>();
        foreach (var row in rows)
        {
            var payload = OpenEvent(lease, row);
            if (payload is null) continue;
            items.Add(new DreamRunEventItem(row.Seq, ParseTime(row.At), DreamWire.Parse<DreamEventKind>(row.Kind), payload.Text, payload.Data));
        }
        return items;
    }

    /// <summary>The run's latest journal line ("what I am doing now"), or null.</summary>
    public async Task<string?> GetLatestEventTextAsync(Guid runId)
    {
        using var lease = await protector.TryAcquireForReadAsync();
        using var conn = OpenConnection();
        var row = await conn.QuerySingleOrDefaultAsync<EventRow>(
            $"SELECT {EventColumns} FROM dream_run_event WHERE run_id = @runId ORDER BY seq DESC LIMIT 1", new { runId = Id(runId) });
        return row is null ? null : OpenEvent(lease, row)?.Text;
    }

    /// <summary>Whether a run still has a journal (it is kept seven days).</summary>
    public async Task<bool> HasEventsAsync(Guid runId)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM (SELECT 1 FROM dream_run_event WHERE run_id = @runId LIMIT 1)", new { runId = Id(runId) }) > 0;
    }

    private DreamEventPayload? OpenEvent(Services.ChatKeyLease? lease, EventRow row) =>
        Open<DreamEventPayload>(lease, row.Cipher, row.Iv, row.KeyVersion, DreamAad.Event(Guid.Parse(row.Id)));

    private sealed class EventRow
    {
        public string Id { get; set; } = "";
        public long Seq { get; set; }
        public string RunId { get; set; } = "";
        public string At { get; set; } = "";
        public string Kind { get; set; } = "";
        public byte[] Cipher { get; set; } = [];
        public byte[] Iv { get; set; } = [];
        public long? KeyVersion { get; set; }
    }
}

/// <summary>The journal of one run, written through the store: every line sealed, never an article's text.</summary>
internal sealed class DreamStoreRunLog(DreamStore store, Guid runId, TimeProvider clock) : IDreamRunLog
{
    public async Task WriteAsync(DreamEventKind kind, string text, IReadOnlyList<Guid>? articleIds = null, JsonObject? data = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await store.InsertEventAsync(runId, kind, new DreamEventPayload(text, articleIds ?? [], data), clock.GetUtcNow().UtcDateTime);
    }
}
