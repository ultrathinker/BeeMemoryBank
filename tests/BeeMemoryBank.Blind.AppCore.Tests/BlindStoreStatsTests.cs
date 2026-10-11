using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// The "store" line of the blind apps' screens (docs/design/step-a-spec.md 10.2, 13.4), which replaced the note count: the restore point's age,
/// the changes held since and the bytes stored, counted from the copy's own database, never a number made up when it cannot be read; and the
/// activation's effect on a copy that is already loaded.
/// </summary>
public sealed class BlindStoreStatsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb-appcore-store-" + Guid.NewGuid().ToString("N"));

    public BlindStoreStatsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task AFreshCopy_HoldsNoBaselineAndNoDeltas_AndTheSummaryHasNoNoteCount()
    {
        using var services = Compose(Path.Combine(_dir, "beememorybank.db"));

        var summary = await services.GetRequiredService<BlindStoreStats>().GetAsync();

        summary.Should().NotBeNull();
        summary!.RestorePointAt.Should().BeNull();
        summary.ChangesSince.Should().Be(0);
        summary.StoredBytes.Should().BeGreaterThan(0, "an empty database still occupies pages");
        typeof(BlindStoreInfo).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Note", StringComparison.OrdinalIgnoreCase),
            "a blind copy shows no note count (owner decision 6)");
    }

    [Fact]
    public async Task EveryRowOfTheLog_IsADelta_AndNotesAreNotCounted()
    {
        using var services = Compose(Path.Combine(_dir, "beememorybank.db"));
        await services.GetRequiredService<BlindStartup>().EnsureReadyAsync();
        using (var conn = services.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        {
            for (var i = 0; i < 3; i++)
                await conn.ExecuteAsync(
                    @"INSERT INTO tbl_event (event_id, node_id, lamport_ts, event_type, payload, signature, protocol_version, created_at)
                      VALUES (@id, @node, @lamport, 'sealed', '{}', x'0102', 4, '2026-10-09T00:00:00Z')",
                    new { id = Guid.NewGuid().ToString(), node = Guid.NewGuid().ToString(), lamport = i + 1 });
            await conn.ExecuteAsync(
                "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@id, 'x', '/a', 'A', '2026-10-07T00:00:00Z', '2026-10-07T00:00:00Z')",
                new { id = Guid.NewGuid().ToString() });
        }

        var summary = await services.GetRequiredService<BlindStoreStats>().GetAsync();

        summary!.ChangesSince.Should().Be(3);
    }

    [Fact]
    public async Task ADatabaseThatCannotBeOpened_IsUnknown_NotZero()
    {
        var notADatabase = Path.Combine(_dir, "damaged.db");
        await File.WriteAllBytesAsync(notADatabase, Enumerable.Repeat((byte)0x5A, 8192).ToArray());
        using var services = Compose(notADatabase);

        (await services.GetRequiredService<BlindStoreStats>().GetAsync()).Should().BeNull();
    }

    [Fact]
    public async Task TheCallersCancellation_IsNotSwallowed()
    {
        using var services = Compose(Path.Combine(_dir, "beememorybank.db"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => services.GetRequiredService<BlindStoreStats>().GetAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static BlindStoreInfo Summary(DateTime? created, long deltas = 12, long stored = 4 * 1024 * 1024) => new(created, deltas, stored);

    [Fact]
    public void TheStoreLine_SaysNoneBeforeTheFirstLoad_UnknownForNull_AndTellsTheThreeFactsOtherwise()
    {
        BlindHomeView.StoreText(Summary(Now.UtcDateTime), Status(initialLoadDone: false), Now).Should().Contain("none yet").And.Contain("first load");
        BlindHomeView.StoreText(null, Status(initialLoadDone: true), Now).Should().Contain("unknown").And.NotContain("0");
        BlindHomeView.StoreText(Summary(Now.UtcDateTime - TimeSpan.FromDays(3)), Status(initialLoadDone: true), Now)
            .Should().Be("Restore point: made 3 d ago. Changes since: 12. Stored: 4 MiB.");
        BlindHomeView.StoreText(Summary(Now.UtcDateTime - TimeSpan.FromMinutes(5), deltas: 0, stored: 300), Status(initialLoadDone: true), Now)
            .Should().Be("Restore point: made 5 min ago. Changes since: 0. Stored: 1 KiB.");
        BlindHomeView.StoreText(Summary(null), Status(initialLoadDone: true), Now).Should().Contain("none yet");
    }

    [Fact]
    public void TheStoreLine_NeverMentionsNotes()
    {
        foreach (var load in new[] { false, true })
            foreach (var summary in new[] { null, Summary(Now.UtcDateTime), Summary(null) })
                BlindHomeView.StoreText(summary, Status(load), Now).Should().NotContainEquivalentOf("note");
        BlindHomeView.Build(Status(true), null, backupRunning: false, Summary(Now.UtcDateTime), Now).Store.Should().StartWith("Restore point:");
    }

    // ── the activation ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheActivation_SendsALoadedCopyBackToItsFirstLoad_OnceAndWithoutLoadingAnythingBySelf()
    {
        using var services = Compose(Path.Combine(_dir, "beememorybank.db"));
        var state = services.GetRequiredService<BlindPhoneState>();
        state.InitialLoadDone = true;
        var observers = services.GetServices<IMetadataActivationObserver>().ToList();
        observers.Should().ContainSingle(o => o is BlindPhoneActivationObserver);
        var activation = new MetadataActivation(Guid.NewGuid(), "k1", 1, DateTime.UtcNow, Guid.NewGuid());

        foreach (var observer in observers) await observer.OnActivatedAsync(activation);
        foreach (var observer in observers) await observer.OnActivatedAsync(activation);

        state.InitialLoadDone.Should().BeFalse("the next heavy pass downloads a format-2 replica");
    }

    [Fact]
    public async Task TheActivation_OnACopyThatNeverLoaded_ChangesNothing()
    {
        using var services = Compose(Path.Combine(_dir, "beememorybank.db"));
        var state = services.GetRequiredService<BlindPhoneState>();
        var log = services.GetRequiredService<BlindPhoneLog>();
        var before = log.Latest(100).Count;

        foreach (var observer in services.GetServices<IMetadataActivationObserver>())
            await observer.OnActivatedAsync(new MetadataActivation(Guid.NewGuid(), "k1", 1, DateTime.UtcNow, Guid.NewGuid()));

        state.InitialLoadDone.Should().BeFalse();
        log.Latest(100).Count.Should().Be(before, "nothing was loaded, so nothing is said about loading again");
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
