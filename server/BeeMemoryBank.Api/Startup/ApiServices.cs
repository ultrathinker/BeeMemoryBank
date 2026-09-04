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
/// Everything registered in the container, lifted out of Program.cs verbatim.
///
/// <para>Program.cs had grown to ~600 lines mixing four unrelated concerns — container setup,
/// one-shot startup work, the middleware chain and endpoint mapping — in one flat script where
/// the only thing separating them was a blank line. Ordering matters in all four, and differently
/// in each: registration order rarely matters, startup-task order almost always does, middleware
/// order is the pipeline. Splitting them makes each file a list of one kind of decision.</para>
///
/// <para>This is a move, not a rewrite: the statements are in their original order.</para>
/// </summary>
public static class ApiServices
{
    public static void AddBeeApiServices(this WebApplicationBuilder builder, string dataPath)
    {
builder.Services.AddStorage(dataPath);
builder.Services.AddCore();
builder.Services.AddMemoryCache();
builder.Services.AddOnnxEmbeddings(dataPath);
builder.Services.AddSync();
builder.Services.AddSingleton<SyncTokenStore>();
// Per-node, not per-process: see SyncChallengeRateLimiter.
builder.Services.AddSingleton<BeeMemoryBank.Api.Endpoints.SyncEndpoints.SyncChallengeRateLimiter>();
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

// Named client for outbound requests to a caller-supplied URL, where the ONLY thing standing
// between us and an internal address is a check on the host we were given. The default handler
// follows redirects, so a host that passes that check can 302 the request onto loopback or a
// metadata endpoint and the check is bypassed. Used by /api/sync/probe-relay; mirrors the same
// hardening on OpenRouterClient (below) and ChatEndpoints' ImageFetchClient.
builder.Services.AddHttpClient(BeeMemoryBank.Api.Endpoints.SyncEndpoints.NoRedirectClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
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
        // GetRequiredService, not GetService: a SnapshotService without a session has no way to
        // encrypt, and CreateAsync then writes the vault out in the clear. That must not be
        // reachable by silently resolving null at the composition root.
        sp.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>()));
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
// Encrypts chat rows written before chat.db content/attachments/tool-calls were encrypted at rest
// (finding H3a). Needs the master DEK, so it can only run while the vault is unlocked — it polls
// and no-ops when locked rather than hooking unlock, matching PendingEmbeddingProcessor. On a node
// with nothing legacy left (and on every fresh node) a tick is one empty partial-index lookup.
builder.Services.AddHostedService<BeeMemoryBank.Api.Services.ChatHistoryBackfillProcessor>();
builder.Services.AddScoped<ZipExportService>();
builder.Services.AddScoped<CompactionService>();
// Node reset lives in Core so the API endpoint and `bmb init reset` share one definition of
// "wipe"; the Api contributes chat.db cleanup through the hook interface.
builder.Services.AddScoped(sp => ActivatorUtilities.CreateInstance<BeeMemoryBank.Core.Services.NodeResetService>(sp, dataPath));
builder.Services.AddScoped<BeeMemoryBank.Core.Services.INodeResetHook, ApiStateResetHook>();
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

    }
}
