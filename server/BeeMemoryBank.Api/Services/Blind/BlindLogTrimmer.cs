using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync;
using Dapper;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Keeps a blind node's event log from growing forever (plan 5.4). A blind node cannot compact the
/// ordinary way — that publishes a snapshot_checkpoint event, and it authors none — so it trims
/// locally: rows every peer has already read from it AND the last state anchor already covers go,
/// and a tbl_compaction_log row records the cut, so a peer asking for anything below it gets the
/// usual 410 instead of a silent hole.
/// </summary>
public sealed class BlindLogTrimmer(IServiceScopeFactory scopeFactory, ILogger<BlindLogTrimmer> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TrimAsync();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Blind log trim failed; retrying in {Interval}", Interval);
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    /// <summary>Trims once; returns the new cut-off, or null when nothing could go.</summary>
    public async Task<long?> TrimAsync()
    {
        // Deletes from the event log: never while a reseed cutover reads it (EventWriteGate).
        using var _ = await EventWriteGate.Instance.EnterAsync();
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var events = sp.GetRequiredService<IEventLogRepository>();

        // How far every active peer has read from us. One that never has pins the log whole: it
        // would otherwise be cut off before its first pull.
        var peers = await sp.GetRequiredService<ISyncPushPositionRepository>().GetAllActivePeersWithPushPositionsAsync();
        if (peers.Count == 0) return null;
        var readByAll = peers.Min(p => p.LastPushedSeq ?? 0);

        var cutoff = Math.Min(readByAll, await LastAnchorCoversUpToAsync(sp.GetRequiredService<IDbConnectionFactory>()));
        if (cutoff <= (await events.GetLastCompactionCpAsync() ?? 0)) return null;

        var removed = await events.DeleteUpToAsync(cutoff);
        using (var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_compaction_log (compacted_at, cp_before, cp_after, events_removed, reason)
                  VALUES (@at, NULL, @cutoff, @removed, 'blind local trim')",
                new { at = DateTime.UtcNow.ToString("O"), cutoff, removed });
        logger.LogInformation("Blind log trimmed up to {Cutoff}: {Removed} events", cutoff, removed);
        return cutoff;
    }

    /// <summary>
    /// The highest sequence below which every event is covered by the newest state anchor (plan 5.5):
    /// its position vector says, per source node, up to which Lamport time the anchored state
    /// includes that node's events. A restore checks what is newer than the anchor against the
    /// events' own signatures, so those must stay. No anchor yet — nothing is covered, nothing goes.
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
