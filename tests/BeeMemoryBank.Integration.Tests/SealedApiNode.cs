using System.Net.Http.Json;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The key source the connection factories of a test host see: it counts every request for a page key. A factory asks for the key before it
/// opens anything, so "no request" is "no vault or chat connection was made" (docs/design/step-b-spec.md 15.3.2).
/// </summary>
internal sealed class CountingKeySource(IVaultKeySource inner) : IVaultKeySource
{
    private int _requests;

    public int Requests => Volatile.Read(ref _requests);

    public VaultStoreMode Mode => inner.Mode;
    public bool IsOpen => inner.IsOpen;
    public long OpenGeneration => inner.OpenGeneration;

    public byte[] GetPageKey(string purpose)
    {
        Interlocked.Increment(ref _requests);
        return inner.GetPageKey(purpose);
    }
}

/// <summary>The real Api over a data folder a test prepared, with the counting key source in front of the gate.</summary>
public sealed class SealedApiFactory(string dataPath, Action<IServiceCollection>? configure = null) : BmbWebApplicationFactory(dataPath)
{
    internal CountingKeySource? Counting { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IVaultKeySource>(sp => Counting = new CountingKeySource(sp.GetRequiredService<VaultGate>())));
            configure?.Invoke(services);
        });
    }
}

/// <summary>What <see cref="SealedApiDirBuilder.BuildAsync"/> made: the folder, and the keys of the node it holds (the tests need them to prove what opens what).</summary>
internal sealed record SealedApiDir(string Dir, byte[] MasterDek, byte[] DatabaseKey, Guid NodeId);

/// <summary>
/// A real sealed data folder for the Api (docs/design/step-b-spec.md 2.5): the canary node of <see cref="SealedNodeTemplate"/>, its vault and chat
/// databases converted by the page codec exactly as the migration does it, and a boot store written by <see cref="UnlockMaterialMirror"/>
/// with the node's doors. The Api that starts over it is the product's own <c>Program</c>.
/// </summary>
internal static class SealedApiDirBuilder
{
    public static async Task<SealedApiDir> BuildAsync(SealedNodeTemplate template, string? dir = null)
    {
        dir ??= Path.Combine(Path.GetTempPath(), "bmb_sealed_api_" + Guid.NewGuid().ToString("N"));
        SealedNode.CopyTree(template.PlainDir, dir, skipDatabases: false);
        var vaultPath = Path.Combine(dir, VaultStoreFiles.Database);
        var chatPath = Path.Combine(dir, VaultStoreFiles.Chat);
        var bootPath = Path.Combine(dir, VaultStoreFiles.Boot);

        // The master DEK of the plain copy, from the password: the boot store wraps the database key under it.
        byte[] masterDek;
        using (var plain = new DbConnectionFactory(vaultPath))
        {
            var session = new SessionService(new KeySlotRepository(plain));
            (await session.UnlockAsync(SealedNodeTemplate.Password)).Should().BeTrue("the template's password opens its key slots");
            masterDek = session.GetMasterDek();
            session.Lock();
        }
        SqliteConnection.ClearAllPools();

        var boot = new BootStore(bootPath);
        var doors = new BootDoorStore(boot.Connections);
        var wrappers = new BootDbKeyStore(boot.Connections);
        var keyService = new DatabaseKeyService(boot, wrappers);
        var databaseKey = keyService.Create();
        var vaultKey = keyService.DerivePageKey(databaseKey, DbPurposes.Vault);
        var chatKey = keyService.DerivePageKey(databaseKey, DbPurposes.Chat);
        try
        {
            await SealFileAsync(vaultPath, vaultKey);
            if (File.Exists(chatPath)) await SealFileAsync(chatPath, chatKey);

            var keys = new InMemoryVaultKeySource(vaultKey, chatKey);
            using var sealedVault = new DbConnectionFactory(vaultPath, keys);
            var mirror = new UnlockMaterialMirror(sealedVault, boot, doors, wrappers, new BootStoreWriter(boot.Connections));
            await mirror.WriteNewBootStoreAsync(bootPath, sealedVault, databaseKey, 1, [(byte[])masterDek.Clone()]);
            var nodeId = Guid.Parse((await boot.GetMetaAsync(BootMetaKeys.NodeId))!);
            SqliteConnection.ClearAllPools();
            return new SealedApiDir(dir, masterDek, databaseKey, nodeId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(vaultKey);
            CryptographicOperations.ZeroMemory(chatKey);
        }
    }

    private static async Task SealFileAsync(string path, byte[] pageKey)
    {
        var sealedPath = path + ".sealed-tmp";
        await using (var image = File.OpenRead(path))
            await PlainImageImporter.ToSealedFileAsync(image, image.Length, sealedPath, pageKey, memoryCap: 256L << 20);
        foreach (var sidecar in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(path + sidecar)) File.Delete(path + sidecar);
        File.Delete(path);
        File.Move(sealedPath, path);
    }
}

/// <summary>A started Api over a sealed folder: the factory (it removes the folder when disposed) and the helpers every sealed-gate test uses.</summary>
internal sealed class SealedApiNode : IDisposable
{
    private readonly Action<IServiceCollection>? _configure;

    /// <summary>A GET that must answer with a JSON document; says what the node answered when it did not.</summary>
    public static async Task<System.Text.Json.JsonElement> GetJsonAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"{url} answered {(int)response.StatusCode}: {text}");
        text.Should().NotBeNullOrWhiteSpace($"{url} answered {(int)response.StatusCode} with an empty body; headers: {response.Headers}{response.Content.Headers}");
        return System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
    }

    public SealedApiDir Files { get; }
    public SealedApiFactory Factory { get; private set; }
    public string Dir => Files.Dir;

    private SealedApiNode(SealedApiDir files, Action<IServiceCollection>? configure)
    {
        Files = files;
        _configure = configure;
        Factory = new SealedApiFactory(files.Dir, configure) { KeepData = true };
    }

    public static async Task<SealedApiNode> StartAsync(SealedNodeTemplate template, Action<IServiceCollection>? configure = null)
    {
        var files = await SealedApiDirBuilder.BuildAsync(template);
        return new SealedApiNode(files, configure);
    }

    /// <summary>A plain node over a copy of a data folder (no keys: the node is not sealed yet).</summary>
    public static SealedApiNode PlainOver(string dir, Action<IServiceCollection>? configure = null) => new(new SealedApiDir(dir, [], [], Guid.Empty), configure);

    /// <summary>A node over a folder somebody already prepared.</summary>
    public static SealedApiNode Over(SealedApiDir files, Action<IServiceCollection>? configure = null) => new(files, configure);

    public IServiceProvider Services => Factory.Services;
    public IVaultGate Gate => Factory.Services.GetRequiredService<IVaultGate>();
    public SessionService Session => Factory.Services.GetRequiredService<SessionService>();
    public HttpClient Client() => Factory.CreateClient();

    public Task<HttpResponseMessage> LoginAsync(HttpClient client, string password, string username = "admin") =>
        client.PostAsJsonAsync("/api/session/login", new { username, password });

    /// <summary>Stops the host and starts a new one over the same files: what a restart of the process is.</summary>
    public void Restart()
    {
        Factory.Dispose();
        SqliteConnection.ClearAllPools();
        Factory = new SealedApiFactory(Files.Dir, _configure) { KeepData = true };
        _ = Factory.Services;   // the host is built lazily; a restart is over only when the start has run (the resolver, the gate)
    }

    public void Dispose()
    {
        Factory.KeepData = false;
        Factory.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
