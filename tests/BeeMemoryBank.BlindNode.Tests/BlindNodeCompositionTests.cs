using System.Reflection;
using BeeMemoryBank.Integration.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// What a blind node is made of. The host is a program of its own, so "a blind node cannot do X" is a statement about
/// which code is in the binary - and it is tested as that: the assemblies the host references, the types it contains,
/// and the container it builds. (The routes it serves are pinned by BlindRouteMatrixTests.)
/// </summary>
public class BlindNodeCompositionTests
{
    private static readonly Assembly Host = typeof(Program).Assembly;

    /// <summary>The application assemblies a blind node is made of: the host and every BeeMemoryBank.* assembly it reaches.</summary>
    private static readonly Lazy<List<Assembly>> AppAssemblies = new(() =>
    {
        ReferenceClosure();
        return [Host, .. LoadedApp.Values];
    });

    private static readonly Dictionary<string, Assembly> LoadedApp = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A type by full name in any application assembly of the blind node.</summary>
    private static Type? FindType(string fullName) =>
        AppAssemblies.Value.Select(a => a.GetType(fullName, throwOnError: false)).FirstOrDefault(x => x is not null);

    /// <summary>Every assembly reachable from the host through references (nothing outside the host's own closure is loaded).</summary>
    private static HashSet<string> ReferenceClosure()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Assembly>();
        queue.Enqueue(Host);
        while (queue.Count > 0)
        {
            foreach (var reference in queue.Dequeue().GetReferencedAssemblies())
            {
                if (reference.Name is null || !seen.Add(reference.Name)) continue;
                // Only follow our own assemblies and third-party ones the host ships; the framework is the runtime's.
                if (reference.Name.StartsWith("BeeMemoryBank.", StringComparison.Ordinal) ||
                    !(reference.Name.StartsWith("System", StringComparison.Ordinal) ||
                      reference.Name.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                      reference.Name is "netstandard" or "mscorlib" or "WindowsBase"))
                {
                    try
                    {
                        var loaded = Assembly.Load(reference);
                        if (reference.Name.StartsWith("BeeMemoryBank.", StringComparison.Ordinal)) LoadedApp[reference.Name] = loaded;
                        queue.Enqueue(loaded);
                    }
                    catch (IOException) { /* not shipped with the test host: nothing to follow */ }
                }
            }
        }
        return seen;
    }

    /// <summary>
    /// The guards below are only worth anything if the scan can see what IS there: a closure walk that silently
    /// stopped at the first hop would pass every "does not reference" check forever.
    /// </summary>
    [Fact]
    public void TheScansSeeWhatTheHostDoesContainAndReference()
    {
        var closure = ReferenceClosure();
        closure.Should().Contain(
            ["BeeMemoryBank.Core", "BeeMemoryBank.Storage", "BeeMemoryBank.Sync", "BeeMemoryBank.Crypto", "BeeMemoryBank.Search", "BeeMemoryBank.AppPaths"],
            "the host's own libraries are reached directly: the shared libraries");
        closure.Should().Contain(["Dapper", "Microsoft.Data.Sqlite", "BouncyCastle.Cryptography"],
            "and third-party code one hop down (the shared libraries' packages) is reached too");
        Host.GetType("BeeMemoryBank.Api.Services.SnapshotService", throwOnError: false).Should().NotBeNull(
            "linked Api sources are inside the host assembly, which is what the type checks rely on");
        FindType("BeeMemoryBank.Sync.EventApplier").Should().NotBeNull(
            "the shared libraries are among the assemblies the type checks scan");
        AppAssemblies.Value.Select(a => a.GetName().Name).Should().Contain(
            ["BeeMemoryBank.Core", "BeeMemoryBank.Storage", "BeeMemoryBank.Sync", "BeeMemoryBank.Crypto", "BeeMemoryBank.Search"]);
    }

    [Theory]
    // The full-node layer and the phone's client. The shared libraries cannot reference them; a host that did would carry the code that
    // holds the master key, opens content and manages the node.
    [InlineData("BeeMemoryBank.Vault")]
    [InlineData("BeeMemoryBank.Blind.PhoneClient")]
    public void TheHostDoesNotReachTheFullNodeLayer(string forbidden)
    {
        ReferenceClosure().Should().NotContain(forbidden);
    }

    [Theory]
    // the programs and libraries of a full node a blind node must not contain
    [InlineData("BeeMemoryBank.Api")]
    [InlineData("BeeMemoryBank.Web")]
    [InlineData("BeeMemoryBank.Cli")]
    [InlineData("BeeMemoryBank.Embeddings")]
    [InlineData("BeeMemoryBank.Media")]
    [InlineData("BeeMemoryBank.Infrastructure")]
    [InlineData("BeeMemoryBank.Rekey")]
    [InlineData("BeeMemoryBank.Node")]
    // third-party code behind them
    [InlineData("ModelContextProtocol")]
    [InlineData("ModelContextProtocol.AspNetCore")]
    [InlineData("Microsoft.ML.OnnxRuntime")]
    [InlineData("SixLabors.ImageSharp")]
    [InlineData("Velopack")]
    [InlineData("Makaretu.Dns.Multicast")]
    public void TheHostDoesNotReferenceAnythingOfAFullNode(string forbidden)
    {
        ReferenceClosure().Should().NotContain(forbidden,
            "a blind node holds ciphertext it cannot read; code that reads, embeds, transcodes, updates or talks MCP has no place in it");
    }

    [Theory]
    // These are types of BeeMemoryBank.Api. The host links source files of the Api, so a file linked by mistake would
    // put one of them into this assembly - and with it the thing a blind node must not be able to do.
    [InlineData("BeeMemoryBank.Api.Middleware.AgentAuthMiddleware", "agent auth unlocks a session with an agent's wrapped DEK")]
    [InlineData("BeeMemoryBank.Api.Middleware.CallerScopeMiddleware", "a blind node has no users or agents to resolve")]
    [InlineData("BeeMemoryBank.Api.Services.HttpContextCallerScopeStore", "no caller scope")]
    [InlineData("BeeMemoryBank.Api.Services.HttpActorProvider", "a blind node never authors an event")]
    [InlineData("BeeMemoryBank.Api.Services.RestoreInitiatorService", "the new device's side of a restore")]
    [InlineData("BeeMemoryBank.Api.Services.Recovery.RecoveryRestoreService", "the new device's side of a restore")]
    [InlineData("BeeMemoryBank.Api.Services.Recovery.BlindRestoreClient", "the new device's side of a restore")]
    [InlineData("BeeMemoryBank.Api.Services.DekRotationService", "a blind node has no DEK to rotate")]
    [InlineData("BeeMemoryBank.Api.Services.UpdateService", "no desktop update machinery")]
    [InlineData("BeeMemoryBank.Api.Services.OpenRouterClient", "no AI chat")]
    [InlineData("BeeMemoryBank.Api.Services.ChatDbInitializer", "no AI chat")]
    [InlineData("BeeMemoryBank.Api.Services.ZipExportService", "an export would be of ciphertext it cannot open")]
    [InlineData("BeeMemoryBank.Api.Services.CompactionService", "compaction needs the content key")]
    [InlineData("BeeMemoryBank.Api.Helpers.McpToolRegistry", "no MCP")]
    // And these are types of the libraries: management of articles, folders, keys, users and roles, search, import and
    // export, restoring a vault, the semantic index, the favourites/agents/versions data - what a store that cannot read
    // has no use for.
    [InlineData("BeeMemoryBank.Core.Services.ArticleService", "a blind node does not edit notes")]
    [InlineData("BeeMemoryBank.Core.Services.SearchService", "it has no index to search")]
    [InlineData("BeeMemoryBank.Core.Services.InitializationService", "it initializes itself from its key, never from a password")]
    [InlineData("BeeMemoryBank.Core.Services.KeyManagementService", "it manages no keys")]
    [InlineData("BeeMemoryBank.Core.Services.UserService", "it has no users")]
    [InlineData("BeeMemoryBank.Core.Services.RoleService", "it has no roles")]
    [InlineData("BeeMemoryBank.Core.Services.TreeService", "it shows no tree")]
    [InlineData("BeeMemoryBank.Core.Services.RestoreService", "it does not restore a vault")]
    [InlineData("BeeMemoryBank.Core.Services.ObsidianImportService", "no import")]
    [InlineData("BeeMemoryBank.Core.Services.BeeImportService", "no import")]
    [InlineData("BeeMemoryBank.Search.Indexing.IndexBuilder", "no search index")]
    [InlineData("BeeMemoryBank.Storage.Search.EncryptedSegmentStore", "no search index segments")]
    [InlineData("BeeMemoryBank.Storage.Sqlite.AgentRepository", "no agents")]
    [InlineData("BeeMemoryBank.Storage.Sqlite.FavoriteRepository", "no favourites")]
    [InlineData("BeeMemoryBank.Storage.Sqlite.ArticleVersionRepository", "no article versions to show")]
    [InlineData("BeeMemoryBank.Sync.PendingEmbeddingProcessor", "no model, so nothing to embed")]
    // The master-DEK and full-node machinery of the shared sync code, named exactly (Codex review B): not curated by feel.
    [InlineData("BeeMemoryBank.Sync.DekRotation.PeerDekRotationApplier", "a peer re-wraps retained master DEKs; the blind node has BlindDekRotationApplier")]
    [InlineData("BeeMemoryBank.Sync.DekRotation.DekRewrapper", "re-wrapping a master DEK")]
    [InlineData("BeeMemoryBank.Sync.DekRotation.DekRotationMaterial", "handling the rotation material of a master DEK")]
    [InlineData("BeeMemoryBank.Sync.Recovery.SealedSecretService", "sealing a secret for a peer is the full node phone pairing")]
    [InlineData("BeeMemoryBank.Sync.Recovery.RecoveryServiceCollectionExtensions", "the full-node AddRecovery registration; the blind composition registers its own")]
    [InlineData("BeeMemoryBank.Sync.Blind.BlindPhonePullClient", "the Android pull client lives in Blind.PhoneClient")]
    [InlineData("BeeMemoryBank.Sync.Blind.IBlindPhonePullClient", "the Android pull client lives in Blind.PhoneClient")]
    // The master-key, session and content-crypto code (the vault split, BMB-99): BeeMemoryBank.Vault, which this host does not reference.
    [InlineData("BeeMemoryBank.Core.Services.SessionService", "a blind node has no session")]
    [InlineData("BeeMemoryBank.Core.Services.CommentService", "it opens and writes no comment")]
    [InlineData("BeeMemoryBank.Core.Services.MediaService", "it opens and writes no media")]
    [InlineData("BeeMemoryBank.Core.Services.RemoteAccountService", "it has no remote accounts")]
    [InlineData("BeeMemoryBank.Core.Services.NodeDataKeyEnvelope", "it wraps no master key")]
    [InlineData("BeeMemoryBank.Crypto.MasterKeyManager", "it handles no master key")]
    [InlineData("BeeMemoryBank.Crypto.DekManager", "it handles no data key")]
    [InlineData("BeeMemoryBank.Crypto.ArticleEncryptor", "it encrypts no article")]
    [InlineData("BeeMemoryBank.Crypto.MediaEncryptor", "it encrypts no media")]
    [InlineData("BeeMemoryBank.Crypto.ProtectedContentCodec", "it opens no protected content")]
    [InlineData("BeeMemoryBank.Crypto.EnvelopeFraming", "the framing of content ciphertext it cannot open")]
    [InlineData("BeeMemoryBank.Crypto.RecoveryBoxCrypto", "it creates and opens no recovery box")]
    [InlineData("BeeMemoryBank.Crypto.NodeIdentityVault", "the identity key of a blind node is in a file, never under a master key")]
    [InlineData("BeeMemoryBank.Sync.EventLogger", "a blind node authors no event (BlindEventLogger refuses)")]
    [InlineData("BeeMemoryBank.Sync.SessionNodeAuthSigner", "it signs with its external key (ExternalKeyNodeAuthSigner)")]
    [InlineData("BeeMemoryBank.Sync.RemoteSentinelVerifier", "the master-key sentinel check needs the key")]
    [InlineData("BeeMemoryBank.Sync.LazySlotRewrapService", "it has no key slots to re-wrap")]
    [InlineData("BeeMemoryBank.Sync.Recovery.RecoveryEventPublisher", "it creates no recovery box")]
    [InlineData("BeeMemoryBank.Sync.Recovery.DeviceBoxPublisher", "it creates no recovery box")]
    [InlineData("BeeMemoryBank.Sync.Recovery.RecoveryReconciler", "it reconciles no recovery box")]
    [InlineData("BeeMemoryBank.Sync.Recovery.StateAnchorService", "it writes no state anchor")]
    [InlineData("BeeMemoryBank.Sync.Recovery.RecoveryKeyResolver", "it opens no recovery box")]
    [InlineData("BeeMemoryBank.Storage.Sqlite.RetiredMasterDekStore", "it keeps no retired master key")]
    [InlineData("BeeMemoryBank.Api.Services.SessionSnapshotKeyOperations", "full snapshot encryption with the session key")]
    [InlineData("BeeMemoryBank.Api.Services.Blind.BlindPreflight", "the PC-side pre-flight of a blind node")]
    [InlineData("BeeMemoryBank.Api.Services.Blind.BlindNodeManager", "the PC-side management of blind nodes")]
    public void TheBlindCodeDoesNotContain(string fullTypeName, string why)
    {
        FindType(fullTypeName).Should().BeNull(why);
    }

    [Fact]
    public void NoTypeOfTheBlindCodeIsInAnMcpOrChatNamespace()
    {
        var offenders = AppAssemblies.Value.SelectMany(a => a.GetTypes())
            .Where(t => t.Namespace is { } ns &&
                        (ns.Contains(".McpTools", StringComparison.Ordinal) || ns.Contains(".Chat", StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .ToList();
        offenders.Should().BeEmpty();
    }

    /// <summary>The container is built with validation: every service the host registers can be constructed.</summary>
    private sealed class ValidatingFactory : BlindNodeFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseDefaultServiceProvider(o => o.ValidateOnBuild = true);
        }
    }

    [Fact]
    public async Task EveryRegisteredServiceCanBeConstructed_AndEveryHostedServiceIsPresent()
    {
        using var blind = new ValidatingFactory();
        await blind.InitializeNodeAsync();

        // ValidateOnBuild already threw if a constructor parameter had no registration. Resolve the singletons and the
        // hosted services as well - the ones registered through factory lambdas are only proven by being created.
        var hosted = blind.Services.GetServices<IHostedService>().Select(h => h.GetType().Name).ToList();
        hosted.Should().Contain(["SyncScheduler", "CleanupService", "BlindLogTrimmer", "BlindBackupScheduleService"]);
        hosted.Should().NotContain(n => n.Contains("Embedding") || n.Contains("Index") || n.Contains("Mdns") ||
                                        n.Contains("Chat") || n.Contains("RemoteAccount"),
            "a blind node has no model, no search index, no LAN announcement, no chat and no remote accounts");

        using var scope = blind.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Api.Services.SnapshotService>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Api.Services.BlindSeedService>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Api.Services.BlindPairing>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Sync.IRestoreInitiator>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Interfaces.IDekRotationApplier>().Should().NotBeNull();
    }
}
