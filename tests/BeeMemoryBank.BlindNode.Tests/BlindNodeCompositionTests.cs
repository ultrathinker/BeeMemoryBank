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
    private static readonly Assembly BlindLib = typeof(BeeMemoryBank.Blind.BlindComposition).Assembly;

    /// <summary>A type by full name in the host or the blind library - the two assemblies a blind node is made of.</summary>
    private static Type? FindType(string fullName) =>
        Host.GetType(fullName, throwOnError: false) ?? BlindLib.GetType(fullName, throwOnError: false);

    /// <summary>Every assembly reachable from the host through references (names only; nothing is loaded).</summary>
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
                    try { queue.Enqueue(Assembly.Load(reference)); }
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
        closure.Should().Contain(["BeeMemoryBank.Blind", "BeeMemoryBank.AppPaths"], "the host's own libraries are reached directly");
        closure.Should().Contain(["Dapper", "Microsoft.Data.Sqlite", "BouncyCastle.Cryptography"],
            "and third-party code one hop down (the Blind assembly's packages) is reached too");
        Host.GetType("BeeMemoryBank.Api.Services.SnapshotService", throwOnError: false).Should().NotBeNull(
            "linked Api sources are inside the host assembly, which is what the type checks rely on");
        BlindLib.GetType("BeeMemoryBank.Sync.EventApplier", throwOnError: false).Should().NotBeNull(
            "linked library sources are inside the Blind assembly, under their original names");
    }

    [Theory]
    // The originals the Blind assembly is linked from. A host that references one of them next to Blind would carry
    // every type twice and could reach code the Blind assembly deliberately left out.
    [InlineData("BeeMemoryBank.Core")]
    [InlineData("BeeMemoryBank.Storage")]
    [InlineData("BeeMemoryBank.Sync")]
    [InlineData("BeeMemoryBank.Crypto")]
    [InlineData("BeeMemoryBank.Search")]
    public void TheHostReachesTheLibrariesOnlyThroughTheBlindAssembly(string original)
    {
        ReferenceClosure().Should().NotContain(original);
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
    public void TheBlindCodeDoesNotContain(string fullTypeName, string why)
    {
        FindType(fullTypeName).Should().BeNull(why);
    }

    [Fact]
    public void NoTypeOfTheBlindCodeIsInAnMcpOrChatNamespace()
    {
        var offenders = Host.GetTypes().Concat(BlindLib.GetTypes())
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
