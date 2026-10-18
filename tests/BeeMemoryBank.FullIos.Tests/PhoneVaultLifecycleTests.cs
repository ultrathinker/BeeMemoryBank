using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;
using GateState = BeeMemoryBank.Core.Interfaces.VaultState;
using AppVaultState = BeeMemoryBank.FullIos.Services.VaultState;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// The start and open sequence of a phone (docs/design/step-b-spec.md 5.8, 10.8, 12.1, 12.2; red tests 15.3.11 and 15.3.15), driven through the very
/// composition the iPhone app builds (<c>AddFullNode</c> + <c>AddPhoneVault</c> + <c>AddOpenWork</c>) over a real folder, real SQLite3 Multiple Ciphers
/// and a fake Keychain: seal in the foreground, background (lock closes the database), unlock with the password and with the quick unlock, a
/// migration interrupted and resolved at the next start, <c>phone_no_copy</c>, and no database key or page key in any key store.
/// </summary>
public class PhoneVaultLifecycleTests
{
    private const string Canary = "CANARY-phone-7f3a91c2e5";

    private sealed class Env : IPhoneEnvironment
    {
        public bool IsForeground { get; set; } = true;
        public int? BatteryPercent { get; set; } = 80;
        public bool IsCharging { get; set; }
        public bool PeerHoldsHead { get; set; } = true;
        public long? Free { get; set; }

        public long? FreeBytes(string path) => Free;
    }

    /// <summary>
    /// The Keychain of the tests: two items in memory. It refuses any value that is a database key or a page key of the vault - it must never be
    /// handed one (spec 12.4) - and writes down everything it was given.
    /// </summary>
    private sealed class GuardedKeychain : IUnlockKeyStore
    {
        public byte[]? Key;
        public byte[]? SlotBytes;
        public List<byte[]> Forbidden { get; } = [];
        public List<byte[]> Seen { get; } = [];
        public int Calls;

        public bool IsAvailable => true;
        public string BiometryName => "Face ID";
        public bool IsEnrolled => Key is not null && SlotBytes is not null;

        private void Check(byte[] value)
        {
            Seen.Add(value.ToArray());
            foreach (var forbidden in Forbidden)
                if (value.AsSpan().SequenceEqual(forbidden))
                    throw new InvalidOperationException("A database key or a page key reached the key store.");
        }

        public void Save(byte[] unlockKey, byte[] slot)
        {
            Calls++;
            Check(unlockKey);
            Check(slot);
            Key = unlockKey.ToArray();
            SlotBytes = slot.ToArray();
        }

        public byte[]? ReadSlot() => SlotBytes?.ToArray();

        public Task<byte[]?> ReadUnlockKeyAsync(string reason)
        {
            Calls++;
            if (Key is not null) Check(Key);
            return Task.FromResult(Key?.ToArray());
        }

        public void Forget()
        {
            Key = null;
            SlotBytes = null;
        }
    }

    /// <summary>One run of the app over a folder: what <c>OnStart</c> does, in the order of spec 5.8.</summary>
    private sealed class PhoneApp : IAsyncDisposable
    {
        public required string Dir { get; init; }
        public required PhoneStart Start { get; init; }
        public required ServiceProvider Services { get; init; }
        public required Env Environment { get; init; }

        public IVaultGate Gate => Services.GetRequiredService<IVaultGate>();
        public PhoneVaultLifecycle Lifecycle => Services.GetRequiredService<PhoneVaultLifecycle>();
        public SessionService Session => Services.GetRequiredService<SessionService>();
        public FullVault Vault => new(Services);
        public NotesService Notes => new(Services);
        public SealMigrationService Seal => Services.GetRequiredService<SealMigrationService>();

        public static async Task<PhoneApp> StartAsync(string dir, Env? environment = null, bool defaultPeerCheck = false)
        {
            environment ??= new Env();
            var start = PhoneVaultLifecycle.Begin(dir);
            var collection = new ServiceCollection()
                .AddFullNode(new FullNodePaths(dir))
                .AddPhoneVault(dir, start, encryptionOffered: true)
                .AddOpenWork()
                .AddSingleton<IPhoneEnvironment>(environment);
            // The default check (what the apps use) reads the peers' reported positions; the tests otherwise say what a peer reported.
            if (!defaultPeerCheck) collection.AddSingleton<PeerHoldsHead>(_ => _ => Task.FromResult(environment.PeerHoldsHead));
            var services = collection.BuildServiceProvider();
            if (start.CanStart)
            {
                // A sealed phone cannot read its database before a door has opened it: the sweep of the staging folder runs now, the rest at the open.
                if (start.Mode == VaultStoreMode.Sealed)
                {
                    FullNodeServices.SweepStaging(services);
                    await PhoneVaultLifecycle.PrepareSealedStartAsync(services);
                }
                else
                {
                    await FullNodeServices.PrepareAsync(services);
                }
            }
            return new PhoneApp { Dir = dir, Start = start, Services = services, Environment = environment };
        }

        /// <summary>A vault with a canary note, on a phone that joined a mesh which switched metadata encryption on (the migration needs it, spec 10.2).</summary>
        public async Task<Guid> CreateVaultWithCanaryAsync()
        {
            await Vault.CreateAsync("Test iPhone", "Correct-Horse-9");
            // The note first: a node that has switched on metadata encryption writes nothing without a metadata key, and this test phone holds none.
            var id = await Notes.SaveAsync(null, Canary, "/", Canary + " in the body");
            Services.GetRequiredService<ISyncProtocolPolicy>().MarkActivated();
            return id;
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            Start.Lease.Dispose();
        }
    }

    private const string Password = "Correct-Horse-9";

    private static IEnumerable<string> FilesOf(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".lock", StringComparison.Ordinal));

    private static bool AnyFileHoldsCanary(string dir)
    {
        var needle = Encoding.UTF8.GetBytes(Canary);
        var utf16 = Encoding.Unicode.GetBytes(Canary);
        foreach (var file in FilesOf(dir))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            if (bytes.AsSpan().IndexOf(needle) >= 0 || bytes.AsSpan().IndexOf(utf16) >= 0) return true;
        }
        return false;
    }

    private static async Task<PhoneApp> SealedAppAsync(string name)
    {
        var dir = TestFolders.New(name);
        var app = await PhoneApp.StartAsync(dir);
        await app.CreateVaultWithCanaryAsync();
        var preflight = await app.Seal.PreflightAsync(new SealOptions());
        preflight.Blockers.Should().BeEmpty();
        var done = await app.Seal.RunAsync(new SealOptions());
        done.Phase.Should().Be(SealProgress.Done);
        return app;
    }

    [Fact]
    public void PhoneEncryptionAvailable_is_true_in_this_build_and_a_plain_folder_starts_through_the_gate()
    {
        PhoneVaultLifecycle.PhoneEncryptionAvailable.Should().BeTrue("this build offers the phone's own encryption button");
        PhoneVaultLifecycle.NeedsGate(TestFolders.New("phone-gate")).Should().BeTrue("a phone that may be encrypted starts through the gate: lease, resolver, diagnosis");
    }

    [Fact]
    public async Task A_phone_is_sealed_locks_in_the_background_and_opens_with_the_password_and_with_the_quick_unlock()
    {
        await using var app = await PhoneApp.StartAsync(TestFolders.New("phone-seal"));
        app.Start.Mode.Should().Be(VaultStoreMode.Plain);
        var id = await app.CreateVaultWithCanaryAsync();
        AnyFileHoldsCanary(app.Dir).Should().BeTrue("the control: a plain phone shows the note to a raw scan");

        var done = await app.Seal.RunAsync(new SealOptions());

        done.Phase.Should().Be(SealProgress.Done);
        VaultStoreFiles.DetectMode(app.Dir).Should().Be(VaultStoreMode.Sealed);
        app.Gate.IsSealed.Should().BeTrue();
        app.Gate.State.Should().Be(GateState.Open, "the owner who encrypted it is still in");
        AnyFileHoldsCanary(app.Dir).Should().BeFalse("no file of the phone's folder shows the note once it is encrypted");
        (await app.Notes.GetAsync(id))!.Body.Should().Contain(Canary);

        // The database key and the vault's page key, as the gate holds them while it is open - the key store must never see either.
        var keys = app.Services.GetRequiredService<ISealedVaultKeys>();
        var keyService = app.Services.GetRequiredService<IDatabaseKeyService>();
        var dbk = keys.GetDatabaseKey();
        var pageKey = keyService.DerivePageKey(dbk, DbPurposes.Vault);
        var keychain = new GuardedKeychain();
        keychain.Forbidden.Add(dbk);
        keychain.Forbidden.Add(pageKey);
        var quick = new QuickUnlock(keychain, app.Services);
        quick.Enable();

        // Background: the session locks and the database closes.
        await app.Lifecycle.OnBackgroundAsync();
        app.Gate.State.Should().Be(GateState.Closed);
        app.Vault.IsUnlocked.Should().BeFalse();
        (await app.Vault.GetStateAsync()).Should().Be(AppVaultState.Locked, "an encrypted phone whose database is closed is a locked vault; the app reads nothing to say so");
        var read = async () => await app.Notes.GetAsync(id);
        await read.Should().ThrowAsync<VaultSealedException>("a closed phone reads nothing");

        // A wrong password does not open it, and looks like any wrong password.
        (await app.Lifecycle.UnlockWithPasswordAsync("wrong-password")).Should().BeFalse();
        app.Gate.State.Should().Be(GateState.Closed);
        (await app.Lifecycle.UnlockWithPasswordAsync(Password)).Should().BeTrue();
        app.Gate.State.Should().Be(GateState.Open);
        (await app.Notes.GetAsync(id))!.Body.Should().Contain(Canary);

        // Quick unlock with the fake Keychain.
        await app.Lifecycle.OnBackgroundAsync();
        app.Gate.State.Should().Be(GateState.Closed);
        (await quick.UnlockAsync("Open your memory bank")).Should().Be(QuickUnlockResult.Unlocked);
        app.Gate.State.Should().Be(GateState.Open);
        (await app.Notes.GetAsync(id))!.Body.Should().Contain(Canary);

        keychain.Calls.Should().BeGreaterThan(0);
        keychain.Seen.Should().NotBeEmpty();
        keychain.Seen.Any(s => s.AsSpan().SequenceEqual(dbk) || s.AsSpan().SequenceEqual(pageKey)).Should().BeFalse(
            "the key store holds an unlock key and a wrapped master key, never the database key or a page key");
    }

    [Fact]
    public async Task A_quick_unlock_key_that_does_not_open_this_vault_is_turned_off_and_the_phone_stays_closed()
    {
        await using var app = await SealedAppAsync("phone-quick-wrong");
        var keychain = new GuardedKeychain();
        var strangerDek = SecureRandom.GetBytes(32);
        var unlockKey = SecureRandom.GetBytes(32);
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(strangerDek, unlockKey);
        keychain.Save(unlockKey, QuickUnlock.Slot(iv, wrapped));
        await app.Lifecycle.OnBackgroundAsync();
        app.Gate.State.Should().Be(GateState.Closed);

        var result = await new QuickUnlock(keychain, app.Services).UnlockAsync("Open your memory bank");

        result.Should().Be(QuickUnlockResult.TurnedOff);
        keychain.IsEnrolled.Should().BeFalse("the quick unlock is off, the password is asked for");
        app.Gate.State.Should().Be(GateState.Closed);
        app.Vault.IsUnlocked.Should().BeFalse();
        (await app.Lifecycle.UnlockWithPasswordAsync(Password)).Should().BeTrue("the password still opens it");
    }

    [Theory]
    [InlineData("vacuum-vault", false)]
    [InlineData("journal-verified", true)]
    [InlineData("journal-swapping", true)]
    [InlineData("renamed-beememorybank.db", true)]
    [InlineData("renamed-beememorybank.boot.db", true)]
    [InlineData("journal-deleted", true)]
    public async Task A_migration_interrupted_at_a_step_is_resolved_at_the_next_start(string step, bool sealedAfter)
    {
        var dir = TestFolders.New("phone-kill-" + step.Replace('.', '-'));
        Guid id;
        await using (var app = await PhoneApp.StartAsync(dir))
        {
            id = await app.CreateVaultWithCanaryAsync();
            app.Seal.StepHook = s => { if (s == step) throw new SealKill(s); };
            app.Seal.SwapStepHook = s => { if (s == step) throw new SealKill(s); };
            var kill = async () => await app.Seal.RunAsync(new SealOptions());
            await kill.Should().ThrowAsync<SealKill>();
        }

        await using var next = await PhoneApp.StartAsync(dir);

        next.Start.CanStart.Should().BeTrue();
        next.Start.Mode.Should().Be(sealedAfter ? VaultStoreMode.Sealed : VaultStoreMode.Plain,
            $"after {step} the start goes {(sealedAfter ? "forward" : "back")}");
        Directory.EnumerateFileSystemEntries(dir, "*.seal-*").Should().BeEmpty("no replaced plain file or half-made target survives the next start");
        File.Exists(Path.Combine(dir, SealJournal.FileName)).Should().BeFalse();
        if (sealedAfter) AnyFileHoldsCanary(dir).Should().BeFalse("no plain copy survives the next start");
        (await next.Lifecycle.UnlockWithPasswordAsync(Password)).Should().BeTrue();
        (await next.Notes.GetAsync(id))!.Body.Should().Contain(Canary, "no third state: the phone holds what it held");
    }

    [Fact]
    public async Task phone_no_copy_blocks_an_unsynced_phone_and_passes_when_a_peer_holds_the_head_or_the_owner_ticks_the_box()
    {
        var environment = new Env { PeerHoldsHead = false };
        await using var app = await PhoneApp.StartAsync(TestFolders.New("phone-no-copy"), environment);
        await app.CreateVaultWithCanaryAsync();

        var unsynced = await app.Seal.PreflightAsync(new SealOptions());
        unsynced.Blockers.Select(b => b.Code).Should().Equal(SealBlockers.PhoneNoCopy);
        var started = async () => await app.Seal.StartAsync(Password, new SealOptions());
        await started.Should().ThrowAsync<SealMigrationException>("the migration does not start while the blocker stands");
        VaultStoreFiles.DetectMode(app.Dir).Should().Be(VaultStoreMode.Plain, "nothing was changed");

        (await app.Seal.PreflightAsync(new SealOptions(AcceptNoSnapshot: true))).Blockers.Should().BeEmpty("the owner's tick");

        environment.PeerHoldsHead = true;
        (await app.Seal.PreflightAsync(new SealOptions())).Blockers.Should().BeEmpty("a peer reported the head");
    }

    [Fact]
    public async Task The_default_peer_check_reads_the_positions_peers_reported()
    {
        await using var app = await PhoneApp.StartAsync(TestFolders.New("phone-peer-positions"), defaultPeerCheck: true);
        await app.CreateVaultWithCanaryAsync();
        (await app.Seal.PreflightAsync(new SealOptions())).Blockers.Select(b => b.Code).Should().BeEquivalentTo([SealBlockers.PhoneNoCopy], "no peer is known");

        var peer = Guid.NewGuid();
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = peer, DisplayName = "A laptop", Ed25519PublicKey = RandomNumberGenerator.GetBytes(32), Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        var head = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        var positions = scope.ServiceProvider.GetRequiredService<ISyncPushPositionRepository>();
        await positions.RecordReportedPositionAsync(peer, Math.Max(0, head - 1));
        (await app.Seal.PreflightAsync(new SealOptions())).Blockers.Select(b => b.Code).Should().BeEquivalentTo([SealBlockers.PhoneNoCopy], "the peer has read less than the phone's head");

        await positions.RecordReportedPositionAsync(peer, head);
        (await app.Seal.PreflightAsync(new SealOptions())).Blockers.Should().BeEmpty("a peer reported a position at the phone's head");
    }

    [Fact]
    public async Task The_foreground_and_the_battery_block_the_migration()
    {
        var environment = new Env { IsForeground = false };
        await using var app = await PhoneApp.StartAsync(TestFolders.New("phone-foreground"), environment);
        await app.CreateVaultWithCanaryAsync();

        (await app.Seal.PreflightAsync(new SealOptions())).Blockers.Select(b => b.Code).Should().Equal(SealBlockers.MobileForeground);

        environment.IsForeground = true;
        environment.BatteryPercent = 20;
        (await app.Seal.PreflightAsync(new SealOptions())).Blockers.Select(b => b.Code).Should().BeEquivalentTo([SealBlockers.MobileForeground], "a low battery without a charger");

        environment.IsCharging = true;
        (await app.Seal.PreflightAsync(new SealOptions())).Blockers.Should().BeEmpty("a charger makes up for the battery");
    }

    [Fact]
    public async Task The_free_space_is_the_phones_data_partition_not_the_drive_of_the_path_root()
    {
        // On Android the root of every path is the read-only system partition with no free space; the phone answers with the data partition.
        var environment = new Env { Free = 0 };
        await using var app = await PhoneApp.StartAsync(TestFolders.New("phone-free-space"), environment);
        await app.CreateVaultWithCanaryAsync();

        (await app.Seal.PreflightAsync(new SealOptions())).Blockers.Select(b => b.Code).Should().Equal(SealBlockers.FreeSpace);

        environment.Free = 32L * 1024 * 1024 * 1024;
        (await app.Seal.PreflightAsync(new SealOptions())).Blockers.Should().BeEmpty("the data partition has room");

        environment.Free = null;
        (await app.Seal.PreflightAsync(new SealOptions())).Blockers.Should().BeEmpty("no host answer falls back to the generic one, and a test disk has room");
    }

    [Fact]
    public async Task A_sealed_folder_with_a_plain_companion_does_not_start_and_says_why()
    {
        string dir;
        await using (var app = await SealedAppAsync("phone-companion"))
            dir = app.Dir;
        var plainChat = new byte[4096];
        "SQLite format 3\0"u8.CopyTo(plainChat);
        plainChat[16] = 0x10;   // page size 4096
        plainChat[18] = 1;
        plainChat[19] = 1;
        plainChat[28] = 0;
        plainChat[31] = 1;
        File.WriteAllBytes(Path.Combine(dir, "chat.db"), plainChat);

        var start = PhoneVaultLifecycle.Begin(dir);
        try
        {
            start.CanStart.Should().BeFalse();
            start.Broken!.Reason.Should().Be("plain_companion");
            start.Broken.Files.Should().Contain("chat.db");
        }
        finally
        {
            start.Lease.Dispose();
        }
    }

    [Fact]
    public async Task A_phone_that_may_not_be_encrypted_registers_no_gate_and_no_key_material()
    {
        var dir = TestFolders.New("phone-hidden");
        var start = PhoneVaultLifecycle.Begin(dir);
        await using var services = new ServiceCollection()
            .AddFullNode(new FullNodePaths(dir))
            .AddPhoneVault(dir, start)
            .AddOpenWork()
            .BuildServiceProvider();
        try
        {
            await FullNodeServices.PrepareAsync(services);

            services.GetService<IVaultGate>().Should().BeNull("while the button is hidden a plain phone has no gate");
            services.GetService<SealMigrationService>().Should().BeNull();
            File.Exists(Path.Combine(dir, VaultStoreFiles.Boot)).Should().BeFalse("no boot store is made on a plain phone");
            (await new FullVault(services).CreateAsync("Test iPhone", Password)).Should().NotBeNullOrEmpty();
            await services.GetRequiredService<PhoneVaultLifecycle>().OnBackgroundAsync();
            new FullVault(services).IsUnlocked.Should().BeFalse("the background still locks a plain phone");
        }
        finally
        {
            start.Lease.Dispose();
        }
    }
}
