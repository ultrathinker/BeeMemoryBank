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

builder.Services.AddLoopbackForwardedHeaders(builder.Configuration);

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
builder.Services.AddOnnxEmbeddings(dataPath);
builder.Services.AddSync();
builder.Services.AddSingleton<SyncTokenStore>();
builder.Services.AddSingleton<BeeMemoryBank.Api.Services.IPublicHostValidator, BeeMemoryBank.Api.Services.DnsPublicHostValidator>();
// BMB_SYNC_INTERVAL_SECONDS: override scheduler tick (default 60s). Useful for tests with
// fast iteration; set to e.g. 5 to push/pull every 5s. Production should leave it unset.
TimeSpan? syncInterval = int.TryParse(Environment.GetEnvironmentVariable("BMB_SYNC_INTERVAL_SECONDS"), out var s) && s >= 1
    ? TimeSpan.FromSeconds(s) : null;
builder.Services.AddSyncScheduler(interval: syncInterval, periodicCleanupFactory: sp =>
    sp.GetRequiredService<SyncTokenStore>().CleanupExpired);
builder.Services.AddCleanupService();

// BMB_EMBEDDING_INTERVAL_SECONDS / BMB_EMBEDDING_BATCH_SIZE, BMB_INDEX_INTERVAL_SECONDS /
// BMB_INDEX_BATCH_SIZE: override the pending-embedding / pending-index processors' tick interval
// (default 5 min) and per-cycle batch size (default 50). Unset in production; useful for a
// one-time mass-import catch-up on an existing deployment where waiting out the default drip-feed
// schedule would take hours. See also POST /api/admin/search/embeddings/backfill for an
// on-demand full drain that doesn't require restarting the process at all.
TimeSpan? embeddingInterval = int.TryParse(Environment.GetEnvironmentVariable("BMB_EMBEDDING_INTERVAL_SECONDS"), out var eis) && eis >= 1
    ? TimeSpan.FromSeconds(eis) : null;
int? embeddingBatchSize = int.TryParse(Environment.GetEnvironmentVariable("BMB_EMBEDDING_BATCH_SIZE"), out var ebs) && ebs >= 1
    ? ebs : null;
builder.Services.AddEmbeddingProcessor(interval: embeddingInterval, batchSize: embeddingBatchSize);

TimeSpan? indexInterval = int.TryParse(Environment.GetEnvironmentVariable("BMB_INDEX_INTERVAL_SECONDS"), out var iis) && iis >= 1
    ? TimeSpan.FromSeconds(iis) : null;
int? indexBatchSize = int.TryParse(Environment.GetEnvironmentVariable("BMB_INDEX_BATCH_SIZE"), out var ibs) && ibs >= 1
    ? ibs : null;
builder.Services.AddIndexProcessor(interval: indexInterval, batchSize: indexBatchSize);

// ── mDNS announce: advertise this node on the LAN (_beememorybank._tcp.local) ──
// Runs in the API because that is where the authoritative InvisibleModeService (registered by
// AddCore) and the node identity (INodeIdentityRepository) live — the announcer checks both on its
// refresh cycle and withdraws its announcement when invisible mode is on.
// BMB_MDNS_PORT / BMB_MDNS_HTTPS let the deployment supply the reachable port/HTTPS flag; the HTTPS
// flag's real wiring (Ярус-1 local CA) is a later task.
builder.Services.AddMdnsAnnouncer(o =>
{
    if (int.TryParse(Environment.GetEnvironmentVariable("BMB_MDNS_PORT"), out var port) && port > 0)
        o.Port = port;
    if (bool.TryParse(Environment.GetEnvironmentVariable("BMB_MDNS_HTTPS"), out var https))
        o.Https = https;
});
builder.Services.AddHttpClient();
builder.Services.AddTransient<HttpClient>(sp =>
    sp.GetRequiredService<IHttpClientFactory>().CreateClient());
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
// Singleton: RestoreInitiatorService holds in-memory progress state for /restore/progress polling.
// Task.Run flows in EventApplier and SnapshotEndpoints fire-and-forget, so the service must outlive
// the request scope. Scoped dependencies (repositories) are resolved via IServiceScopeFactory per
// operation to avoid capturing a single scope at construction time.
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<RestoreInitiatorService>(sp, dataPath));
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<DekRotationService>(sp, dataPath));
builder.Services.AddSingleton<IDekRotationApplier>(sp => sp.GetRequiredService<DekRotationService>());
// Singleton: UpdateService holds in-memory state-machine state for /node/update/status polling.
// All collaborators (Snapshot/Maintenance/DekRotation/SnapshotRestore/Session services) are
// singletons resolved from the container; dataPath is the explicit ActivatorUtilities arg.
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<UpdateService>(sp, dataPath));
// LazySlotRewrapService is registered by AddSync() in Sync DI now (so CLI/mobile get it too).
builder.Services.AddSingleton<BeeMemoryBank.Sync.IRestoreInitiator>(sp => sp.GetRequiredService<RestoreInitiatorService>());
// Core-side retry contract: SessionService.UnlockCoreAsync resolves IRestoreRetrier to sweep
// stuck restore events on every unlock (mirrors the DEK-rotation retry pattern).
builder.Services.AddSingleton<IRestoreRetrier>(sp => sp.GetRequiredService<RestoreInitiatorService>());
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<McpResponseManager>(sp, dataPath));
builder.Services.AddSingleton<DownloadTokenService>();
builder.Services.AddSingleton<BeeMemoryBank.Api.Services.ProtectedUnlockCache>();
// OsAutoUnlockService is Windows-only; registered as a conditional singleton so other code can
// resolve it as OsAutoUnlockService? (nullable) and safely get null on non-Windows platforms.
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton(sp =>
        new BeeMemoryBank.Core.Services.OsAutoUnlockService(
            sp.GetRequiredService<BeeMemoryBank.Core.Interfaces.IKeySlotRepository>(),
            sp.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>(),
            dataPath));
}
builder.Services.AddHostedService<DownloadCleanupHostedService>();
builder.Services.AddHostedService<AuditLogPruningHostedService>();
builder.Services.AddHostedService<BeeMemoryBank.Api.Services.RemoteAccountSyncScheduler>();
builder.Services.AddScoped<ZipExportService>();
builder.Services.AddScoped<CompactionService>();
builder.Services.AddSingleton<SnapshotJoinCache>();
var mediaDir = Path.Combine(dataPath, "media");
Directory.CreateDirectory(mediaDir);
builder.Services.AddSingleton(new BeeMemoryBank.Core.Services.MediaStorageOptions(mediaDir));

// ── AI chat (Phase 0) ──────────────────────────────────────────────────────────
// chat.db is a SEPARATE SQLite DB from beememorybank.db, owned entirely by the Api. Its
// ChatDbConnectionFactory is a distinct DI type (does NOT implement Core's IDbConnectionFactory)
// so it can never collide with BeeMemoryBank.Storage.DbConnectionFactory. NOT registered via
// AddStorage; schema created by ChatDbInitializer (not MigrationRunner / Storage/Migrations).
// See docs/ai-chat-implementation-plan.md §1 ("Chat DB").
builder.Services.AddSingleton(new ChatDbConnectionFactory(dataPath));
builder.Services.AddScoped<ChatDbInitializer>();
builder.Services.AddScoped<ChatConversationRepository>();
builder.Services.AddScoped<ChatMessageRepository>();
builder.Services.AddScoped<ChatSettingsRepository>();
// Phase 5: chat_attachment CRUD (vision uploads + generated images) — chat.db only, never synced.
builder.Services.AddScoped<ChatAttachmentRepository>();
// M3 fix: OpenRouterClient's egress is documented as "pinned to https://openrouter.ai ... prevents
// an SSRF-style redirect of vault content to an attacker host" (see OpenRouterClient.cs), but that
// was only true of the URL, not the HttpClient — the plain AddScoped<OpenRouterClient>() this
// replaced resolved the DEFAULT HttpClient (registered above via AddHttpClient() +
// AddTransient<HttpClient>()), whose handler has AllowAutoRedirect=true. A 307/308 from
// openrouter.ai would silently re-POST the entire conversation (decrypted article bodies
// included) to wherever the redirect pointed, with only the Authorization header stripped
// cross-origin — the payload travels regardless. AddHttpClient<T>() gives OpenRouterClient its
// OWN typed client instead of sharing the default one, so this handler config can't leak onto
// (or be overridden by) any other HttpClient consumer. Mirrors ImageFetchClient's SSRF hardening
// in ChatEndpoints.Stream.cs, which got this right from the start.
builder.Services.AddHttpClient<OpenRouterClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
// Phase 3: per-conversation destructive-op cap (in-memory singleton — see ChatDestructiveOpCounter).
builder.Services.AddSingleton<ChatDestructiveOpCounter>();
// Phase 1: curated read-only tool surface for the native AI chat. Scoped (depends on the
// ambient CallerScope + SessionService, both request-scoped). See ChatToolDispatcher.
builder.Services.AddScoped<ChatToolDispatcher>();
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

builder.Services.AddSingleton(new BeeMemoryBank.Api.Helpers.McpToolRegistry(new[]
{
    typeof(BeeSearchTools),
    typeof(BeeReadTools),
    typeof(BeeWriteTools),
    typeof(BeeSessionTools),
    typeof(BeeUploadTools),
    typeof(BeeAuditTools),
    typeof(BeeConceptTools)
}));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    // Serialize enums as strings ("Idle", "Downloading", ...) instead of numeric. The Web proxy
    // deserializes RestoreProgressDto.CurrentStep as a string — a numeric default would 500 the
    // login page during any restore. Applies to all endpoints that return enum-typed properties.
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

var app = builder.Build();

app.UseLoopbackForwardedHeaders();

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

// Rate limiting for sensitive endpoints (unlock, join) — brute-force protection
app.UseMiddleware<BeeMemoryBank.Api.Middleware.RateLimitMiddleware>();

// Maintenance mode — blocks all requests except snapshot restore and session unlock
app.UseMiddleware<BeeMemoryBank.Api.Middleware.MaintenanceMiddleware>();

// Agent bearer auth (non-blocking, auto-unlock)
app.UseMiddleware<BeeMemoryBank.Api.Middleware.AgentAuthMiddleware>();

// Ambient caller scope — resolves folder ACL once per request, repos filter reads automatically
app.UseMiddleware<BeeMemoryBank.Api.Middleware.CallerScopeMiddleware>();

// Validate MCP tool/call parameter names. The SDK silently drops unknown args,
// which sends weak models into guess-the-flag loops. We short-circuit with a
// schema-bearing error before the SDK sees the request.
app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/mcp"),
    branch =>
    {
        // Session/identity guard runs first — "can this call even proceed" is a more
        // fundamental gate than "are the argument names spelled right."
        branch.UseMiddleware<BeeMemoryBank.Api.Middleware.McpSessionGuardMiddleware>();
        branch.UseMiddleware<BeeMemoryBank.Api.Middleware.McpParameterValidationMiddleware>();
    });

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
    // `version` is the compiled-in build version (source of truth for update checks).
    // `deployedAt`/`build` are kept for backward compatibility (older mobile/Maestro readers).
    return Results.Ok(new { version = BeeMemoryBank.Api.Helpers.AppVersion.Current, deployedAt, build = "2026-04-18" });
}).WithTags("Health").AllowAnonymous();

app.MapSessionEndpoints();
app.MapArticleEndpoints();
app.MapTreeEndpoints();
app.MapConceptTagEndpoints();
app.MapFolderEndpoints();
app.MapCopyEndpoints();
app.MapRemoteAuthEndpoints();
app.MapRemoteAccountEndpoints();
app.MapSearchEndpoints();
app.MapKeyEndpoints();
app.MapWhitelistEndpoints();
app.MapAgentEndpoints();
app.MapFavoriteEndpoints();
app.MapBrandingEndpoints();
app.MapUserEndpoints();
app.MapJoinEndpoints();
app.MapInitEndpoints();
app.MapSyncEndpoints();
app.MapSnapshotEndpoints();
    app.MapDekRotationEndpoints();
    app.MapUpdateEndpoints();
app.MapActivityEndpoints();
app.MapCommentEndpoints();
app.MapRestrictionEndpoints();
app.MapRoleEndpoints();
app.MapDownloadEndpoints();
    app.MapMediaEndpoints();
    app.MapVersionEndpoints();
    app.MapObsidianImportEndpoints();
    app.MapBeeImportEndpoints();
    app.MapHardDeleteEndpoints();
    app.MapCompactionEndpoints();
    app.MapAdminEndpoints();
    app.MapSearchMetricsEndpoints();
    app.MapInternetAccessEndpoints();
    app.MapAutoUnlockEndpoints();
    app.MapChatEndpoints();
    app.MapMcp("/mcp");

app.Run();

// Required for WebApplicationFactory in tests
public partial class Program { }
