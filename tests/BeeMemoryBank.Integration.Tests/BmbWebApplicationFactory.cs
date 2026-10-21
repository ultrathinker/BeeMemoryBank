using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// WebApplicationFactory for integration tests.
/// Each instance uses an isolated temporary directory.
/// </summary>
public class BmbWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string TestInternalKey = "test-internal-key-for-integration";

    /// <summary>
    /// Exposed for tests that build their own client (e.g. an agent bearer client) and still need to
    /// clear the internal-key gate, so what they assert is the endpoint's own rule rather than the
    /// missing header.
    /// </summary>
    public const string InternalKeyForTests = TestInternalKey;

    /// <summary>A request with this header reaches its handler with <c>RequestAborted</c> already cancelled: the client has gone away.</summary>
    public const string ClientGoneHeader = "X-Test-Client-Gone";

    /// <summary>A request that carries this header comes, as far as the app can tell, from the address it names (the harness presents every other request as loopback).</summary>
    public const string RemoteIpHeader = "X-Test-Remote-Ip";

    private readonly string _tempDir;

    /// <summary>A node over a fresh, empty data folder.</summary>
    public BmbWebApplicationFactory() : this(Path.Combine(Path.GetTempPath(), "bmb_integration_" + Guid.NewGuid().ToString("N"))) { }

    /// <summary>A node over a data folder the test has prepared (a sealed node's files, an interrupted migration); the factory owns it and removes it on dispose.</summary>
    public BmbWebApplicationFactory(string dataPath) => _tempDir = dataPath;

    private HttpMessageHandler? _outboundHandler;

    public string DataPath => _tempDir;

    /// <summary>True: disposing the host leaves the data folder (a test that restarts a node over the same files). The last owner clears it.</summary>
    public bool KeepData { get; set; }

    /// <summary>
    /// Changes every test host makes to its container after the app's own registrations (installed once by the project that references the
    /// types involved; null where nothing is installed). It exists so that a test node on a Mac never reaches the user's own login Keychain.
    /// </summary>
    public static Action<IServiceCollection>? HostSafety { get; set; }

    /// <summary>
    /// Routes ALL of this node's outbound <see cref="IHttpClientFactory"/> clients — the default
    /// unnamed client (used by e.g. InitEndpoints' /api/init/join) AND any named one (e.g.
    /// RestoreInitiatorService's "SyncClient") — through <paramref name="handler"/> instead of a
    /// real SocketsHttpHandler. Lets a test steer production code that calls
    /// httpClientFactory.CreateClient(...) into another in-process TestServer/WebApplicationFactory
    /// without opening real sockets. A common choice is another <see cref="BmbWebApplicationFactory"/>'s
    /// <c>Server.CreateHandler()</c>.
    /// <para>
    /// Must be called BEFORE the host is first built — i.e. before <c>Server</c>,
    /// <c>CreateClient()</c>, or <c>Services</c> is touched — since <see cref="ConfigureWebHost"/>
    /// only runs once, at that point.
    /// </para>
    /// </summary>
    public void RouteOutboundHttpThrough(HttpMessageHandler handler) => _outboundHandler = handler;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("BeeMemoryBank:DataPath", _tempDir);
        Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", TestInternalKey);

        // TestServer synthesizes requests in-process, so Connection.RemoteIpAddress is null unless
        // a test sets it. Real deployments never look like that: the API's only local caller is the
        // Web layer over loopback, which RateLimitMiddleware deliberately exempts (browser traffic
        // is throttled a hop earlier, per real client IP, by the Web layer's own limiter). Present
        // loopback so the harness exercises that same shape; a null address is treated as an
        // unidentifiable remote peer and throttled, which is correct in production but would just
        // make whole test classes exhaust the login budget between each other.
        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter, LoopbackRemoteIpStartupFilter>());

        // This file is also compiled into projects that must not reference Infrastructure (the blind node tests), so it names no Infrastructure
        // type: the Integration project installs the hook (MacHostSafety).
        if (HostSafety is { } safety) builder.ConfigureTestServices(services => safety(services));

        if (_outboundHandler is { } handler)
        {
            // ConfigureTestServices, NOT ConfigureServices: the latter runs BEFORE the app's own
            // registrations in minimal hosting, so any client the app configures a primary handler
            // for (e.g. the redirect-disabled "no-redirect" client used by /api/sync/probe-relay)
            // would overwrite this routing afterwards and quietly open a real socket instead. Test
            // services run last and therefore win for every client, named or not.
            builder.ConfigureTestServices(services =>
                services.ConfigureAll<HttpClientFactoryOptions>(o =>
                    o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = handler)));
        }
    }

    /// <summary>Stamps loopback onto every request before the app pipeline runs. See ConfigureWebHost.</summary>
    private sealed class LoopbackRemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, nextMiddleware) =>
            {
                ctx.Connection.RemoteIpAddress ??= System.Net.IPAddress.Loopback;
                // A test that needs callers from different addresses (a limiter keyed on the source) says which one this request comes from.
                if (ctx.Request.Headers.TryGetValue(RemoteIpHeader, out var asked) && System.Net.IPAddress.TryParse(asked.ToString(), out var address))
                    ctx.Connection.RemoteIpAddress = address;
                // A request that says its client is gone (a test of a long write that must stop when the client disconnects): the handler runs with the abort token
                // already cancelled, which is what the server hands it when the connection drops. The transport of the test stays up, so the exception comes back whole.
                if (ctx.Request.Headers.ContainsKey(ClientGoneHeader)) ctx.RequestAborted = new CancellationToken(canceled: true);
                await nextMiddleware();
            });
            next(app);
        };
    }

    public new HttpClient CreateClient()
    {
        var client = base.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Key", TestInternalKey);
        client.DefaultRequestHeaders.Add("X-User-Role", "superadmin");
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && !KeepData && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

#if !BLIND_NODE_HOST // full-node setup and join: the blind node initializes itself (BlindNodeFactory); the BlindNode.Tests project links this file without them
    /// <summary>Initializes the node and returns the password.</summary>
    public async Task InitializeNodeAsync(string displayName = "TestNode", string password = "testPassword")
    {
        using var scope = Services.CreateScope();
        var initService = scope.ServiceProvider.GetRequiredService<InitializationService>();
        await initService.InitializeAsync("admin", displayName, password);
        // A fixture node is a plain node on purpose (the tests of the plain paths); the setup's seal intent would have the next start of this folder wipe it.
        scope.ServiceProvider.GetService<BeeMemoryBank.Vault.Sealed.ISealIntents>()?.Complete();
    }

    /// <summary>
    /// Sets the node up the way every node is set up since 2.6.2 (BMB-306): an encrypted database and metadata encryption on, as <c>POST /api/init/standalone</c>
    /// leaves it - and then unlocks it with the password. For the tests of restores, which only an encrypted node takes.
    /// </summary>
    public async Task InitializeEncryptedNodeAsync(string displayName = "TestNode", string password = "testPassword")
    {
        // What the endpoint does (InitializationService, then FreshNodeSealing), without the password policy of its request: the old tests of restores
        // use short passwords, and the endpoint itself is covered by FreshNodeSealedSetupTests.
        using (var scope = Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync("admin", displayName, password);
            await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Vault.Sealed.FreshNodeSealing>().SealAsync(password, activateMetadata: true);
        }
        if (!await Services.GetRequiredService<SessionService>().UnlockAsync(password))
            throw new InvalidOperationException("The password of the setup does not open the node it just made.");
    }

    /// <summary>
    /// Joins this node to an existing node's network via the /api/join endpoint,
    /// so that both nodes share the same Master DEK.
    /// </summary>
    public async Task JoinNodeAsync(HttpClient serverClient, string displayName, string masterPassword)
    {
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        // Generate local Ed25519 keys
        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        var nodeId = Guid.NewGuid();

        // Call /api/join on the server node
        var joinResp = await serverClient.PostAsJsonAsync("/api/join", new
        {
            masterPassword,
            nodeId,
            displayName,
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null,
            // A 2.6 joiner says so: a host that has switched metadata encryption on takes no other (and one that has not takes it as well).
            protocolVersion = BeeMemoryBank.Sync.Metadata.EventClasses.SealedProtocol
        }, opts);
        joinResp.EnsureSuccessStatusCode();

        var joinData = await joinResp.Content.ReadFromJsonAsync<JsonElement>(opts);
        var keySlot = joinData.GetProperty("keySlot");

        // Decrypt the Master DEK using the password
        var encDek = Convert.FromBase64String(keySlot.GetProperty("encryptedMasterDekB64").GetString()!);
        var iv = Convert.FromBase64String(keySlot.GetProperty("ivB64").GetString()!);
        var salt = Convert.FromBase64String(keySlot.GetProperty("saltB64").GetString()!);
        var argonMem = keySlot.GetProperty("argonMemory").GetInt32();
        var argonIter = keySlot.GetProperty("argonIterations").GetInt32();
        var argonPar = keySlot.GetProperty("argonParallelism").GetInt32();

        var kek = KeyDerivation.DeriveKek(masterPassword, salt, argonMem, argonIter, argonPar);
        var masterDek = MasterKeyManager.UnwrapMasterDek(encDek, iv, kek);

        // Re-wrap with a local salt
        var localSalt = KeyDerivation.GenerateSalt();
        var localKek = KeyDerivation.DeriveKek(masterPassword, localSalt);
        var (localEncDek, localIv) = MasterKeyManager.WrapMasterDek(masterDek, localKek);

        var now = DateTime.UtcNow;

        using var scope = Services.CreateScope();
        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var keySlotRepo = scope.ServiceProvider.GetRequiredService<IKeySlotRepository>();
        var whitelistRepo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        // Save identity
        await nodeRepo.CreateAsync(new NodeIdentity
        {
            NodeId = nodeId,
            DisplayName = displayName,
            Ed25519PublicKey = publicKey,
            Ed25519PrivateKey = privateKey,
            CreatedAt = now
        });

        // Save key slot
        var slotId = await keySlotRepo.CreateAsync(new MasterKeyStore
        {
            SlotType = "user",
            EncryptedMasterDek = localEncDek,
            IV = localIv,
            Salt = localSalt,
            ArgonMemory = CryptoConstants.DefaultArgonMemory,
            ArgonIterations = CryptoConstants.DefaultArgonIterations,
            ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
            CreatedAt = now
        });

        // Phase A2: a "user" slot must be bound to a tbl_user row, otherwise the joined node
        // has no superadmin to log in as. Mirror what InitializationService does on standalone
        // setup: synthesize a default admin username from the node's display name.
        await userRepo.CreateAsync(new User
        {
            Username = displayName,
            DisplayName = displayName,
            Role = UserRoles.Superadmin,
            KeySlotId = slotId,
            CreatedAt = now
        });

        // Save sentinel
        var sentinel = MasterKeyManager.ComputeSentinel(masterDek);
        await nodeRepo.StoreSentinelAsync(sentinel);
        Array.Clear(masterDek);

        // Self is NEVER added to tbl_whitelist — self-identity lives in tbl_node_identity only.

        // Add server node to whitelist
        var remoteNode = joinData.GetProperty("remoteNode");
        await whitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = Guid.Parse(remoteNode.GetProperty("nodeId").GetString()!),
            DisplayName = remoteNode.GetProperty("displayName").GetString()!,
            Ed25519PublicKey = Convert.FromBase64String(remoteNode.GetProperty("ed25519PublicKeyB64").GetString()!),
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now,
            // Mirrors JoinCommand.cs / NodeSetupService.cs / InitEndpoints.cs's own join flow: the
            // bootstrap node proved the master password, so it is a superadmin (JoinAuthority).
            IsSuperadmin = true
        });
    }
#endif
}
