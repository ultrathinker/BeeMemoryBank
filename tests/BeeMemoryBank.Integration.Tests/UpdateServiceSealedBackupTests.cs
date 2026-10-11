using System.Security.Cryptography;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The pre-update backup of a locked SEALED node through the real <c>UpdateService.TakePreUpdateBackupAsync</c>
/// (docs/design/step-b-spec.md 8.5, red test 15.1.10): the quiesced set - the sealed database, the chat database and the boot store copied
/// as files while the gate is held closed and the relay's writes are paused - verified against the sources by length and SHA-256. The gate
/// and the relay are doubles that behave as their contracts say (A.1, A.4): an unlock during the hold is refused as busy, a push during
/// the pause is refused as busy and succeeds, once, after it.
/// </summary>
public sealed class UpdateServiceSealedBackupTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_updsealed_" + Guid.NewGuid().ToString("N"));
    private readonly string _canary = MetadataCanaries.Ascii("note");
    private readonly InMemoryVaultKeySource _keys = new();
    private ServiceProvider _services = null!;
    private FakeGate _gate = null!;
    private FakeRelay _relay = null!;
    private UpdateService _svc = null!;
    private SqliteConnection? _bootHolder; // keeps the boot store's WAL alive, as the relay's pooled connection does

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);

        // The sealed pair and the chat database, written through the keyed factories and closed cleanly.
        using (var vault = new DbConnectionFactory(_dir, _keys))
        {
            using (var c = vault.CreateConnection())
            {
                c.Execute("CREATE TABLE tbl_article (id INTEGER PRIMARY KEY, title TEXT)");
                c.Execute("INSERT INTO tbl_article (title) VALUES (@t)", new { t = _canary });
                c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            }
            await vault.CloseForSealAsync(TimeSpan.FromSeconds(10));
        }
        using (var chat = new ChatDbConnectionFactory(_dir, _keys))
        {
            using (var c = chat.CreateConnection())
            {
                c.Execute("CREATE TABLE chat_conversation (id INTEGER PRIMARY KEY, title TEXT)");
                c.Execute("INSERT INTO chat_conversation (title) VALUES (@t)", new { t = _canary });
                c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            }
            await chat.CloseForSealAsync(TimeSpan.FromSeconds(10));
        }
        SqliteConnection.ClearAllPools();

        // The boot store, with frames that are not checkpointed yet (the update's checkpoint is what completes its main file).
        var boot = Path.Combine(_dir, "beememorybank.boot.db");
        _bootHolder = new SqliteConnection($"Data Source={boot};Pooling=False");
        _bootHolder.Open();
        _bootHolder.Execute($"PRAGMA application_id = {VaultStoreFiles.BootApplicationId}");
        _bootHolder.Execute("PRAGMA journal_mode=WAL");
        _bootHolder.Execute("PRAGMA wal_autocheckpoint=0");
        _bootHolder.Execute("CREATE TABLE tbl_boot_meta (k TEXT PRIMARY KEY, v TEXT)");
        for (var i = 0; i < 30; i++) _bootHolder.Execute("INSERT INTO tbl_boot_meta VALUES (@k, @v)", new { k = "k" + i, v = new string('v', 200) });
        new FileInfo(boot + "-wal").Length.Should().BeGreaterThan(0, "the control: the boot store really has an uncheckpointed WAL");

        // The files a backup must never carry.
        File.WriteAllText(Path.Combine(_dir, "server-auto-unlock.key"), "KEY-MATERIAL-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(_dir, "server-auto-unlock.enabled"), "enabled");
        File.WriteAllBytes(Path.Combine(_dir, "os-auto-unlock.dat"), RandomNumberGenerator.GetBytes(64));

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddStorage(_dir);
        services.AddCore();
        services.AddHttpClient();
        services.AddSingleton(sp => new SnapshotService(_dir, sp.GetRequiredService<DbConnectionFactory>()));
        _services = services.BuildServiceProvider();

        _gate = new FakeGate { Sealed = true };
        _relay = new FakeRelay(_gate);
        _svc = NewService(_gate, _relay);
    }

    private UpdateService NewService(IVaultGate? gate, ISealedRelayLifecycle? relay) => new(
        [new byte[32], new byte[32]],
        _services.GetRequiredService<SnapshotService>(),
        _services.GetRequiredService<MaintenanceModeService>(),
        ActivatorUtilities.CreateInstance<DekRotationService>(_services, _dir),
        ActivatorUtilities.CreateInstance<RestoreInitiatorService>(_services, _dir),
        _services.GetRequiredService<SessionService>(),
        _dir,
        _services.GetRequiredService<ILogger<UpdateService>>(),
        new InMemoryArtifactSource(new Dictionary<string, byte[]>()),
        new AlwaysHealthyHealthCheck(),
        "simulate", gate, relay);

    public Task DisposeAsync()
    {
        _bootHolder?.Dispose();
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
        return Task.CompletedTask;
    }

    private static string Sha(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private static readonly string[] SetNames =
        ["beememorybank.db", "beememorybank.db-wal", "beememorybank.db-shm", "chat.db", "chat.db-wal", "chat.db-shm", "beememorybank.boot.db", "beememorybank.boot.db-wal"];

    // ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_quiesced_set_is_copied_under_the_hold_and_the_pause_and_every_copy_equals_its_source()
    {
        var statements = new List<string>();
        _svc.ColdBackupSqlRecorder = statements.Add;
        var seenDuringCopy = new List<string>();
        var pushDuringCopy = "";
        var unlockDuringCopy = "";
        _svc.AfterColdBackupCopy = async dir =>
        {
            // This is the middle of the copy: the gate is held closed, the relay is paused.
            seenDuringCopy.Add($"held={_gate.IsHeld} paused={_relay.Paused}");
            pushDuringCopy = await _relay.PushAsync("event-1");
            unlockDuringCopy = (await _gate.OpenAsync(new byte[32])).Refusal ?? "opened";
            await Task.CompletedTask;
        };

        var backup = await _svc.TakePreUpdateBackupAsync("2.6.0", CancellationToken.None);

        backup.Should().Be(Path.Combine(_dir, "updates", "pre-update-2.6.0"));
        seenDuringCopy.Should().Equal("held=True paused=True");
        pushDuringCopy.Should().Be("vault_sealed_busy", "a push that arrives during the copy is answered 503 vault_sealed_busy");
        unlockDuringCopy.Should().Be("busy", "an unlock attempt during the copy is answered 503 vault_busy (VaultOpenRefusals.Busy)");
        _gate.IsHeld.Should().BeFalse();
        _relay.Paused.Should().BeFalse();

        // After the release the pusher double retries and is stored, once; the unlock works.
        (await _relay.PushAsync("event-1")).Should().Be("stored");
        (await _relay.PushAsync("event-1")).Should().Be("already_present");
        _relay.Inbox.Should().Equal("event-1");
        (await _gate.OpenAsync(new byte[32])).Opened.Should().BeTrue();

        // every copied file equals its source for file (length and SHA-256, re-read here)
        var copied = Directory.GetFiles(backup).Select(Path.GetFileName).OrderBy(n => n).ToList();
        copied.Should().Contain(["beememorybank.db", "chat.db", "beememorybank.boot.db"]);
        copied.Should().OnlyContain(n => SetNames.Contains(n!), "nothing but the files of the set is in the backup");
        foreach (var name in copied)
        {
            new FileInfo(Path.Combine(backup, name!)).Length.Should().Be(new FileInfo(Path.Combine(_dir, name!)).Length, name);
            Sha(Path.Combine(backup, name!)).Should().Be(Sha(Path.Combine(_dir, name!)), name);
        }
        copied.Should().NotContain("beememorybank.boot.db-wal", "the boot store was checkpointed, so its copy is complete without a WAL");
        statements.Should().Equal("PRAGMA wal_checkpoint(TRUNCATE)");
    }

    [Fact]
    public async Task The_boot_copy_holds_everything_the_boot_store_had_though_its_wal_was_not_checkpointed_before()
    {
        var backup = await _svc.TakePreUpdateBackupAsync("2.6.0", CancellationToken.None);

        using var copy = new SqliteConnection($"Data Source={Path.Combine(backup, "beememorybank.boot.db")};Pooling=False;Mode=ReadOnly");
        copy.Open();
        copy.QuerySingle<int>("SELECT count(*) FROM tbl_boot_meta").Should().Be(30);
        copy.QuerySingle<int>("PRAGMA application_id").Should().Be(VaultStoreFiles.BootApplicationId);
    }

    [Fact]
    public async Task A_sealed_wal_is_copied_only_when_one_existed_and_the_key_files_are_never_copied()
    {
        var withoutWal = await _svc.TakePreUpdateBackupAsync("2.6.0", CancellationToken.None);
        Directory.GetFiles(withoutWal).Select(Path.GetFileName).Should().NotContain(["beememorybank.db-wal", "beememorybank.db-shm", "chat.db-wal"]);

        // a node that was not closed cleanly leaves a sealed WAL: it belongs to the set
        File.WriteAllBytes(Path.Combine(_dir, "beememorybank.db-wal"), RandomNumberGenerator.GetBytes(32 + 24 + 4096));
        var withWal = await _svc.TakePreUpdateBackupAsync("2.6.1", CancellationToken.None);
        Directory.GetFiles(withWal).Select(Path.GetFileName).Should().Contain("beememorybank.db-wal");
        Sha(Path.Combine(withWal, "beememorybank.db-wal")).Should().Be(Sha(Path.Combine(_dir, "beememorybank.db-wal")));

        foreach (var backup in new[] { withoutWal, withWal })
        {
            File.Exists(Path.Combine(backup, "server-auto-unlock.key")).Should().BeFalse();
            File.Exists(Path.Combine(backup, "server-auto-unlock.enabled")).Should().BeFalse();
            File.Exists(Path.Combine(backup, "os-auto-unlock.dat")).Should().BeFalse();
        }
    }

    [Fact]
    public async Task A_canary_scan_of_the_backup_is_clean_and_the_set_copied_back_into_an_empty_directory_starts_sealed_and_opens_with_the_key()
    {
        var backup = await _svc.TakePreUpdateBackupAsync("2.6.0", CancellationToken.None);

        // the control: the canary is real, and it is in the sealed database's rows
        using (var live = new DbConnectionFactory(_dir, _keys))
        using (var c = live.CreateConnection())
            c.QuerySingle<string>("SELECT title FROM tbl_article").Should().Be(_canary);
        SqliteConnection.ClearAllPools();
        MetadataCanaries.ScanDirectory(backup, [_canary]).Should().BeEmpty("the backup holds sealed files and a boot store only");

        var restored = Path.Combine(Path.GetTempPath(), "bmb_updsealed_restore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(restored);
        try
        {
            foreach (var file in Directory.GetFiles(backup)) File.Copy(file, Path.Combine(restored, Path.GetFileName(file)));

            VaultStoreFiles.Diagnose(restored).Mode.Should().Be(VaultStoreMode.Sealed);
            using var factory = new DbConnectionFactory(restored, _keys);
            using var conn = factory.CreateConnection();
            conn.QuerySingle<string>("PRAGMA integrity_check").Should().Be("ok");
            conn.QuerySingle<string>("SELECT title FROM tbl_article").Should().Be(_canary);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(restored, true); } catch { }
        }
    }

    [Fact]
    public async Task A_difference_between_copy_and_verification_ends_with_backup_inconsistent_no_copy_left_and_the_hold_lifted()
    {
        _svc.AfterColdBackupCopy = dir =>
        {
            // a copy that is not what it was copied from
            using var fs = new FileStream(Path.Combine(dir, "chat.db"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            fs.Position = 100;
            fs.WriteByte(0x5A);
            return Task.CompletedTask;
        };

        var act = async () => await _svc.TakePreUpdateBackupAsync("2.6.0", CancellationToken.None);

        (await act.Should().ThrowAsync<IOException>()).Which.Message.Should().StartWith("backup_inconsistent");
        Directory.Exists(Path.Combine(_dir, "updates", "pre-update-2.6.0")).Should().BeFalse("no half set is left behind");
        _gate.IsHeld.Should().BeFalse("the hold is lifted on the error path too");
        _relay.Paused.Should().BeFalse();
    }

    [Fact]
    public async Task A_copy_that_is_longer_than_its_source_is_inconsistent_too()
    {
        // ApplyAsync turns this exception into the Failed state ("Pre-update backup failed: backup_inconsistent: ...") and goes no further.
        _svc.AfterColdBackupCopy = dir => { File.AppendAllText(Path.Combine(dir, "beememorybank.boot.db"), "x"); return Task.CompletedTask; };

        var act = async () => await _svc.TakePreUpdateBackupAsync("2.6.0", CancellationToken.None);

        (await act.Should().ThrowAsync<IOException>()).Which.Message.Should().Contain("backup_inconsistent");
    }

    [Fact]
    public async Task A_node_unlocked_meanwhile_gets_a_snapshot_instead_of_a_file_copy()
    {
        // A plain, migrated data directory stands in for the unlocked node: the snapshot is the existing, unchanged path.
        var plainDir = Path.Combine(Path.GetTempPath(), "bmb_updsealed_plain_" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddStorage(plainDir);
        services.AddCore();
        services.AddHttpClient();
        services.AddSingleton(sp => new SnapshotService(plainDir, sp.GetRequiredService<DbConnectionFactory>()));
        using var provider = services.BuildServiceProvider();
        try
        {
            await provider.GetRequiredService<MigrationRunner>().RunMigrationsAsync();
            var gate = new FakeGate { Sealed = true, UnlockedMeanwhile = true };
            var svc = new UpdateService(
                [new byte[32], new byte[32]], provider.GetRequiredService<SnapshotService>(), provider.GetRequiredService<MaintenanceModeService>(),
                ActivatorUtilities.CreateInstance<DekRotationService>(provider, plainDir), ActivatorUtilities.CreateInstance<RestoreInitiatorService>(provider, plainDir),
                provider.GetRequiredService<SessionService>(), plainDir, provider.GetRequiredService<ILogger<UpdateService>>(),
                new InMemoryArtifactSource(new Dictionary<string, byte[]>()), new AlwaysHealthyHealthCheck(), "simulate", gate, new FakeRelay(gate));

            var backup = await svc.TakePreUpdateBackupAsync("2.6.0", CancellationToken.None);

            backup.Should().StartWith(Path.Combine(plainDir, "snapshots"), "the backup is a snapshot");
            File.Exists(backup).Should().BeTrue();
            Directory.Exists(Path.Combine(plainDir, "updates")).Should().BeFalse("no file copy was made");
            gate.HoldRequests.Should().Be(1);
        }
        finally
        {
            provider.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(plainDir, true); } catch { }
        }
    }

    [Fact]
    public async Task A_plain_locked_node_still_takes_the_plain_cold_copy_exactly_as_before()
    {
        var plainDir = Path.Combine(Path.GetTempPath(), "bmb_updsealed_plain2_" + Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddStorage(plainDir);
        services.AddCore();
        services.AddHttpClient();
        services.AddSingleton(sp => new SnapshotService(plainDir, sp.GetRequiredService<DbConnectionFactory>()));
        using var provider = services.BuildServiceProvider();
        try
        {
            await provider.GetRequiredService<MigrationRunner>().RunMigrationsAsync();
            foreach (var gate in new IVaultGate?[] { null, new FakeGate { Sealed = false } })
            {
                var svc = new UpdateService(
                    [new byte[32], new byte[32]], provider.GetRequiredService<SnapshotService>(), provider.GetRequiredService<MaintenanceModeService>(),
                    ActivatorUtilities.CreateInstance<DekRotationService>(provider, plainDir), ActivatorUtilities.CreateInstance<RestoreInitiatorService>(provider, plainDir),
                    provider.GetRequiredService<SessionService>(), plainDir, provider.GetRequiredService<ILogger<UpdateService>>(),
                    new InMemoryArtifactSource(new Dictionary<string, byte[]>()), new AlwaysHealthyHealthCheck(), "simulate", gate, null);

                var backup = await svc.TakePreUpdateBackupAsync("2.5.9", CancellationToken.None);

                backup.Should().Be(Path.Combine(plainDir, "updates", "pre-update-2.5.9", "beememorybank.db"));
                File.Exists(backup).Should().BeTrue();
            }
        }
        finally
        {
            provider.Dispose();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(plainDir, true); } catch { }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Doubles that behave as the contracts say (A.1, A.4).

    private sealed class FakeGate : IVaultGate
    {
        private int _held;
        public bool Sealed { get; init; }
        public bool UnlockedMeanwhile { get; init; }
        public int HoldRequests { get; private set; }

        public VaultState State => Sealed ? VaultState.Closed : VaultState.Plain;
        public bool IsSealed => Sealed;
        public bool IsUsable => !Sealed;
        public bool IsHeld => Volatile.Read(ref _held) == 1;

        public Task<VaultOpenResult> OpenAsync(byte[] masterDek, CancellationToken ct = default) =>
            Task.FromResult(IsHeld ? VaultOpenResult.Refused(VaultOpenRefusals.Busy) : VaultOpenResult.Done);

        public Task<IAsyncDisposable> HoldClosedAsync(string reason, CancellationToken ct = default)
        {
            HoldRequests++;
            if (UnlockedMeanwhile) throw new InvalidOperationException("The vault is not closed.");
            Volatile.Write(ref _held, 1);
            return Task.FromResult<IAsyncDisposable>(new Lease(() => Volatile.Write(ref _held, 0)));
        }

        public Task<IAsyncDisposable> OpenTemporarilyAsync(byte[] masterDek, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CompleteOpenAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task CloseAsync(string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task WaitUsableAsync(CancellationToken ct) => Task.CompletedTask;
        public event Action<VaultState>? StateChanged { add { } remove { } }
    }

    private sealed class FakeRelay(FakeGate gate) : ISealedRelayLifecycle
    {
        private int _paused;
        private readonly List<string> _inbox = [];
        public bool Paused => Volatile.Read(ref _paused) == 1;
        public IReadOnlyList<string> Inbox => _inbox;

        public Task<IAsyncDisposable> PauseWritesAsync(CancellationToken ct = default)
        {
            if (!gate.IsHeld) throw new InvalidOperationException("The gate is not Closed.");
            Volatile.Write(ref _paused, 1);
            return Task.FromResult<IAsyncDisposable>(new Lease(() => Volatile.Write(ref _paused, 0)));
        }

        /// <summary>A peer's push: refused as busy while the writes are paused, stored once otherwise.</summary>
        public Task<string> PushAsync(string eventId)
        {
            if (Paused) return Task.FromResult("vault_sealed_busy");
            if (_inbox.Contains(eventId)) return Task.FromResult("already_present");
            _inbox.Add(eventId);
            return Task.FromResult("stored");
        }

        public Task OnOpenedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<RelayImportResult> ImportAsync(CancellationToken ct = default) => Task.FromResult(new RelayImportResult(0, 0, 0, 0, 0));
        public Task OnClosingAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Lease(Action release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            release();
            return ValueTask.CompletedTask;
        }
    }
}
