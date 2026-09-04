using System.Text.Encodings.Web;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Hosting.AspNetCore;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Api.Startup;

/// <summary>
/// One-shot work between <c>builder.Build()</c> and the first request: migrations, bootstrappers,
/// crash-recovery sweeps, clock restore, auto-unlock.
///
/// <para>Deliberately NOT converted into <c>IHostedService</c>s. Their order is load-bearing —
/// migrations before anything that reads a table, the restore sweep before the flow can be
/// re-entered — and hosted services are ordered by registration in a way that is easy to disturb
/// from the other end of the file. A numbered list of awaits says what it does.</para>
/// </summary>
public static class ApiStartupTasks
{
    public static async Task RunBeeApiStartupTasksAsync(this WebApplication app, string dataPath)
    {
// NOTE: UseLoopbackForwardedHeaders() is deliberately NOT called here. It belongs to the pipeline,
// not to the startup tasks, and Program.cs calls it immediately before this method -- exactly
// where the pre-split file had it. The split briefly had it in both places, registering two
// ForwardedHeadersMiddleware instances: with ForwardLimit = 1 each, two passes consume two hops
// whenever the first one is itself a trusted proxy, which is the shape a spoofed X-Forwarded-For
// needs to survive.

// BMB_READY_FILE: signals startup completion to a parent orchestrator (bmbd) by writing
// {pid, urls, applicationName, version, startupTimeUtc} once Kestrel has bound its actual
// port — ApplicationStarted fires after that. Off by default: standalone/Docker/tests don't
// set this env var and see no behavior change.
var readyFilePath = Environment.GetEnvironmentVariable("BMB_READY_FILE");
if (!string.IsNullOrEmpty(readyFilePath))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var readyInfo = new BeeMemoryBank.Hosting.ReadyFileInfo(
            Pid: Environment.ProcessId,
            Urls: app.Urls.ToList(),
            ApplicationName: "BeeMemoryBank.Api",
            Version: BeeMemoryBank.Api.Helpers.AppVersion.Current,
            StartupTimeUtc: DateTime.UtcNow
        );
        BeeMemoryBank.Hosting.ReadyFileManager.Write(readyFilePath, readyInfo);
    });
}

// BMB_STDIN_LIFELINE: when the parent orchestrator closes this process's stdin (graceful
// stop signal) or dies without closing it (still an EOF from this end), trigger a normal
// graceful shutdown via StopApplication() instead of relying solely on a hard kill.
if (Environment.GetEnvironmentVariable("BMB_STDIN_LIFELINE") == "1")
{
    BeeMemoryBank.Hosting.StdinLifeline.Start(() => app.Lifetime.StopApplication());
}

// Run migrations on startup
using (var scope = app.Services.CreateScope())
{
    var migrator = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
    await migrator.RunMigrationsAsync();
}

// Bootstrap tbl_folder from existing article tree_path values (one-time, idempotent)
using (var scope = app.Services.CreateScope())
{
    var bootstrapper = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Storage.Sqlite.FolderBootstrapper>();
    await bootstrapper.RunIfNeededAsync();
}

// Initialize the AI chat DB schema (separate chat.db; idempotent CREATE TABLE IF NOT EXISTS).
// Placed AFTER the beedb migration/bootstrapper blocks and deliberately does NOT use
// MigrationRunner or Storage/Migrations. See docs/ai-chat-implementation-plan.md §1 ("Chat DB").
using (var scope = app.Services.CreateScope())
{
    var chatInitializer = scope.ServiceProvider.GetRequiredService<ChatDbInitializer>();
    await chatInitializer.InitializeAsync();
}

// Restore Lamport clock from DB
{
    using var scope = app.Services.CreateScope();
    var maxTs = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxLamportTimestampAsync();
    app.Services.GetRequiredService<LamportClock>().Initialize(maxTs);
}

// Crash-recovery sweep for restore flow: any tbl_restore_event_state row stuck in
// Downloading or Applying means the previous process died mid-restore. Mark them Failed
// so the admin sees a clear NeedsAdminDecision in the UI and can re-initiate or cancel.
// Without this, the row sits forever — the RESTORE_NETWORK event is already in tbl_event,
// so the next sync pull won't redeliver it, and the orchestrator sees state != Pending
// and silently no-ops.
{
    using var scope = app.Services.CreateScope();
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
    // (Claude R2 prod review CRIT-1.)
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
            $"DEK rotation interrupted by process restart while in state {row.State}. The DB itself is consistent (single-tx atomic). Re-initiate or cancel from /Admin.");
        startupLogger.LogWarning(
            "Marked stuck DEK rotation {EventId} (was {OldState}) as Failed during startup recovery.",
            row.EventId, row.State);
    }

    // Sweep stale Proposed rows (>24h or past explicit ExpiresAt) → Cancelled. Without this,
    // a node that received a PROPOSED but never the matching COMMIT would accumulate them
    // forever. (Claude R2 prod review CRIT-2.)
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

    // Network-wide restore copies media files into data/media BEFORE the SQL commit so that
    // tbl_media row inserts are guaranteed to find the file on disk. If the process died in
    // that window, we have orphan *.enc files (DB has no row referencing them). Reconcile here.
    try
    {
        app.Services.GetRequiredService<SnapshotService>().CleanupOrphanMediaFiles();
    }
    catch (Exception ex)
    {
        startupLogger.LogWarning(ex, "Startup orphan-media cleanup failed (non-fatal).");
    }
}

// ── OS auto-unlock (opt-in, Windows-only) ──────────────────────────────────────────────────
// Attempt to auto-unlock the session using the DPAPI-protected secret, if the feature was
// previously enabled by the admin. This runs in-process (Api's SessionService is the
// authoritative singleton), after all migrations so the tbl_key_slot table is fully ready.
// No new HTTP endpoint is needed: SessionService lives in-process and UnlockWithDek/
// TryAutoUnlockAsync are direct method calls. If auto-unlock fails for any reason (file absent,
// DPAPI decryption error, sentinel mismatch) we log a warning and continue — the admin can still
// unlock manually via /Login.
if (OperatingSystem.IsWindows())
{
    using var autoUnlockScope = app.Services.CreateScope();
    var autoUnlockLogger = autoUnlockScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var autoUnlockSvc = app.Services.GetService<BeeMemoryBank.Core.Services.OsAutoUnlockService>();
        if (autoUnlockSvc != null)
        {
            var nodeRepo = autoUnlockScope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Interfaces.INodeIdentityRepository>();
            var unlocked = await autoUnlockSvc.TryAutoUnlockAsync(nodeRepo);
            if (unlocked)
                autoUnlockLogger.LogInformation("Session auto-unlocked via OS auto-unlock slot (DPAPI).");
            else
                autoUnlockLogger.LogDebug("OS auto-unlock: slot or secret file not present, or unlock failed — manual unlock required.");
        }
    }
    catch (Exception ex)
    {
        app.Services.GetRequiredService<ILogger<Program>>()
            .LogWarning(ex, "OS auto-unlock attempt failed at startup (non-fatal); manual unlock required.");
    }
}

// Backfill concept tag embeddings in background
{
    using var scope = app.Services.CreateScope();
    var conceptTagService = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Services.ConceptTagService>();
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

// Session cleanup: securely wipe master DEK from memory on shutdown
app.Lifetime.ApplicationStopping.Register(() =>
{
    var session = app.Services.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>();
    session.ClearPendingDek();
    session.Lock();
    app.Services.GetRequiredService<ILogger<Program>>()
        .LogInformation("Session locked on application shutdown — master DEK wiped from memory");
});

    }
}
