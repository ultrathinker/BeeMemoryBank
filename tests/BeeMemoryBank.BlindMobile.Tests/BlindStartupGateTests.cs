using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// The first start of the app on a real phone (stage 5): migrations ran in <c>App.OnStart</c> while the page's
/// <c>OnAppearing</c> already tried to create the identity, found no tables, and the swallowed error left the screen on
/// "No identity". Everything that touches the database now waits on <see cref="BlindStartup"/>.
/// </summary>
public sealed class BlindStartupGateTests
{
    [Fact]
    public async Task CallersWhileTheFirstRunIsStillGoing_ShareThatRun_AndNoneIsReadyBeforeItEnds()
    {
        var release = new TaskCompletionSource();
        var runs = 0;
        var startup = new BlindStartup(async _ => { Interlocked.Increment(ref runs); await release.Task; });

        var first = startup.EnsureReadyAsync();
        var second = startup.EnsureReadyAsync();

        await Task.Delay(50);
        first.IsCompleted.Should().BeFalse("the database is not open yet");
        second.IsCompleted.Should().BeFalse("a second caller must wait for the run in progress, not slip past it");

        release.SetResult();
        await Task.WhenAll(first, second);
        runs.Should().Be(1);
    }

    [Fact]
    public async Task AFinishedRun_IsNotRepeated()
    {
        var runs = 0;
        var startup = new BlindStartup(_ => { runs++; return Task.CompletedTask; });

        await startup.EnsureReadyAsync();
        await startup.EnsureReadyAsync();

        runs.Should().Be(1);
    }

    [Fact]
    public async Task AFailedRun_ReachesItsCaller_AndTheNextCallerTriesAgain()
    {
        var attempts = 0;
        var startup = new BlindStartup(_ =>
        {
            attempts++;
            return attempts == 1 ? Task.FromException(new IOException("disk full")) : Task.CompletedTask;
        });

        var first = async () => await startup.EnsureReadyAsync();
        await first.Should().ThrowAsync<IOException>();

        await startup.EnsureReadyAsync();
        attempts.Should().Be(2, "a failure must not leave the app stuck on a closed database until the process dies");
    }

    [Fact]
    public async Task TheAppsOwnWiring_CreatesTheTables_BeforeEnsureReadyReturns()
    {
        var dir = NewDir();
        try
        {
            await using var provider = Compose(dir);

            await provider.GetRequiredService<BlindStartup>().EnsureReadyAsync();

            (await TableExistsAsync(provider, "tbl_node_identity")).Should().BeTrue("the identity row is written there");
        }
        finally
        {
            ReleasePools();
        }
    }

    /// <summary>The failure the gate exists for, so a reader can see it is real: the identity cannot be made on a database nobody migrated.</summary>
    [Fact]
    public async Task WithoutTheGate_TheIdentityCannotBeCreatedOnAFreshDatabase()
    {
        var dir = NewDir();
        try
        {
            await using var provider = Compose(dir);

            var create = async () => await provider.GetRequiredService<BlindMobilePairing>().CreateIdentityAsync("Phone");
            await create.Should().ThrowAsync<Exception>("tbl_node_identity does not exist yet");

            await provider.GetRequiredService<BlindStartup>().EnsureReadyAsync();
            await provider.GetRequiredService<BlindMobilePairing>().CreateIdentityAsync("Phone");

            provider.GetRequiredService<BlindMobilePairing>().HasIdentity.Should().BeTrue();
        }
        finally
        {
            ReleasePools();
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb-s5-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ServiceProvider Compose(string dir)
    {
        var services = new ServiceCollection();
        BlindMobileServices.ConfigureServices(services, dir, Path.Combine(dir, "beememorybank.db"));
        var keys = new MemoryKeys();
        var preferences = new Dictionary<string, string>();
        services.AddSingleton<IBlindPhoneStore>(new PreferencesBlindStore(
            k => preferences.GetValueOrDefault(k), (k, v) => preferences[k] = v, k => preferences.Remove(k)));
        services.AddSingleton<IBlindNodeKeys>(keys);
        services.AddSingleton<IBlindPhoneKeys>(keys);
        services.AddSingleton<IDeviceStateProvider>(new FixedDevice());
        return services.BuildServiceProvider();
    }

    private static async Task<bool> TableExistsAsync(IServiceProvider provider, string table)
    {
        using var conn = provider.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table", new { table }) > 0;
    }

    private static void ReleasePools() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private sealed class MemoryKeys : IBlindNodeKeys
    {
        private byte[]? _seed, _backup, _secret;
        public void SaveIdentitySeed(byte[] seed) => _seed = (byte[])seed.Clone();
        public byte[]? LoadIdentitySeed() => (byte[]?)_seed?.Clone();
        public void SaveBackupKey(byte[] key) => _backup = (byte[])key.Clone();
        public byte[]? LoadBackupKey() => (byte[]?)_backup?.Clone();
        public void SavePairingSecret(byte[] secret) => _secret = (byte[])secret.Clone();
        public byte[]? LoadPairingSecret() => (byte[]?)_secret?.Clone();
        public void ClearPairingSecret() => _secret = null;
        public void Clear() => _seed = _backup = _secret = null;
    }

    private sealed class FixedDevice : IDeviceStateProvider
    {
        public BlindPhoneDeviceState Current() => new(true, true, true, 100);
    }
}
