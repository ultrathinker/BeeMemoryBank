using System.Data;
using System.Data.Common;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync.Blind;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Takes out of the event log what <see cref="EventInvariants"/> forbids but a build before those
/// checks may already have stored: an event authored by a blind node, or a DEK rotation carrying an
/// envelope for one. Everything in <c>tbl_event</c> is served to peers and pushed to them, so such a
/// row keeps spreading for as long as it sits there.
///
/// <para>Removed, not hidden, and physically: the envelope case is the reason. A rotation event
/// naming a blind node carries the master DEK sealed to that node's key, and the blind node's key
/// sits in a plain file next to the log. So the removal runs with <c>secure_delete</c> (the freed
/// cells are zeroed), in one transaction with its quarantine record, and is followed by a WAL
/// truncate and a VACUUM, so neither a free page nor an old WAL frame keeps the payload. Not
/// silently: each row gets a permanent entry in <c>tbl_sync_quarantine</c> (id, type, origin, why)
/// and a warning, and a redelivered copy is refused by the applier before its idempotency check.</para>
///
/// <para>Physical erasure here cannot reach copies made while the row existed (backups, restic,
/// packages). A node that held such an envelope therefore records <see cref="BlindState.DekExposureKey"/>:
/// the master DEK must be treated as exposed and rotated (see REPORT, stage-1 review #2).</para>
/// </summary>
public static class StoredEventRepair
{
    /// <returns>The number of events removed.</returns>
    public static async Task<int> RunAsync(IServiceScopeFactory scopes, ILogger logger)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();

        List<(string EventId, string NodeId, string EventType, string Payload)> candidates;
        using (var conn = db.CreateConnection())
            candidates = (await conn.QueryAsync<(string, string, string, string)>(
                @"SELECT event_id, node_id, event_type, payload FROM tbl_event
                  WHERE lower(substr(node_id, 1, 4)) = 'b11d' OR event_type IN (@Proposed, @Commit)",
                new { Proposed = EventTypes.DekRotationProposed, Commit = EventTypes.DekRotationCommit })).ToList();

        var violations = candidates
            .Select(c => (c.EventId, c.NodeId, c.EventType, c.Payload,
                Reason: Guid.TryParse(c.NodeId, out var origin) ? EventInvariants.Violation(origin, c.EventType, c.Payload) : null))
            .Where(v => v.Reason != null)
            .ToList();
        if (violations.Count == 0)
        {
            // A previous run removed rows but died before scrubbing the file: the rows are gone, the
            // bytes may not be. The marker, written with the delete, is what says so.
            if (await CleanupPendingAsync(db)) await ScrubAsync(db, logger);
            return 0;
        }

        var sealsForBlind = violations.Where(v => EventInvariants.BlindEnvelopeRecipient(v.EventType, v.Payload) != null)
            .Select(v => v.EventId).ToList();
        var now = DateTime.UtcNow;
        using (var conn = db.CreateConnection())
        {
            await conn.ExecuteAsync("PRAGMA secure_delete = ON");
            using (var tx = conn.BeginTransaction())
            {
                foreach (var v in violations)
                {
                    await conn.ExecuteAsync(
                        @"INSERT INTO tbl_sync_quarantine
                              (event_id, event_type, origin_node_id, failure_count, deferred_failure_count,
                               first_failed_at_utc, last_failed_at_utc, last_error, last_failure_kind)
                          VALUES (@EventId, @EventType, @NodeId, 1, 0, @Now, @Now, @Error, 'permanent')
                          ON CONFLICT(event_id) DO UPDATE SET
                              failure_count = failure_count + 1, last_failed_at_utc = @Now,
                              last_error = @Error, last_failure_kind = 'permanent'",
                        new { v.EventId, v.EventType, v.NodeId, Now = now, Error = "Removed from the event log at startup: " + v.Reason },
                        tx);
                    await conn.ExecuteAsync("DELETE FROM tbl_event WHERE event_id = @EventId", new { v.EventId }, tx);
                }
                // Durable "the file still has to be scrubbed": cleared only after the scrub succeeded.
                await conn.ExecuteAsync(
                    @"INSERT INTO tbl_blind_state (key, value, updated_at) VALUES (@key, @now, @now)
                      ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
                    new { key = CleanupPendingKey, now = now.ToString("O") }, tx);
                if (sealsForBlind.Count > 0)
                    await conn.ExecuteAsync(
                        @"INSERT INTO tbl_blind_state (key, value, updated_at) VALUES (@key, @value, @now)
                          ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
                        new
                        {
                            key = BlindState.DekExposureKey,
                            value = $"{now:O}: removed rotation event(s) {string.Join(", ", sealsForBlind)} that sealed the master DEK for a blind node",
                            now = now.ToString("O")
                        },
                        tx);
                tx.Commit();
            }
        }
        await ScrubAsync(db, logger);

        foreach (var v in violations)
            logger.LogWarning("Stored event {EventId} ({Type}) from {NodeId} removed from the event log and quarantined: {Reason}",
                v.EventId, v.EventType, v.NodeId, v.Reason);
        if (sealsForBlind.Count > 0)
            logger.LogError(
                "This node held {Count} rotation event(s) that sealed the master DEK for a blind node. Treat the master DEK as " +
                "exposed: rotate it from a superadmin node (the new rotation leaves blind nodes out). Copies of this database " +
                "made before now still hold the envelope.", sealsForBlind.Count);
        return violations.Count;
    }

    /// <summary>
    /// Only the scrub, and only if the marker says one is owed — a single-row lookup, cheap enough for
    /// every sync cycle. A scrub the start could not finish (a reader held its checkpoint back) is thus
    /// retried while the node runs, instead of leaving the removed bytes on disk until a restart.
    /// </summary>
    public static async Task RetryPendingScrubAsync(IServiceScopeFactory scopes, ILogger logger)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        if (await CleanupPendingAsync(db)) await ScrubAsync(db, logger);
    }

    /// <summary>The tbl_blind_state key that says removed rows have not been scrubbed from the file yet.</summary>
    public const string CleanupPendingKey = "repair_cleanup_pending";

    private static async Task<bool> CleanupPendingAsync(IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM tbl_blind_state WHERE key = @key", new { key = CleanupPendingKey }) > 0;
    }

    /// <summary>
    /// secure_delete zeroed the freed cells in the pages it wrote; an older frame of those pages may
    /// still sit in the WAL, and a rebuild leaves no free page behind. The marker goes only after the
    /// file is clean: the checkpoint that folds the rebuilt pages into the main file (and truncates it)
    /// has to have COMPLETED, not just been issued. A checkpoint a reader holds back reports
    /// SQLITE_BUSY in its result instead of failing, so the result is checked; anything short of
    /// complete leaves the marker, and the next start (or sync run) scrubs again. Clearing the marker
    /// is itself safe to lose in a crash: by then the only thing its WAL frames hold is that delete.
    /// </summary>
    private static async Task ScrubAsync(IDbConnectionFactory db, ILogger logger)
    {
        try
        {
            using var conn = db.CreateConnection();
            await conn.ExecuteAsync("PRAGMA secure_delete = ON");
            if (!await CheckpointCompletedAsync(conn))
            {
                logger.LogWarning("Stored event repair: the WAL could not be checkpointed (busy); the scrub runs again at the next start");
                return;
            }
            await conn.ExecuteAsync("VACUUM");
            if (!await CheckpointCompletedAsync(conn))
            {
                logger.LogWarning("Stored event repair: the rebuilt file could not be checkpointed (busy); the scrub runs again at the next start");
                return;
            }
            await conn.ExecuteAsync("DELETE FROM tbl_blind_state WHERE key = @key", new { key = CleanupPendingKey });
            await CheckpointCompletedAsync(conn);
            logger.LogInformation("Stored event repair: database file scrubbed");
        }
        catch (DbException ex)
        {
            logger.LogWarning(ex, "Stored event repair: scrubbing the database file failed; it runs again at the next start");
        }
    }

    /// <summary>
    /// <c>wal_checkpoint(TRUNCATE)</c> returns (busy, frames in the WAL, frames checkpointed); it is complete
    /// only when nothing held it back and every frame reached the main file (-1/-1 outside WAL mode).
    /// </summary>
    private static async Task<bool> CheckpointCompletedAsync(IDbConnection conn)
    {
        var (busy, log, checkpointed) = await conn.QuerySingleAsync<(long, long, long)>("PRAGMA wal_checkpoint(TRUNCATE)");
        return busy == 0 && log == checkpointed;
    }
}
