using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// WebApplicationFactory for integration tests.
/// Each instance uses an isolated temporary directory.
/// </summary>
public class BmbWebApplicationFactory : WebApplicationFactory<Program>
{
    private const string TestInternalKey = "test-internal-key-for-integration";

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "bmb_integration_" + Guid.NewGuid().ToString("N"));

    public string DataPath => _tempDir;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("BeeMemoryBank:DataPath", _tempDir);
        Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", TestInternalKey);
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
        if (disposing && Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>Initializes the node and returns the password.</summary>
    public async Task InitializeNodeAsync(string displayName = "TestNode", string password = "testPassword")
    {
        using var scope = Services.CreateScope();
        var initService = scope.ServiceProvider.GetRequiredService<InitializationService>();
        await initService.InitializeAsync("admin", displayName, password);
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
            apiAddress = (string?)null
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
            // Mirror prod /api/init/join: trust-on-join → Superadmin in local whitelist.
            // Without this, EventApplier blocks legitimate cross-node sync of whitelist/
            // hard-delete/restore events. (Wave 2 mitigation for gemini #1/#2/#3.)
            IsSuperadmin = true
        });
    }
}
