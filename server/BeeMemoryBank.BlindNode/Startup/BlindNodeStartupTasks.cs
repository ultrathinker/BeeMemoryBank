using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Startup;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.BlindNode.Startup;

/// <summary>
/// One-shot work between <c>builder.Build()</c> and the first request - the blind half of BeeMemoryBank.Api's
/// ApiStartupTasks.cs, in the same order (the order is load-bearing: migrations before anything that reads a
/// table, the restore sweep before the flow can be re-entered). Dropped on purpose: the desktop orchestrator's
/// ready file and stdin lifeline, the chat database, OS auto-unlock, the concept-tag embedding backfill.
/// </summary>
public static class BlindNodeStartupTasks
{
    public static async Task RunBlindNodeStartupTasksAsync(this WebApplication app, string dataPath)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();

        // A blind seed's cutover interrupted by a crash is finished or rolled back before anything opens the database.
        BlindSeedCutover.Recover(dataPath, logger);

        using (var scope = app.Services.CreateScope())
        {
            var migrator = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
            await migrator.RunMigrationsAsync();
        }
        await StoredEventRepair.RunAsync(app.Services.GetRequiredService<IServiceScopeFactory>(), logger);

        // Identity with the key in a file, leftover temp files. Right after migrations - which a blind node runs
        // like any other, no DEK involved.
        await BlindRoleStartup.RunAsync(app.Services, dataPath, logger);

        // Bootstrap tbl_folder from existing article tree_path values (one-time, idempotent)
        using (var scope = app.Services.CreateScope())
        {
            var bootstrapper = scope.ServiceProvider.GetRequiredService<FolderBootstrapper>();
            await bootstrapper.RunIfNeededAsync();
        }

        // Restore Lamport clock from DB
        {
            using var scope = app.Services.CreateScope();
            var maxTs = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxLamportTimestampAsync();
            app.Services.GetRequiredService<LamportClock>().Initialize(maxTs);
        }

        // Crash-recovery sweeps (restore flow, DEK rotation state, leftover staging file, orphan media).
        {
            using var scope = app.Services.CreateScope();
            var stateRepo = scope.ServiceProvider.GetRequiredService<IRestoreEventStateRepository>();
            var stuck = (await stateRepo.GetByStateAsync(RestoreEventState.Downloading))
                .Concat(await stateRepo.GetByStateAsync(RestoreEventState.Applying))
                .ToList();
            foreach (var row in stuck)
            {
                await stateRepo.UpdateStateAsync(row.EventId, RestoreEventState.Failed,
                    $"Restore was interrupted by process restart while in state {row.State}. Re-initiate from /Admin/Snapshots or cancel.");
                logger.LogWarning(
                    "Marked stuck restore {EventId} (was {OldState}) as Failed during startup recovery.",
                    row.EventId, row.State);
            }

            // Standalone restore writes a `<dbpath>.standalone-staging` file as part of its atomic swap; a process
            // that died between the staging commit and the File.Move leaves it behind, carrying the snapshot
            // originator's identity. Clean up.
            var stagingPath = Path.Combine(dataPath, "beememorybank.db.standalone-staging");
            if (File.Exists(stagingPath))
            {
                try { File.Delete(stagingPath); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to delete leftover standalone-staging file"); }
                logger.LogWarning("Removed leftover standalone restore staging file from a previous interrupted restore.");
            }

            // A DEK rotation row stuck in Committing proves its transaction rolled back (the re-wrap and the state
            // write share one): mark the ones this node originated Failed, leave a peer's for the next accept.
            var dekStateRepo = scope.ServiceProvider.GetRequiredService<IDekRotationStateRepository>();
            var nodeIdRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
            var localIdentity = await nodeIdRepo.GetAsync();
            var localNodeIdStr = localIdentity?.NodeId.ToString() ?? string.Empty;
            var eventLogRepo = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
            var stuckDek = await dekStateRepo.GetByStateAsync(DekRotationState.Committing);
            foreach (var row in stuckDek)
            {
                var commit = await eventLogRepo.GetByIdAsync(row.EventId);
                var isLocallyOriginated = commit != null
                    && commit.NodeId.ToString().Equals(localNodeIdStr, StringComparison.OrdinalIgnoreCase);
                if (!isLocallyOriginated)
                {
                    logger.LogInformation(
                        "DEK rotation {EventId} from peer {NodeId} left in Committing - will retry on next unlock or manual accept.",
                        row.EventId, commit?.NodeId);
                    continue;
                }
                await dekStateRepo.UpdateStateAsync(row.EventId, DekRotationState.Failed,
                    $"DEK rotation interrupted by process restart while in state {row.State}. The DB itself is consistent (single-tx atomic). Re-initiate or cancel from /Admin.");
                logger.LogWarning(
                    "Marked stuck DEK rotation {EventId} (was {OldState}) as Failed during startup recovery.",
                    row.EventId, row.State);
            }

            // Sweep stale Proposed rows (> 24 h) to Cancelled: a PROPOSED whose COMMIT never came would otherwise pile up.
            var stuckProposed = await dekStateRepo.GetByStateAsync(DekRotationState.Proposed);
            foreach (var row in stuckProposed)
            {
                if (DateTime.TryParse(row.CreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var created)
                    && created < DateTime.UtcNow.AddHours(-24))
                {
                    await dekStateRepo.UpdateStateAsync(row.EventId, DekRotationState.Cancelled,
                        "Proposed event expired without matching COMMIT - auto-cancelled at startup.");
                    logger.LogInformation("Expired stale Proposed DEK rotation {EventId} (created {CreatedAt})",
                        row.EventId, row.CreatedAt);
                }
            }

            // A network-wide restore copies media files in BEFORE the SQL commit; a process that died in that
            // window leaves orphan *.enc files. Reconcile.
            try
            {
                app.Services.GetRequiredService<SnapshotService>().CleanupOrphanMediaFiles();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Startup orphan-media cleanup failed (non-fatal).");
            }
        }

        // Backfill legacy media ciphertext into the content-addressed blob store, in background. Idempotent,
        // additive, no decryption; the sweep only removes a file whose blob it has just confirmed present.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = app.Services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<MediaBlobBackfillService>();
                var result = await svc.BackfillAsync();
                if (result.Stored > 0 || result.MissingFile > 0)
                    logger.LogInformation(
                        "Media blob backfill on startup: {Stored} stored, {Missing} missing file, {Already} already done.",
                        result.Stored, result.MissingFile, result.AlreadyDone);
                var swept = await svc.SweepRedundantEncFilesAsync();
                if (swept > 0)
                    logger.LogInformation("Media .enc sweep on startup: deleted {Swept} redundant file(s).", swept);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Media blob backfill failed");
            }
        });

        // Session cleanup: wipe the (never populated) master DEK slot on shutdown, as every node does.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            var session = app.Services.GetRequiredService<SessionService>();
            session.ClearPendingDek();
            session.Lock();
            logger.LogInformation("Session locked on application shutdown");
        });
    }
}
