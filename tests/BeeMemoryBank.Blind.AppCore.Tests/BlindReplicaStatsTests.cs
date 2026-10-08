using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// The "notes in this copy" line of the blind apps' screens (the iOS app shows it): a row count of the copy's own database, never a number
/// made up when the database cannot be read.
/// </summary>
public sealed class BlindReplicaStatsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb-appcore-stats-" + Guid.NewGuid().ToString("N"));

    public BlindReplicaStatsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task CountsTheLiveNotes_OfTheRealComposition_AndNotTheDeletedOnes()
    {
        using var services = Compose(Path.Combine(_dir, "beememorybank.db"));
        var stats = services.GetRequiredService<BlindReplicaStats>();

        (await stats.CountNotesAsync()).Should().Be(0, "a fresh copy holds nothing");

        using (var conn = services.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        {
            const string insert = "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@id, 'x', '/a', @status, '2026-10-07T00:00:00Z', '2026-10-07T00:00:00Z')";
            await conn.ExecuteAsync(insert, new { id = Guid.NewGuid().ToString(), status = "A" });
            await conn.ExecuteAsync(insert, new { id = Guid.NewGuid().ToString(), status = "A" });
            await conn.ExecuteAsync(insert, new { id = Guid.NewGuid().ToString(), status = "D" });
        }

        (await stats.CountNotesAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ADatabaseThatCannotBeOpened_IsUnknown_NotZero()
    {
        // The database file is not a database: the start-up (migrations) fails, and the count says so with null.
        var notADatabase = Path.Combine(_dir, "damaged.db");
        await File.WriteAllBytesAsync(notADatabase, Enumerable.Repeat((byte)0x5A, 8192).ToArray());
        using var services = Compose(notADatabase);

        (await services.GetRequiredService<BlindReplicaStats>().CountNotesAsync()).Should().BeNull();
    }

    [Fact]
    public async Task TheCallersCancellation_IsNotSwallowed()
    {
        using var services = Compose(Path.Combine(_dir, "beememorybank.db"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => services.GetRequiredService<BlindReplicaStats>().CountNotesAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void TheNotesLine_SaysNoneBeforeTheFirstLoad_UnknownForNull_AndTheNumberOtherwise()
    {
        BlindHomeView.NotesText(5, Status(initialLoadDone: false)).Should().Contain("none yet").And.Contain("first load");
        BlindHomeView.NotesText(null, Status(initialLoadDone: true)).Should().Contain("unknown").And.NotContain("0");
        BlindHomeView.NotesText(0, Status(initialLoadDone: true)).Should().Be("Notes in this copy: 0.");
        BlindHomeView.NotesText(42, Status(initialLoadDone: true)).Should().Be("Notes in this copy: 42.");
    }

    private ServiceProvider Compose(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBlindStateStore, MemoryState>();
        services.AddSingleton<IBlindSecretStore, NoSecrets>();
        services.AddSingleton<IBlindLifecycle, NoLifecycle>();
        BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(_dir, dbPath));
        return services.BuildServiceProvider();
    }

    private static BlindAppStatus Status(bool initialLoadDone) =>
        new(Guid.NewGuid(), "Phone", IsPaired: true, initialLoadDone, null, null, BlindBackupSchedule.Off, false, null, null, [], []);

    private sealed class MemoryState : IBlindStateStore
    {
        private readonly Dictionary<string, string?> _values = [];
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, string? value) => _values[key] = value;
    }

    private sealed class NoSecrets : IBlindSecretStore
    {
        public void SaveIdentitySeed(byte[] seed) { }
        public byte[]? LoadIdentitySeed() => null;
        public byte[]? LoadBackupKey() => null;
        public byte[]? LoadPairingSecret() => null;
        public void SaveBackupKey(byte[] key) { }
        public void SavePairingSecret(byte[] secret) { }
        public void ClearPairingSecret() { }
        public void Clear() { }
    }

    private sealed class NoLifecycle : IBlindLifecycle
    {
        public void StopBackgroundWork() { }
        public void StopBackupService() { }
        public void RestartAfterWipe() { }
    }
}
