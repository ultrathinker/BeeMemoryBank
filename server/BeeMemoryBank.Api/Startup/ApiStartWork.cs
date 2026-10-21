using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Api.Startup;

/// <summary>
/// The one-shot work of a start, as methods (docs/design/step-b-spec.md 5.3 table 2). A plain node runs them inline, in the order and with the
/// statements <see cref="ApiStartupTasks"/> always had; a sealed node runs the same work as <see cref="IVaultOpenStep"/>s each time its database
/// opens (below), because it cannot read its database before a door has opened it.
/// </summary>
internal static class ApiStartWork
{
    /// <summary>Vault migrations, then the repair of stored events (open step 100).</summary>
    public static async Task RunMigrationsAsync(IServiceProvider services)
    {
        using (var scope = services.CreateScope())
        {
            var migrator = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
            await migrator.RunMigrationsAsync();
        }
        await StoredEventRepair.RunAsync(services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<ILogger<Program>>());
    }

    /// <summary>Bootstrap tbl_folder from existing article tree_path values (one-time, idempotent; open step 200).</summary>
    public static async Task BootstrapFoldersAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var bootstrapper = scope.ServiceProvider.GetRequiredService<FolderBootstrapper>();
        await bootstrapper.RunIfNeededAsync();
    }

    /// <summary>
    /// Initialize the AI chat DB schema (separate chat.db; idempotent CREATE TABLE IF NOT EXISTS; open step 300). Placed AFTER the migration and
    /// bootstrapper blocks and deliberately does NOT use MigrationRunner or Storage/Migrations. See docs/ai-chat-implementation-plan.md §1
    /// ("Chat DB"). A blind node has no AI chat.
    /// </summary>
    public static async Task InitializeChatAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var chatInitializer = scope.ServiceProvider.GetRequiredService<ChatDbInitializer>();
        await chatInitializer.InitializeAsync();
    }

    /// <summary>Restore the Lamport clock from the database (open step 400, every open).</summary>
    public static async Task RestoreClockAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var maxTs = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxLamportTimestampAsync();
        services.GetRequiredService<LamportClock>().Initialize(maxTs);
    }

    /// <summary>
    /// The crash-recovery sweeps of a start (open step 500, first open of the process only): a restore, a standalone-restore staging file, a DEK
    /// rotation and the media staging folder that a process which died left behind, and - on a plain node - the working copies of snapshots.
    /// </summary>
    public static async Task RunCrashSweepsAsync(IServiceProvider services, string dataPath, bool sweepSnapshotStaging)
    {
        using var scope = services.CreateScope();
        // Crash-recovery sweep for restore flow: any tbl_restore_event_state row stuck in
        // Downloading or Applying means the previous process died mid-restore. Mark them Failed
        // so the admin sees a clear NeedsAdminDecision in the UI and can re-initiate or cancel.
        // Without this, the row sits forever — the RESTORE_NETWORK event is already in tbl_event,
        // so the next sync pull won't redeliver it, and the orchestrator sees state != Pending
        // and silently no-ops.
        var stateRepo = scope.ServiceProvider.GetRequiredService<IRestoreEventStateRepository>();
        var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        var stuck = (await stateRepo.GetByStateAsync(RestoreEventState.Downloading))
            .Concat(await stateRepo.GetByStateAsync(RestoreEventState.Applying))
            .ToList();
        foreach (var row in stuck)
        {
            await stateRepo.UpdateStateAsync(row.EventId, RestoreEventState.Failed,
                $"Restore was interrupted by process restart while in state {row.State}. Re-initiate from /Admin/Snapshots or cancel.");
            startupLogger.LogWarning(
                "Marked stuck restore {EventId} (was {OldState}) as Failed during startup recovery.",
                row.EventId, row.State);
        }

        // Standalone restore writes a `<dbpath>.standalone-staging` file as part of its atomic-swap
        // sequence. If the process died between the staging-file commit and the File.Move, the live
        // DB is intact (good) but the staging file is leftover. It contains the snapshot originator's
        // identity, so leaving it on disk is mildly sensitive — clean up.
        var stagingPath = Path.Combine(dataPath, "beememorybank.db.standalone-staging");
        if (File.Exists(stagingPath))
        {
            try { File.Delete(stagingPath); }
            catch (Exception ex) { startupLogger.LogWarning(ex, "Failed to delete leftover standalone-staging file"); }
            startupLogger.LogWarning("Removed leftover standalone restore staging file from a previous interrupted restore.");
        }

        // Crash-recovery sweep for DEK rotation: any tbl_dek_rotation_state row stuck in
        // Committing means the previous process died mid-rotation. The destructive re-wrap and
        // the state→Applied write share ONE transaction, so:
        //   • tx committed → state=Applied AND DB on new DEK (atomic)
        //   • tx rolled back → state stays Committing AND DB still on old DEK (atomic)
        // So a state=Committing row at startup proves the tx rolled back. DB is on the old DEK,
        // marking the row Failed is correct, and the admin can retry safely.
        var dekStateRepo = scope.ServiceProvider.GetRequiredService<IDekRotationStateRepository>();
        var nodeIdRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var localIdentity = await nodeIdRepo.GetAsync();
        var localNodeIdStr = localIdentity?.NodeId.ToString() ?? string.Empty;

        // For Committing rows: only mark Failed those originated by THIS node. Peer-originated
        // rows may still be auto-accepted on next unlock (RetryPendingAutoAcceptsAsync), or
        // manually accepted by the admin. Marking them Failed here would prevent both paths.
        var eventLogRepo = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
        var stuckDek = await dekStateRepo.GetByStateAsync(DekRotationState.Committing);
        foreach (var row in stuckDek)
        {
            var commit = await eventLogRepo.GetByIdAsync(row.EventId);
            var isLocallyOriginated = commit != null
                && commit.NodeId.ToString().Equals(localNodeIdStr, StringComparison.OrdinalIgnoreCase);
            if (!isLocallyOriginated)
            {
                startupLogger.LogInformation(
                    "DEK rotation {EventId} from peer {NodeId} left in Committing — will retry on next unlock or manual accept.",
                    row.EventId, commit?.NodeId);
                continue;
            }
            await dekStateRepo.UpdateStateAsync(row.EventId, DekRotationState.Failed,
                $"DEK rotation interrupted by process restart while in state {row.State}. The DB itself is consistent (single-tx atomic). Re-initiate or cancel from /Admin/DekRotation.");
            startupLogger.LogWarning(
                "Marked stuck DEK rotation {EventId} (was {OldState}) as Failed during startup recovery.",
                row.EventId, row.State);
        }

        // Sweep stale Proposed rows (>24h or past explicit ExpiresAt) → Cancelled. Without this,
        // a node that received a PROPOSED but never the matching COMMIT would accumulate them
        // forever.
        var stuckProposed = await dekStateRepo.GetByStateAsync(DekRotationState.Proposed);
        foreach (var row in stuckProposed)
        {
            if (DateTime.TryParse(row.CreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var created)
                && created < DateTime.UtcNow.AddHours(-24))
            {
                await dekStateRepo.UpdateStateAsync(row.EventId, DekRotationState.Cancelled,
                    "Proposed event expired without matching COMMIT — auto-cancelled at startup.");
                startupLogger.LogInformation("Expired stale Proposed DEK rotation {EventId} (created {CreatedAt})",
                    row.EventId, row.CreatedAt);
            }
        }

        // A restore stages media in data/media.staging, commits the database, and only then swaps
        // the staging folder into data/media. A process that died after the commit but before the
        // swap left the restored legacy media (bytes only in .enc files, no blob) in media.staging:
        // resume that first, moving in what the committed tbl_media names. Then the orphan sweep:
        // *.enc files in data/media that no row references belong to the state a restore replaced.
        try
        {
            var snapshots = services.GetRequiredService<SnapshotService>();
            snapshots.ResumeMediaStaging();
            snapshots.CleanupOrphanMediaFiles();
        }
        catch (Exception ex)
        {
            startupLogger.LogWarning(ex, "Startup media reconciliation failed (non-fatal).");
        }

        if (sweepSnapshotStaging) SweepSnapshotStaging(dataPath, startupLogger);
    }

    /// <summary>
    /// A snapshot's working copy of the database (the whole vault until it is filtered) that a kill or power cut left in data/tmp. Nothing is in
    /// progress at start. On a sealed node the copies are encrypted, and the sweep still runs, before the database opens (spec 5.2 step 5).
    /// </summary>
    public static void SweepSnapshotStaging(string dataPath, ILogger logger)
    {
        var staleSnapshotCopies = BeeMemoryBank.Core.IO.SnapshotStaging.Sweep(dataPath);
        if (staleSnapshotCopies > 0)
            logger.LogInformation("Startup: removed {Count} working copy file(s) an interrupted snapshot left", staleSnapshotCopies);
    }

    /// <summary>
    /// Backfill legacy media ciphertext into the content-addressed blob store, in background (16b; open step 600, first open). Idempotent and
    /// additive: reads each .enc file whose row has no hash yet, stores it as a blob and stamps the hash. Migration 023 can only backfill from
    /// surviving media_create events, which are compacted away on a long-lived node, so this disk pass is what actually makes the blob store the
    /// complete home for media. No decryption, no deletion — safe to run every startup.
    /// </summary>
    public static void StartMediaBackfill(IServiceProvider services)
    {
        var mediaBackfillLogger = services.GetRequiredService<ILogger<Program>>();
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<MediaBlobBackfillService>();
                var result = await svc.BackfillAsync();
                if (result.Stored > 0 || result.MissingFile > 0)
                    mediaBackfillLogger.LogInformation(
                        "Media blob backfill on startup: {Stored} stored, {Missing} missing file, {Already} already done.",
                        result.Stored, result.MissingFile, result.AlreadyDone);

                // Once every media is in the blob store, delete the now-redundant .enc files.
                // Safe by construction — SweepRedundantEncFilesAsync only removes a file whose blob it
                // has just confirmed present, so the blob is always the surviving copy.
                var swept = await svc.SweepRedundantEncFilesAsync();
                if (swept > 0)
                    mediaBackfillLogger.LogInformation("Media .enc sweep on startup: deleted {Swept} redundant file(s).", swept);
            }
            catch (Exception ex)
            {
                mediaBackfillLogger.LogError(ex, "Media blob backfill failed");
            }
        });
    }

    /// <summary>Backfill concept tag embeddings in background (open step 700, first open). Not on a blind node: it has no model.</summary>
    public static void StartConceptTagBackfill(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var conceptTagService = scope.ServiceProvider.GetRequiredService<ConceptTagService>();
        var backfillLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        backfillLogger.LogInformation("Starting concept tag embedding backfill in background...");
        _ = Task.Run(async () =>
        {
            try
            {
                await conceptTagService.BackfillEmbeddingsAsync();
                backfillLogger.LogInformation("Concept tag embedding backfill completed");
            }
            catch (Exception ex)
            {
                backfillLogger.LogError(ex, "Concept tag embedding backfill failed");
            }
        });
    }
}

/// <summary>Open step 100: the vault migrations and the repair of stored events, at every open (idempotent).</summary>
internal sealed class MigrationsOpenStep(IServiceProvider services) : IVaultOpenStep
{
    public int Order => 100;
    public Task RunAsync(bool firstOpenInProcess, CancellationToken ct) => ApiStartWork.RunMigrationsAsync(services);
}

/// <summary>Open step 200: the folder bootstrap, at every open.</summary>
internal sealed class FolderBootstrapOpenStep(IServiceProvider services) : IVaultOpenStep
{
    public int Order => 200;
    public Task RunAsync(bool firstOpenInProcess, CancellationToken ct) => ApiStartWork.BootstrapFoldersAsync(services);
}

/// <summary>Open step 300: the chat database schema, at every open (after the chat factory is keyed).</summary>
internal sealed class ChatSchemaOpenStep(IServiceProvider services) : IVaultOpenStep
{
    public int Order => 300;
    public Task RunAsync(bool firstOpenInProcess, CancellationToken ct) => ApiStartWork.InitializeChatAsync(services);
}

/// <summary>Open step 400: the Lamport clock is set to the highest timestamp the log holds, at every open.</summary>
internal sealed class LamportClockOpenStep(IServiceProvider services) : IVaultOpenStep
{
    public int Order => 400;
    public Task RunAsync(bool firstOpenInProcess, CancellationToken ct) => ApiStartWork.RestoreClockAsync(services);
}

/// <summary>Open step 500: the crash-recovery sweeps, at the first open of the process only.</summary>
internal sealed class CrashSweepsOpenStep(IServiceProvider services, string dataPath) : IVaultOpenStep
{
    public int Order => 500;

    public Task RunAsync(bool firstOpenInProcess, CancellationToken ct) =>
        firstOpenInProcess ? ApiStartWork.RunCrashSweepsAsync(services, dataPath, sweepSnapshotStaging: false) : Task.CompletedTask;
}

/// <summary>Open step 600: the media blob backfill, in the background, at the first open of the process only.</summary>
internal sealed class MediaBackfillOpenStep(IServiceProvider services) : IVaultOpenStep
{
    public int Order => 600;

    public Task RunAsync(bool firstOpenInProcess, CancellationToken ct)
    {
        if (firstOpenInProcess) ApiStartWork.StartMediaBackfill(services);
        return Task.CompletedTask;
    }
}

/// <summary>Open step 700: the concept tag embedding backfill, in the background, at the first open of the process only.</summary>
internal sealed class ConceptTagBackfillOpenStep(IServiceProvider services) : IVaultOpenStep
{
    public int Order => 700;

    public Task RunAsync(bool firstOpenInProcess, CancellationToken ct)
    {
        if (firstOpenInProcess) ApiStartWork.StartConceptTagBackfill(services);
        return Task.CompletedTask;
    }
}
