using System.Reflection;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// "Disconnect and wipe" (plan section 10): afterwards the app is a fresh blind app. Everything the blind
/// copy keeps is gone — the identity row and the whole replica database with its rollback copies and
/// candidates, the media blobs, the backups, the replica work folder, the log, the schedule and the rest of
/// the state, and the Keystore secrets (identity seed, pairing secret, backup key) — and nothing else in the
/// app's data folder is touched. A step that cannot be done does not stop the others, and is reported.
/// </summary>
public sealed class BlindPhoneResetTests
{
    [Fact]
    public async Task Wipe_RemovesEverythingTheBlindCopyKeeps_AndNothingElse()
    {
        var t = await WipeRig.NewAsync();

        BlindPhoneReset.Wipe(t.Provider, t.Dir);

        t.Keys.IdentitySeed.Should().BeNull("the Keystore blob bmb_blind_seed_v1 is cleared");
        t.Keys.BackupKey.Should().BeNull();
        t.Keys.PairingSecret.Should().BeNull();
        t.Store.Values.Should().BeEmpty("the schedule, the pairing, the call code, the pending backup name: all state");
        foreach (var gone in t.Owned)
            (File.Exists(gone) || Directory.Exists(gone)).Should().BeFalse($"{Path.GetRelativePath(t.Dir, gone)} belongs to the blind copy");
        File.Exists(t.Foreign).Should().BeTrue("the app's data folder holds other things (Preferences, caches) that are not ours to remove");
        File.Exists(t.ForeignInDir).Should().BeTrue();
    }

    [Fact]
    public async Task AfterTheWipe_TheAppIsAFreshBlindApp_ANewIdentityCanBeMade()
    {
        var t = await WipeRig.NewAsync();
        BlindPhoneReset.Wipe(t.Provider, t.Dir);
        SqliteConnection.ClearAllPools();

        var fresh = new DbConnectionFactory(t.DbPath);
        await new MigrationRunner(fresh).RunMigrationsAsync();
        (await new NodeIdentityRepository(fresh).GetAsync()).Should().BeNull("the old identity row went with the database");
        var pairing = new BlindMobilePairing(t.State, t.Keys, new SqliteBlindIdentityRecorder(fresh), t.Log);

        await pairing.CreateIdentityAsync("New copy");

        pairing.HasIdentity.Should().BeTrue();
        t.State.CallCode.Should().BeNull("a wiped phone is not paired to anything");
        t.State.InitialLoadDone.Should().BeFalse();
    }

    [Fact]
    public async Task OneStepThatFails_DoesNotStopTheOthers_ItIsReportedAfterAll()
    {
        var t = await WipeRig.NewAsync();
        bool? firstDeleteSawKeysCleared = null;
        void Delete(string path)
        {
            firstDeleteSawKeysCleared ??= t.Keys.IdentitySeed is null && t.Store.Values.Count == 0;
            if (path == BlindPaths.Backups(t.Dir)) throw new IOException("simulated: a backup file is locked");
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else File.Delete(path);
        }

        var act = () => BlindPhoneReset.Wipe(t.Provider, t.Dir, Delete);

        var failure = act.Should().Throw<AggregateException>().Which;
        failure.InnerExceptions.Should().ContainSingle().Which.Message.Should().Contain("locked");
        failure.Message.Should().Contain("blind-backups", "the screen tells what is left");
        Directory.Exists(BlindPaths.Backups(t.Dir)).Should().BeTrue("that one could not be removed");
        var backups = BlindPaths.Backups(t.Dir);
        foreach (var gone in t.Owned.Where(p => p != backups && !p.StartsWith(backups + Path.DirectorySeparatorChar)))
            (File.Exists(gone) || Directory.Exists(gone)).Should().BeFalse(
                $"{Path.GetRelativePath(t.Dir, gone)} is removed although a step before it failed");
        t.Keys.IdentitySeed.Should().BeNull();
        firstDeleteSawKeysCleared.Should().BeTrue("the secrets go first: a wipe that dies half way leaves no key behind for what remains");
    }

    [Fact]
    public async Task LifecycleStopsBeforeKeysAndFiles_AndAStopFailureDoesNotSkipTheWipe()
    {
        var t = await WipeRig.NewAsync();
        var lifecycle = new RecordingLifecycle(t.Keys, () => File.Exists(BlindPaths.Log(t.Dir))) { ThrowOnStop = true };
        var services = new ServiceCollection();
        services.AddSingleton<IBlindPhoneKeys>(t.Keys);
        services.AddSingleton(t.State);
        services.AddSingleton<IBlindLifecycle>(lifecycle);
        var act = () => BlindPhoneReset.Wipe(services.BuildServiceProvider(), t.Dir);

        act.Should().Throw<AggregateException>().Which.Message.Should().Contain("stopping the background work");
        lifecycle.StoppedBeforeKeys.Should().BeTrue();
        lifecycle.StoppedBeforeFiles.Should().BeTrue();
        t.Keys.IdentitySeed.Should().BeNull();
        File.Exists(BlindPaths.Log(t.Dir)).Should().BeFalse();
    }

    [Fact]
    public async Task EveryValueTheStateCanHold_IsClearedByTheWipe()
    {
        var t = await WipeRig.NewAsync();
        foreach (var property in typeof(BlindPhoneState).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
            property.SetValue(t.State, SampleValue(property.PropertyType));
        t.Store.Values.Count.Should().BeGreaterThan(8, "every property wrote something");

        BlindPhoneReset.Wipe(t.Provider, t.Dir);

        t.Store.Values.Should().BeEmpty("a value the wipe forgets would survive into the next pairing");
    }

    private static object SampleValue(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(Guid)) return Guid.NewGuid();
        if (underlying == typeof(byte[])) return new byte[] { 1, 2, 3 };
        if (underlying == typeof(string)) return "x";
        if (underlying == typeof(bool)) return true;
        if (underlying == typeof(DateTimeOffset)) return DateTimeOffset.UtcNow;
        if (underlying == typeof(BlindBackupSchedule)) return BlindBackupSchedule.Daily;
        if (underlying == typeof(BlindCallCode))
            return BlindCallCode.Create("https://hub.test:5300", Guid.NewGuid(), "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", new byte[32], new byte[32]);
        throw new NotSupportedException($"Teach this test a sample for {type}, so the wipe is checked against it too.");
    }

    private sealed class WipeRig
    {
        public required string Dir { get; init; }
        public required string DbPath { get; init; }
        public required ServiceProvider Provider { get; init; }
        public required MemoryKeys Keys { get; init; }
        public required MemoryStore Store { get; init; }
        public required BlindPhoneState State { get; init; }
        public required BlindPhoneLog Log { get; init; }
        public required List<string> Owned { get; init; }
        public required string Foreign { get; init; }
        public required string ForeignInDir { get; init; }

        public static async Task<WipeRig> NewAsync()
        {
            var dir = Path.Combine(Path.GetTempPath(), "bmb-s4-wipe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var dbPath = Path.Combine(dir, "beememorybank.db");
            DapperConfig.Configure();
            var factory = new DbConnectionFactory(dbPath);
            await new MigrationRunner(factory).RunMigrationsAsync();
            await new SqliteBlindIdentityRecorder(factory).RecordAsync(BlindNodeId.NewId(), new byte[32], "Old copy", default);
            SqliteConnection.ClearAllPools();

            var owned = new List<string>();
            void Put(string relative)
            {
                var path = Path.Combine(dir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "x");
                owned.Add(path);
            }
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) Put("beememorybank.db" + suffix);
            Put("beememorybank.db.before-replica-0123456789abcdef");
            Put("beememorybank.replica-0123456789abcdef.db");
            Put("beememorybank.replica-0123456789abcdef.db-wal");
            Put(Path.Combine("media", "aaaa.enc"));
            Put(Path.Combine("blind-backups", "bmb-phone-20260101-000000.bmbbackup"));
            Put(Path.Combine("blind-backups", "bmb-phone-20260102-000000.bmbbackup.source"));
            Put(Path.Combine("blind-replica", "backup-package.part"));
            Put(Path.Combine("blind-replica", "replica.install-failed.json"));
            Put("blind-log.jsonl");
            owned.AddRange([dbPath, BlindPaths.Backups(dir), BlindPaths.Replica(dir), Path.Combine(dir, "media")]);
            var foreign = Path.Combine(dir, "some-other-app-file.xml");
            File.WriteAllText(foreign, "preferences");
            var foreignInDir = Path.Combine(dir, "other-folder", "keep.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(foreignInDir)!);
            File.WriteAllText(foreignInDir, "keep");

            var keys = new MemoryKeys();
            keys.SaveIdentitySeed(new byte[32]);
            keys.SaveBackupKey(new byte[32]);
            keys.SavePairingSecret(new byte[32]);
            var store = new MemoryStore();
            var state = new BlindPhoneState(store)
            {
                NodeId = Guid.NewGuid(), DisplayName = "Old copy", Schedule = BlindBackupSchedule.Daily,
                PendingBackupName = "bmb-phone-x.bmbbackup", InitialLoadDone = true,
            };
            var services = new ServiceCollection();
            services.AddSingleton<IBlindPhoneKeys>(keys);
            services.AddSingleton(state);
            services.AddSingleton<IBlindLifecycle, NoopLifecycle>();
            return new WipeRig
            {
                Dir = dir, DbPath = dbPath, Provider = services.BuildServiceProvider(), Keys = keys, Store = store, State = state,
                Log = new BlindPhoneLog(Path.Combine(dir, "blind-log.jsonl"), TimeProvider.System),
                Owned = owned.Distinct().ToList(), Foreign = foreign, ForeignInDir = foreignInDir,
            };
        }
    }

    private sealed class MemoryStore : IBlindPhoneStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Get(string key) => Values.GetValueOrDefault(key);
        public void Set(string key, string? value)
        {
            if (value is null) Values.Remove(key);
            else Values[key] = value;
        }
    }

    private sealed class NoopLifecycle : IBlindLifecycle
    {
        public void StopBackgroundWork() { }
        public void StopBackupService() { }
        public void RestartAfterWipe() { }
    }

    private sealed class RecordingLifecycle(MemoryKeys keys, Func<bool> filesExist) : IBlindLifecycle
    {
        public bool ThrowOnStop { get; init; }
        public bool StoppedBeforeKeys { get; private set; }
        public bool StoppedBeforeFiles { get; private set; }
        public void StopBackgroundWork()
        {
            StoppedBeforeKeys = keys.IdentitySeed is not null;
            StoppedBeforeFiles = filesExist();
            if (ThrowOnStop) throw new IOException("stop failed");
        }
        public void StopBackupService() { }
        public void RestartAfterWipe() { }
    }

    private sealed class MemoryKeys : IBlindNodeKeys
    {
        public byte[]? IdentitySeed { get; private set; }
        public byte[]? BackupKey { get; private set; }
        public byte[]? PairingSecret { get; private set; }
        public void SaveIdentitySeed(byte[] seed) => IdentitySeed = (byte[])seed.Clone();
        public byte[]? LoadIdentitySeed() => (byte[]?)IdentitySeed?.Clone();
        public void SaveBackupKey(byte[] key) => BackupKey = (byte[])key.Clone();
        public byte[]? LoadBackupKey() => (byte[]?)BackupKey?.Clone();
        public void SavePairingSecret(byte[] secret) => PairingSecret = (byte[])secret.Clone();
        public byte[]? LoadPairingSecret() => (byte[]?)PairingSecret?.Clone();
        public void ClearPairingSecret() => PairingSecret = null;
        public void Clear() => IdentitySeed = BackupKey = PairingSecret = null;
    }
}
