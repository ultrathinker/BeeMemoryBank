using System.Text.Encodings.Web;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection.Extensions;

// Safety net for unobserved Task exceptions from `_ = Task.Run(...)` fire-and-forget
// sites (DEK rotation retry, network restore, embedding backfill, etc.). Without this,
// an exception thrown before the inner try/catch is reached (e.g. CreateScope failure,
// OOM, StackOverflow) would crash the process when GC finalizes the Task. Mark
// SetObserved so the host doesn't escalate.
TaskScheduler.UnobservedTaskException += (sender, e) =>
{
    Console.Error.WriteLine($"[UnobservedTaskException] {e.Exception}");
    e.SetObserved();
};

var builder = WebApplication.CreateBuilder(args);

// BMB_INTERNAL_KEY: shared secret for Web→API internal auth (added to every request by InternalKeyHandler).
// In production: always set by docker-entrypoint.sh before both processes start.
// In development: auto-generated per-run and stored in {dataPath}/.internal-key (shared with Web UI).
// FAIL-FAST: refuse to start in production if the key is missing — means entrypoint was bypassed.
if (builder.Environment.IsProduction() &&
    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY")))
{
    throw new InvalidOperationException(
        "BMB_INTERNAL_KEY is not set. In production it must be exported by docker-entrypoint.sh " +
        "before the API process starts. Do not override ENTRYPOINT or run the API directly.");
}

var dataPath = builder.Configuration["BeeMemoryBank:DataPath"]
    ?? Environment.GetEnvironmentVariable("BMB_DATA_PATH")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "data");

Directory.CreateDirectory(dataPath);

// Dev-only: auto-generate BMB_INTERNAL_KEY from a local file shared with the Web UI process.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY")))
{
    var keyFile = Path.Combine(dataPath, ".internal-key");
    string key;
    if (File.Exists(keyFile))
    {
        key = File.ReadAllText(keyFile).Trim();
    }
    else
    {
        key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(keyFile, key);
    }
    Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", key);
}

builder.Services.AddStorage(dataPath);
builder.Services.AddCore();
builder.Services.AddMemoryCache();
builder.Services.AddOnnxEmbeddings();
builder.Services.AddSync();
builder.Services.AddSingleton<SyncTokenStore>();
// BMB_SYNC_INTERVAL_SECONDS: override scheduler tick (default 60s). Useful for tests with
// fast iteration; set to e.g. 5 to push/pull every 5s. Production should leave it unset.
TimeSpan? syncInterval = int.TryParse(Environment.GetEnvironmentVariable("BMB_SYNC_INTERVAL_SECONDS"), out var s) && s >= 1
    ? TimeSpan.FromSeconds(s) : null;
builder.Services.AddSyncScheduler(interval: syncInterval, periodicCleanupFactory: sp =>
    sp.GetRequiredService<SyncTokenStore>().CleanupExpired);
builder.Services.AddCleanupService();
builder.Services.AddEmbeddingProcessor();
builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();

// Route CallerScope through HttpContext.Items so it survives child DI scopes.
// The MCP SDK (ModelContextProtocol) creates a fresh IServiceScope per tool invocation;
// a plain scoped CallerScopeHolder would be a brand-new instance there — defaulting to
// SystemCallerScope — which would silently bypass every folder ACL check. See
// HttpContextCallerScopeStore for details.
builder.Services.Replace(ServiceDescriptor.Scoped<ICallerScopeStore, HttpContextCallerScopeStore>());

builder.Services.AddScoped<IActorProvider, BeeMemoryBank.Api.Services.HttpActorProvider>();
builder.Services.AddSingleton(sp =>
    new SnapshotService(dataPath, sp.GetRequiredService<DbConnectionFactory>(),
        sp.GetRequiredService<INodeIdentityRepository>(),
        sp.GetRequiredService<ILamportClock>(),
        sp.GetRequiredService<ILogger<SnapshotService>>(),
        sp.GetRequiredService<IRestoreReplayShieldRepository>(),
        sp.GetRequiredService<IWhitelistRepository>(),
        sp.GetService<BeeMemoryBank.Core.Services.SessionService>()));
// Singleton: SnapshotRestoreService holds in-memory progress state for /restore/progress polling.
// Task.Run flows in EventApplier and SnapshotEndpoints fire-and-forget, so the service must outlive
// the request scope. Scoped dependencies (repositories) are resolved via IServiceScopeFactory per
// operation to avoid capturing a single scope at construction time.
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<SnapshotRestoreService>(sp, dataPath));
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<DekRotationService>(sp, dataPath));
builder.Services.AddSingleton<IDekRotationApplier>(sp => sp.GetRequiredService<DekRotationService>());
// LazySlotRewrapService is registered by AddSync() in Sync DI now (so CLI/mobile get it too).
builder.Services.AddSingleton<BeeMemoryBank.Sync.IRestoreInitiator>(sp => sp.GetRequiredService<SnapshotRestoreService>());
// Core-side retry contract: SessionService.UnlockCoreAsync resolves IRestoreRetrier to sweep
// stuck restore events on every unlock (mirrors the DEK-rotation retry pattern).
builder.Services.AddSingleton<IRestoreRetrier>(sp => sp.GetRequiredService<SnapshotRestoreService>());
builder.Services.AddSingleton(new McpResponseManager(dataPath));
builder.Services.AddSingleton<DownloadTokenService>();
builder.Services.AddHostedService<DownloadCleanupHostedService>();
builder.Services.AddHostedService<AuditLogPruningHostedService>();
builder.Services.AddScoped<ZipExportService>();
builder.Services.AddScoped<CompactionService>();
builder.Services.AddSingleton<SnapshotJoinCache>();
var mediaDir = Path.Combine(dataPath, "media");
Directory.CreateDirectory(mediaDir);
builder.Services.AddSingleton(new BeeMemoryBank.Core.Services.MediaStorageOptions(mediaDir));
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 500L * 1024 * 1024;
});
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(o =>
{
    o.Limits.MaxRequestBodySize = 500L * 1024 * 1024;
});
builder.Services.AddOpenApi();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<BeeSearchTools>()
    .WithTools<BeeReadTools>()
    .WithTools<BeeWriteTools>()
    .WithTools<BeeSessionTools>()
    .WithTools<BeeUploadTools>()
    .WithTools<BeeAuditTools>()
    .WithTools<BeeConceptTools>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    // Serialize enums as strings ("Idle", "Downloading", ...) instead of numeric. The Web proxy
    // deserializes RestoreProgressDto.CurrentStep as a string — a numeric default would 500 the
    // login page during any restore. Applies to all endpoints that return enum-typed properties.
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

var app = builder.Build();

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

// Rate limiting for sensitive endpoints (unlock, join) — brute-force protection
app.UseMiddleware<BeeMemoryBank.Api.Middleware.RateLimitMiddleware>();

// Maintenance mode — blocks all requests except snapshot restore and session unlock
app.UseMiddleware<BeeMemoryBank.Api.Middleware.MaintenanceMiddleware>();

// Agent bearer auth (non-blocking, auto-unlock)
app.UseMiddleware<BeeMemoryBank.Api.Middleware.AgentAuthMiddleware>();

// Ambient caller scope — resolves folder ACL once per request, repos filter reads automatically
app.UseMiddleware<BeeMemoryBank.Api.Middleware.CallerScopeMiddleware>();

// Error handling
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
        var ex = feature?.Error;
        var (statusCode, message) = ex switch
        {
            KeyNotFoundException e => (404, e.Message),
            ArgumentException e => (400, e.Message),
            UnauthorizedAccessException e => (403, e.Message),
            InvalidOperationException e => (409, e.Message),
            _ => (500, "Internal server error")
        };
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new ErrorResponse(message));
    });
});

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }))
   .WithTags("Health");

app.MapGet("/api/version", () =>
{
    var asm = System.Reflection.Assembly.GetExecutingAssembly();
    var location = asm.Location;
    var deployedAt = File.Exists(location)
        ? File.GetLastWriteTimeUtc(location)
        : DateTime.UtcNow;
    return Results.Ok(new { deployedAt, build = "2026-04-18" });
}).WithTags("Health").AllowAnonymous();

app.MapSessionEndpoints();
app.MapArticleEndpoints();
app.MapTreeEndpoints();
app.MapConceptTagEndpoints();
app.MapFolderEndpoints();
app.MapSearchEndpoints();
app.MapKeyEndpoints();
app.MapWhitelistEndpoints();
app.MapAgentEndpoints();
app.MapUserEndpoints();
app.MapJoinEndpoints();
app.MapInitEndpoints();
app.MapSyncEndpoints();
app.MapSnapshotEndpoints();
    app.MapDekRotationEndpoints();
app.MapActivityEndpoints();
app.MapCommentEndpoints();
app.MapRestrictionEndpoints();
app.MapDownloadEndpoints();
    app.MapMediaEndpoints();
    app.MapVersionEndpoints();
    app.MapObsidianImportEndpoints();
    app.MapHardDeleteEndpoints();
    app.MapCompactionEndpoints();
    app.MapAdminEndpoints();
    app.MapMcp("/mcp");

app.Run();

// Required for WebApplicationFactory in tests
public partial class Program { }
