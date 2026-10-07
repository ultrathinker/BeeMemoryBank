using System.Text.Encodings.Web;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Embeddings;
using BeeMemoryBank.Hosting.AspNetCore;
using BeeMemoryBank.Infrastructure;
using BeeMemoryBank.Infrastructure.Secrets;
using BeeMemoryBank.Media;
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
// BMB_ROLE=blind (plan 3.4): a node that stores and relays the mesh's ciphertext but never holds
// the master DEK. Read from configuration (environment included) so a test host can set it per
// instance. Everything below that needs the DEK, a model or a human is left out in that role.
var role = new EnvironmentNodeRole(builder.Configuration["BMB_ROLE"]);
builder.Services.AddSingleton<INodeRole>(role);

builder.Services.AddStorage(dataPath);
builder.Services.AddCore();
builder.Services.AddMemoryCache();
// Order matters: AddOnnxEmbeddings must run BEFORE AddSync (AddSync calls AddEmbeddingServices
// internally, which registers HybridSearchService / EmbeddingProjectionService that depend on
// IEmbeddingGenerator being present). AddImageTranscoder must run before any scope resolves
// MediaService.
if (role.IsBlind)
    builder.Services.AddSingleton<IEmbeddingGenerator, BeeMemoryBank.Sync.Blind.BlindEmbeddingGenerator>();
else
    builder.Services.AddOnnxEmbeddings(dataPath);
builder.Services.AddImageTranscoder();
builder.Services.AddSync();
builder.Services.AddRecoveryApi();
builder.Services.AddSingleton<SyncTokenStore>();
// Per-node, not per-process: see SyncChallengeRateLimiter.
builder.Services.AddSingleton<BeeMemoryBank.Api.Endpoints.SyncEndpoints.SyncChallengeRateLimiter>();
// The "forgot your password" endpoint's attempt buckets and pending announcement. A blind node has no
// users, no session endpoints and nobody to reset, so it does not carry it.
if (!role.IsBlind)
    builder.Services.AddSingleton<BeeMemoryBank.Api.Services.RecoveryAccessState>();
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
if (!role.IsBlind)
    builder.Services.AddPendingEmbeddingProcessor(interval: embeddingInterval, batchSize: embeddingBatchSize);

TimeSpan? indexInterval = int.TryParse(Environment.GetEnvironmentVariable("BMB_INDEX_INTERVAL_SECONDS"), out var iis) && iis >= 1
    ? TimeSpan.FromSeconds(iis) : null;
int? indexBatchSize = int.TryParse(Environment.GetEnvironmentVariable("BMB_INDEX_BATCH_SIZE"), out var ibs) && ibs >= 1
    ? ibs : null;
if (!role.IsBlind)
    builder.Services.AddIndexProcessor(interval: indexInterval, batchSize: indexBatchSize);

// ── mDNS announce: advertise this node on the LAN (_beememorybank._tcp.local) ──
// Runs in the API because that is where the authoritative InvisibleModeService (registered by
// AddCore) and the node identity (INodeIdentityRepository) live — the announcer checks both on its
// refresh cycle and withdraws its announcement when invisible mode is on.
// BMB_MDNS_PORT / BMB_MDNS_HTTPS let the deployment supply the reachable port/HTTPS flag; the HTTPS
// flag's real wiring (a local CA) is a later task.
// BMB_MDNS_ENABLED=false turns the announcer off. bmbd sets it when nothing it serves is reachable
// from the network (the default desktop install listens on loopback only): announcing a port no
// peer can connect to is useless, and the multicast socket is what makes Windows Firewall ask the
// user to allow BeeMemoryBank.Api on public and private networks right after installation.
// A blind node is paired by code and dialled at the address in it; it never advertises itself.
if (!role.IsBlind && !string.Equals(builder.Configuration["BMB_MDNS_ENABLED"], "false", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddMdnsAnnouncer(o =>
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("BMB_MDNS_PORT"), out var port) && port > 0)
            o.Port = port;
        if (bool.TryParse(Environment.GetEnvironmentVariable("BMB_MDNS_HTTPS"), out var https))
            o.Https = https;
        // The desktop node (bmbd) hands the decision to the profile's "Devices on my network" setting: the announcer is
        // registered, but announces (and opens its multicast socket) only while the setting, or BMB_HTTPS_ENABLED=1, is on, and
        // withdraws when it goes off. The file is the one the node itself reads and writes, in the data folder.
        if (string.Equals(builder.Configuration["BMB_MDNS_FOLLOWS_NETWORK_SETTING"], "1", StringComparison.Ordinal))
        {
            // Both: the setting (or BMB_HTTPS_ENABLED=1) says it is wanted, the node's listener state says it really listens. A port
            // that could not be bound (another program has it) must not be announced, and the record is withdrawn when the listener stops.
            var networkSettings = new BeeMemoryBank.Infrastructure.Network.NodeNetworkSettingsStore(dataPath);
            var listenerState = new BeeMemoryBank.Infrastructure.Network.LanListenerState(dataPath);
            o.AnnounceGate = () => networkSettings.Current().Enabled && listenerState.IsListening();
            o.RefreshInterval = TimeSpan.FromSeconds(10);
        }
    });
}
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
BlindRoleServices.AddSnapshotService(builder.Services, dataPath, sp =>
    // GetRequiredService, not GetService: a SnapshotService without a session has no way to
    // encrypt, and CreateAsync then writes the vault out in the clear. That must not be
    // reachable by silently resolving null at the composition root.
    new SessionSnapshotKeyOperations(sp.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>(),
        // Only in the blind role: the key a blind node signs its packages with.
        sp.GetService<IExternalNodeKey>()));
// Singleton: RestoreInitiatorService holds in-memory progress state for /restore/progress polling.
// Task.Run flows in EventApplier and SnapshotEndpoints fire-and-forget, so the service must outlive
// the request scope. Scoped dependencies (repositories) are resolved via IServiceScopeFactory per
// operation to avoid capturing a single scope at construction time.
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<RestoreInitiatorService>(sp, dataPath));
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<DekRotationService>(sp, dataPath));
if (!role.IsBlind)
    builder.Services.AddSingleton<IDekRotationApplier>(sp => sp.GetRequiredService<DekRotationService>());
// Singleton: UpdateService holds in-memory state-machine state for /node/update/status polling.
// All collaborators (Snapshot/Maintenance/DekRotation/SnapshotRestore/Session services) are
// singletons resolved from the container; dataPath is the explicit ActivatorUtilities arg.
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<UpdateService>(sp, dataPath));
// LazySlotRewrapService is registered by AddSync() in Sync DI now (so CLI/mobile get it too).
if (!role.IsBlind)
{
    builder.Services.AddSingleton<BeeMemoryBank.Sync.IRestoreInitiator>(sp => sp.GetRequiredService<RestoreInitiatorService>());
    // Core-side retry contract: SessionService.UnlockCoreAsync resolves IRestoreRetrier to sweep
    // stuck restore events on every unlock (mirrors the DEK-rotation retry pattern).
    builder.Services.AddSingleton<IRestoreRetrier>(sp => sp.GetRequiredService<RestoreInitiatorService>());
}
builder.Services.AddSingleton(sp => ActivatorUtilities.CreateInstance<McpResponseManager>(sp, dataPath));
builder.Services.AddSingleton<DownloadTokenService>();
builder.Services.AddSingleton<BeeMemoryBank.Api.Services.ProtectedUnlockCache>();
// OsAutoUnlockService needs an OS-backed secret store (Windows DPAPI, macOS Keychain); registered as
// a conditional singleton so other code can resolve it as OsAutoUnlockService? (nullable) and safely
// get null on a platform without one (Linux/Docker).
// Both put the master DEK back into the session without anyone typing a password — which is why
// a blind node (plan 3.4) must not have them at all.
var platformSecretStore = UserSecretStores.CreateDefault(dataPath);
if (platformSecretStore.IsSupported && !role.IsBlind)
{
    builder.Services.AddSingleton<IUserSecretStore>(_ => platformSecretStore);
    builder.Services.AddSingleton(sp =>
        new BeeMemoryBank.Infrastructure.OsAutoUnlock.OsAutoUnlockService(
            sp.GetRequiredService<BeeMemoryBank.Core.Interfaces.IKeySlotRepository>(),
            sp.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>(),
            dataPath,
            sp.GetRequiredService<IUserSecretStore>()));
    // Keeps the vault open across the restart of a desktop app update (see the class).
    builder.Services.AddSingleton(sp =>
        new BeeMemoryBank.Infrastructure.OsAutoUnlock.UpdateUnlockHandoff(
            sp.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>(),
            dataPath,
            secretStore: sp.GetRequiredService<IUserSecretStore>()));
}
builder.Services.AddHostedService<DownloadCleanupHostedService>();
builder.Services.AddHostedService<AuditLogPruningHostedService>();
if (!role.IsBlind)
{
    builder.Services.AddHostedService<BeeMemoryBank.Api.Services.RemoteAccountSyncScheduler>();
    // Moves legacy chat rows onto the node chat key: plaintext from before chat.db was encrypted at
    // rest, and ciphertext sealed directly under the master DEK from before the chat key
    // existed. Needs an unlocked vault — it polls and no-ops when locked rather than hooking unlock,
    // matching PendingEmbeddingProcessor. On a node with nothing legacy left (and on every fresh node)
    // a tick is one empty partial-index lookup per table.
    builder.Services.AddHostedService<BeeMemoryBank.Api.Services.ChatHistoryBackfillProcessor>();
}
// Same migration, run to completion right before every DEK rotation (initiator and peer), so no
// chat row is left sealed under a DEK the node is about to retire. See IDekRotationHook.
builder.Services.AddScoped<BeeMemoryBank.Core.Services.IDekRotationHook, ChatDekRotationHook>();
builder.Services.AddScoped<ZipExportService>();
builder.Services.AddScoped<CompactionService>();
// Blind node (BMB-54): /api/blind/status base fields, backups, jobs/CPU modes, console login, wipe.
builder.Services.AddBlindNodeServices(dataPath);
// Node reset lives in Core so the API endpoint and `bmb init reset` share one definition of
// "wipe"; the Api contributes chat.db cleanup through the hook interface.
builder.Services.AddScoped(sp => ActivatorUtilities.CreateInstance<BeeMemoryBank.Core.Services.NodeResetService>(sp, dataPath));
builder.Services.AddScoped<BeeMemoryBank.Core.Services.INodeResetHook, ApiStateResetHook>();
builder.Services.AddSingleton<SnapshotJoinCache>();
BlindRoleServices.AddMediaStorage(builder.Services, dataPath);

// ── AI chat ─────────────────────────────────────────────────────────────────────
// chat.db is a SEPARATE SQLite DB from beememorybank.db, owned entirely by the Api. Its
// ChatDbConnectionFactory is a distinct DI type (does NOT implement Core's IDbConnectionFactory)
// so it can never collide with BeeMemoryBank.Storage.DbConnectionFactory. NOT registered via
// AddStorage; schema created by ChatDbInitializer (not MigrationRunner / Storage/Migrations).
// See docs/ai-chat-implementation-plan.md §1 ("Chat DB").
builder.Services.AddSingleton(new ChatDbConnectionFactory(dataPath));
// The node chat key every chat.db ciphertext is sealed under (itself wrapped under the master DEK
// in tbl_node_data_key, which DEK rotation carries forward). Singleton: it caches the unwrapped key while
// the vault is unlocked and wipes it on SessionService.Locked.
builder.Services.AddSingleton<ChatDataProtector>();
builder.Services.AddScoped<ChatDbInitializer>();
builder.Services.AddScoped<ChatConversationRepository>();
builder.Services.AddScoped<ChatMessageRepository>();
builder.Services.AddScoped<ChatSettingsRepository>();
// chat_attachment CRUD (vision uploads + generated images) — chat.db only, never synced.
builder.Services.AddScoped<ChatAttachmentRepository>();
// OpenRouterClient's egress is documented as "pinned to https://openrouter.ai ... prevents
// an SSRF-style redirect of vault content to an attacker host" (see OpenRouterClient.cs) —
// the URL pin is not enough by itself: the HttpClient must also refuse redirects, because
// a 307/308 from openrouter.ai would silently re-POST the entire conversation (decrypted
// article bodies included) to wherever the redirect pointed, with only the Authorization
// header stripped cross-origin — the payload travels regardless. AddHttpClient<T>() gives
// OpenRouterClient its OWN typed client instead of sharing the default one, so this handler
// config can't leak onto (or be overridden by) any other HttpClient consumer. Mirrors
// ImageFetchClient's SSRF hardening in ChatEndpoints.Stream.cs.
builder.Services.AddHttpClient<OpenRouterClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
// Per-conversation destructive-op cap (in-memory singleton — see ChatDestructiveOpCounter).
builder.Services.AddSingleton<ChatDestructiveOpCounter>();
// Curated read-only tool surface for the native AI chat. Scoped (depends on the
// ambient CallerScope + SessionService, both request-scoped). See ChatToolDispatcher.
builder.Services.AddScoped<ChatToolDispatcher>();
BlindRoleServices.AddLargeBodyLimits(builder.Services);
builder.Services.AddOpenApi();

// The blind package (CONTRACTS §2): a full node builds it to seed, reseed and hand out replicas; a
// blind node builds it for an Android blind node.
BlindRoleServices.AddBlindPackageServices(builder.Services);
if (role.IsBlind)
{
    BlindRoleServices.AddBlindRoleServices(builder.Services, dataPath);
    BlindRoleServices.UseBlindHttps(builder);
}
else
{
    // The PC's side of blind nodes (plan 4.2, 5.2): pairing, pre-flight, reseed — and reseeding a
    // blind peer from the sync scheduler when it needs it.
    builder.Services.AddScoped<BlindPreflight>();
    // GET /api/blind/replica asks this when it builds a package: is this node a superadmin in the network?
    builder.Services.AddScoped<IReplicaProducerAuthority, BlindPreflightReplicaAuthority>();
    // The local flags table exists on every node; here it only holds the note of an adopted blind checkpoint (the
    // Blind nodes page, SyncClient.SyncWithPeerAsync). A blind node registers it with its own services.
    builder.Services.AddSingleton<BeeMemoryBank.Sync.Blind.BlindState>();
    builder.Services.AddSingleton<BlindNodeManager>();
    builder.Services.AddSingleton<BeeMemoryBank.Sync.Blind.IBlindPeerReseeder>(sp => sp.GetRequiredService<BlindNodeManager>());
    // Pairing an Android blind node, which calls one of the nodes above (plan section 10).
    builder.Services.AddSingleton<BeeMemoryBank.Api.Services.BlindPhone.BlindPhonePairingService>();
    // "Let blind copies call this node" (ADR 0007): the check of the address and the certificate before the row is saved.
    // No extra trust roots in production; a test registers its own after this (the same abstraction the blind apps use).
    builder.Services.AddSingleton(BeeMemoryBank.Crypto.TlsTrustAnchors.None);
    builder.Services.AddSingleton<HubTrustProbe>();
    AddMcp(builder.Services);
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    // Serialize enums as strings ("Idle", "Downloading", ...) instead of numeric. The Web proxy
    // deserializes RestoreProgressDto.CurrentStep as a string — a numeric default would 500 the
    // login page during any restore. Applies to all endpoints that return enum-typed properties.
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

    }

    private static void AddMcp(IServiceCollection services)
    {
services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<BeeSearchTools>()
    .WithTools<BeeReadTools>()
    .WithTools<BeeWriteTools>()
    .WithTools<BeeSessionTools>()
    .WithTools<BeeUploadTools>()
    .WithTools<BeeAuditTools>()
    .WithTools<BeeConceptTools>();

services.AddSingleton(new BeeMemoryBank.Api.Helpers.McpToolRegistry(new[]
{
    typeof(BeeSearchTools),
    typeof(BeeReadTools),
    typeof(BeeWriteTools),
    typeof(BeeSessionTools),
    typeof(BeeUploadTools),
    typeof(BeeAuditTools),
    typeof(BeeConceptTools)
}));
    }
}
