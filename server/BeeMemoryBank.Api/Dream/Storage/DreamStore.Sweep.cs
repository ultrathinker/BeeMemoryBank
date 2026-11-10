using BeeMemoryBank.Core.Exceptions;
using Dapper;

namespace BeeMemoryBank.Api.Dream;

/// <summary>What one sweep did, for the scheduler's log line (counts only).</summary>
public sealed record DreamSweepResult(int EventsAged, int RunsInterrupted, int AdviceReturned, int RowsUnreadable, int RowsNamingDeleted);

// The housekeeping of plan 3.5, every scheduler step.
public sealed partial class DreamStore
{
    private const int SweepBatch = 500;

    /// <summary>
    /// One sweep. <paramref name="liveRunId"/> is the run this process is working on: a row in an active state with any
    /// other id and a stale heartbeat has no owner. <paramref name="existingArticles"/> says which of the given article
    /// ids still exist in the main database (trashed articles exist; hard-deleted ones do not).
    ///
    /// <para>The sealed part needs the chat key. When it cannot be had (locked, or between master keys in a rotation)
    /// that part is skipped: rows are only ever judged unreadable under a key that was really obtained.</para>
    /// </summary>
    public async Task<DreamSweepResult> SweepAsync(DreamThresholds thresholds, Guid? liveRunId, DateTime nowUtc,
        Func<IReadOnlyCollection<Guid>, Task<IReadOnlySet<Guid>>> existingArticles, CancellationToken ct)
    {
        var aged = await DeleteOldEventsAsync(nowUtc - thresholds.EventRetention);
        var interrupted = await InterruptOrphanRunsAsync(liveRunId, nowUtc - thresholds.HeartbeatStale, nowUtc);
        var returned = await ReturnStaleApplyingAsync(nowUtc - thresholds.ApplyingTimeout, nowUtc);

        Services.ChatKeyLease? lease;
        try
        {
            lease = await protector.TryAcquireForReadAsync(ct);
        }
        catch (SessionLockedException)
        {
            lease = null;
        }
        if (lease is null) return new DreamSweepResult(aged, interrupted, returned, 0, 0);

        using (lease)
        {
            var (unreadable, namingDeleted) = await SweepSealedAsync(lease, existingArticles, ct);
            return new DreamSweepResult(aged, interrupted, returned, unreadable, namingDeleted);
        }
    }

    private async Task<int> DeleteOldEventsAsync(DateTime cutoffUtc)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync("DELETE FROM dream_run_event WHERE at < @cutoff", new { cutoff = Time(cutoffUtc) });
    }

    private async Task<int> InterruptOrphanRunsAsync(Guid? liveRunId, DateTime staleBeforeUtc, DateTime nowUtc)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            $"""
            UPDATE dream_run SET state = 'finished', outcome = 'interrupted', finished_at = @now
            WHERE state IN {ActiveStates}
              AND (@live IS NULL OR id != @live)
              AND COALESCE(heartbeat_at, created_at) < @stale
            """,
            new { live = Id(liveRunId), stale = Time(staleBeforeUtc), now = Time(nowUtc) });
    }

    private async Task<int> ReturnStaleApplyingAsync(DateTime cutoffUtc, DateTime nowUtc)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteAsync(
            // The attempt's token goes with it (DreamStore.NewApplyAttempt): the abandoned attempt can no longer end the advice.
            "UPDATE dream_advice SET status = 'new', status_at = @now, status_note = NULL WHERE status = 'applying' AND status_at < @cutoff",
            new { cutoff = Time(cutoffUtc), now = Time(nowUtc) });
    }

    private async Task<(int Unreadable, int NamingDeleted)> SweepSealedAsync(Services.ChatKeyLease lease,
        Func<IReadOnlyCollection<Guid>, Task<IReadOnlySet<Guid>>> existing, CancellationToken ct)
    {
        var unreadable = 0;
        var namingDeleted = 0;

        var advice = await WalkAsync("dream_advice", lease, DreamAad.Advice,
            json => Parse<DreamAdvicePayload>(json)?.ArticleIds(), existing, ct);
        unreadable += advice.Unreadable;
        namingDeleted += advice.NamingDeleted;

        var events = await WalkAsync("dream_run_event", lease, DreamAad.Event,
            json => Parse<DreamEventPayload>(json)?.ArticleIds, existing, ct);
        unreadable += events.Unreadable;
        namingDeleted += events.NamingDeleted;

        var rules = await WalkAsync("dream_rule", lease, DreamAad.Rule,
            json => Parse<DreamRulePayload>(json) is null ? null : [], existing, ct);
        unreadable += rules.Unreadable;

        return (unreadable, namingDeleted);
    }

    private static T? Parse<T>(string json) where T : class
    {
        try
        {
            return DreamJson.Deserialize<T>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Walks one sealed table in id order, in batches, deleting the rows that do not open and those naming an article that is gone.</summary>
    private async Task<(int Unreadable, int NamingDeleted)> WalkAsync(string table, Services.ChatKeyLease lease, Func<Guid, byte[]> aad,
        Func<string, IReadOnlyList<Guid>?> articleIdsOf, Func<IReadOnlyCollection<Guid>, Task<IReadOnlySet<Guid>>> existing, CancellationToken ct)
    {
        var unreadable = 0;
        var namingDeleted = 0;
        var after = "";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            List<SweepRow> rows;
            using (var conn = OpenConnection())
                rows = (await conn.QueryAsync<SweepRow>(
                    $"""
                    SELECT id AS Id, payload_ciphertext AS Cipher, payload_iv AS Iv, payload_key_v AS KeyVersion
                    FROM {table} WHERE id > @after ORDER BY id LIMIT {SweepBatch}
                    """, new { after })).ToList();
            if (rows.Count == 0) break;
            after = rows[^1].Id;

            var broken = new List<string>();
            var named = new List<(string Row, IReadOnlyList<Guid> Articles)>();
            foreach (var row in rows)
            {
                var ids = TryOpenIds(lease, row, aad, articleIdsOf);
                if (ids is null) broken.Add(row.Id);
                else if (ids.Count > 0) named.Add((row.Id, ids));
            }

            unreadable += await DeleteRowsAsync(table, broken);

            var gone = await FindGoneAsync(named.SelectMany(n => n.Articles).Distinct().ToList(), existing);
            if (gone.Count > 0)
                namingDeleted += await DeleteRowsAsync(table, named.Where(n => n.Articles.Any(gone.Contains)).Select(n => n.Row).ToList());

            if (rows.Count < SweepBatch) break;
        }
        return (unreadable, namingDeleted);
    }

    private IReadOnlyList<Guid>? TryOpenIds(Services.ChatKeyLease lease, SweepRow row, Func<Guid, byte[]> aad, Func<string, IReadOnlyList<Guid>?> articleIdsOf)
    {
        if (!Guid.TryParse(row.Id, out var id)) return null;
        var json = protector.TryDecryptText(lease, row.Cipher, row.Iv, (int?)row.KeyVersion, aad(id));
        return json is null ? null : articleIdsOf(json);
    }

    private static async Task<HashSet<Guid>> FindGoneAsync(IReadOnlyList<Guid> ids, Func<IReadOnlyCollection<Guid>, Task<IReadOnlySet<Guid>>> existing)
    {
        if (ids.Count == 0) return [];
        var found = await existing(ids);
        return ids.Where(id => !found.Contains(id)).ToHashSet();
    }

    private async Task<int> DeleteRowsAsync(string table, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return 0;
        using var conn = OpenConnection();
        return await conn.ExecuteAsync($"DELETE FROM {table} WHERE id IN @ids", new { ids });
    }

    private sealed class SweepRow
    {
        public string Id { get; set; } = "";
        public byte[] Cipher { get; set; } = [];
        public byte[] Iv { get; set; } = [];
        public long? KeyVersion { get; set; }
    }
}
