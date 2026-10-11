using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Blind;

public sealed partial class BaselineStore
{
    public async Task<int> TrimAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await TrimLockedAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<int> TrimLockedAsync()
    {
        var current = await CurrentRowAsync();
        var (_, removed) = await TrimLogAsync(factory, new EventLogRepository(factory), new SyncPushPositionRepository(factory),
            policy.SealsMetadata, current?.LocalCut, _logger);
        return removed;
    }

    /// <summary>
    /// A blind host's log trim (spec 9.5; <c>BlindLogTrimmer</c> runs it every few hours, adoption once): rows every active
    /// peer has acknowledged reading AND the newest state anchor covers go, and a compaction record marks the cut so a peer
    /// asking below it gets 410. Once activated the cut is also bounded by what the current baseline covers of this log
    /// (<paramref name="baselineCut"/>): the deltas after it are the only copy of that content on this host. Activated with no
    /// baseline yet, nothing is trimmed at all.
    /// </summary>
    /// <returns>The new cut-off and how many rows went; a null cut-off when nothing could go.</returns>
    public static async Task<(long? Cutoff, int Removed)> TrimLogAsync(
        IDbConnectionFactory db, IEventLogRepository events, ISyncPushPositionRepository pushPositions,
        bool activated, long? baselineCut, ILogger logger)
    {
        // Deletes from the event log: never while a reseed cutover reads it (EventWriteGate).
        using var _ = await EventWriteGate.Instance.EnterAsync();
        if (activated && baselineCut is null) return (null, 0);

        // How far every active peer says it has read from us: its own report, never the delivery watermark (a page merely
        // served). One that never reported pins the log whole.
        var peers = await pushPositions.GetAllActivePeersWithPushPositionsAsync();
        if (peers.Count == 0) return (null, 0);
        var readByAll = peers.Min(p => p.ReportedSeq ?? 0);

        var cutoff = Math.Min(readByAll, await LastAnchorCoversUpToAsync(db));
        if (activated) cutoff = Math.Min(cutoff, baselineCut!.Value);
        if (cutoff <= (await events.GetLastCompactionCpAsync() ?? 0)) return (null, 0);

        var removed = await events.DeleteUpToAsync(cutoff);
        using (var conn = db.CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_compaction_log (compacted_at, cp_before, cp_after, events_removed, reason)
                  VALUES (@at, NULL, @cutoff, @removed, 'blind local trim')",
                new { at = DateTime.UtcNow.ToString("O"), cutoff, removed });
        logger.LogInformation("Blind log trimmed up to {Cutoff}: {Removed} events", cutoff, removed);
        return (cutoff, removed);
    }

    /// <summary>
    /// The highest sequence below which every event is covered by the newest state anchor: its position vector says, per
    /// source node, up to which Lamport time the anchored state includes that node's events. No anchor yet: nothing goes.
    /// </summary>
    private static async Task<long> LastAnchorCoversUpToAsync(IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        var vector = await conn.ExecuteScalarAsync<string?>(
            "SELECT position_vector FROM tbl_state_anchor ORDER BY lamport_ts DESC, created_at DESC LIMIT 1");
        if (vector is null) return 0;

        var firstUncovered = await conn.ExecuteScalarAsync<long?>(
            @"SELECT MIN(e.sequence_num) FROM tbl_event e
              WHERE NOT EXISTS (SELECT 1 FROM json_each(@vector) v
                                WHERE upper(v.key) = upper(e.node_id) AND e.lamport_ts <= v.value)",
            new { vector });
        return firstUncovered is { } seq
            ? seq - 1
            : await conn.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(sequence_num), 0) FROM tbl_event");
    }
}
