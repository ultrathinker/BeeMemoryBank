using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Storage.PreUnlock;
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
    /// The Keychain of the tests: the door's secret (and an older version's wrapped-key item) in memory. It refuses any value that is a database
    /// key or a page key of the vault - it must never be handed one (spec 12.4) - and writes down everything it was given.
    /// </summary>
    private sealed class GuardedKeychain : IUnlockKeyStore
    {
        public byte[]? Secret;
        public byte[]? LegacyKey;
        public byte[]? LegacySlot;
        public List<byte[]> Forbidden { get; } = [];
        public List<byte[]> Seen { get; } = [];
        public int Calls;

        public bool IsAvailable => true;
        public string BiometryName => "Face ID";
        public bool IsEnrolled => Secret is not null || (LegacyKey is not null && LegacySlot is not null);

        private void Check(byte[] value)
        {
            Seen.Add(value.ToArray());
            foreach (var forbidden in Forbidden)
                if (value.AsSpan().SequenceEqual(forbidden))
                    throw new InvalidOperationException("A database key or a page key reached the key store.");
        }

        public void Save(byte[] secret)
        {
            Calls++;
            Check(secret);
            // The real store's order: the new secret first, then an older version's items.
            Secret = secret.ToArray();
            LegacyKey = null;
            LegacySlot = null;
        }

        public byte[]? ReadLegacySlot() => LegacySlot?.ToArray();

        public Task<byte[]?> ReadSecretAsync(string reason)
        {
            Calls++;
            if (Secret is not null) Check(Secret);
            return Task.FromResult(Secret?.ToArray());
        }

        public Task<byte[]?> ReadLegacyKeyAsync(string reason)
        {
            Calls++;
            if (LegacyKey is not null) Check(LegacyKey);
            return Task.FromResult(LegacyKey?.ToArray());
        }

        public void Forget()
        {
            Secret = null;
            LegacyKey = null;
            LegacySlot = null;
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
            await Vault.CreateAsync("Test iPhone", "Correct-Horse-9", "A hint for the canary vault");
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
        await quick.EnableAsync();

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
        await WaitForOpenAsync(app);
        (await app.Notes.GetAsync(id))!.Body.Should().Contain(Canary);

        // Quick unlock with the fake Keychain.
        await app.Lifecycle.OnBackgroundAsync();
        app.Gate.State.Should().Be(GateState.Closed);
        (await quick.UnlockAsync("Open your memory bank")).Should().Be(QuickUnlockResult.Unlocked);
        await WaitForOpenAsync(app);
        (await app.Notes.GetAsync(id))!.Body.Should().Contain(Canary);

        keychain.Calls.Should().BeGreaterThan(0);
        keychain.Seen.Should().NotBeEmpty();
        keychain.Seen.Any(s => s.AsSpan().SequenceEqual(dbk) || s.AsSpan().SequenceEqual(pageKey)).Should().BeFalse(
            "the key store holds an unlock key and a wrapped master key, never the database key or a page key");
    }

    /// <summary>
    /// The iPhone crash at every cold start: an encrypted phone starts with its database closed, and the unlock page read the node's
    /// identity from it (VaultSealedException out of an async void OnAppearing). What the unlock page asks before anything is unlocked - the state,
    /// the node's name, the password hint - reads nothing from the closed database: the name comes from the boot store's mirror, the hint is inside
    /// the database and is simply not shown. Then Face ID opens the vault as before.
    /// </summary>
    [Fact]
    public async Task A_sealed_phone_at_a_cold_start_shows_its_unlock_page_without_reading_the_closed_database_and_Face_ID_opens_it()
    {
        var (dir, nodeId, keychain) = await SealedPhoneWithFaceIdLeftLockedAsync("phone-cold-start-faceid");

        await using var app = await PhoneApp.StartAsync(dir);
        await TheUnlockPageAppearsAsync(app, nodeId);

        var quick = new QuickUnlock(keychain, app.Services);
        quick.IsEnabled.Should().BeTrue("Face ID is still turned on: the unlock page offers it at once");
        (await quick.UnlockAsync("Open your memory bank")).Should().Be(QuickUnlockResult.Unlocked);
        await WaitForOpenAsync(app);
        (await app.Vault.IdentityAsync())!.NodeId.Should().Be(nodeId);
        (await app.Vault.PasswordHintAsync()).Should().Be("A hint for the canary vault", "once open, the hint is readable again");
    }

    /// <summary>The same cold start, opened with the master password.</summary>
    [Fact]
    public async Task A_sealed_phone_at_a_cold_start_shows_its_unlock_page_without_reading_the_closed_database_and_the_password_opens_it()
    {
        var (dir, nodeId, _) = await SealedPhoneWithFaceIdLeftLockedAsync("phone-cold-start-password");

        await using var app = await PhoneApp.StartAsync(dir);
        await TheUnlockPageAppearsAsync(app, nodeId);

        (await app.Vault.UnlockAsync(Password)).Should().BeTrue();
        await WaitForOpenAsync(app);
        (await app.Vault.IdentityAsync())!.DisplayName.Should().Be("Test iPhone");
    }

    /// <summary>What UnlockPage.OnAppearing asks, on a phone whose database is closed: nothing throws and nothing opens.</summary>
    private static async Task TheUnlockPageAppearsAsync(PhoneApp app, Guid nodeId)
    {
        app.Start.Mode.Should().Be(VaultStoreMode.Sealed);
        app.Gate.State.Should().Be(GateState.Closed, "a cold start opens nothing before the person unlocks");
        (await app.Vault.GetStateAsync()).Should().Be(AppVaultState.Locked);
        var identity = await app.Vault.IdentityAsync();
        identity.Should().NotBeNull("the node's name is readable while closed: the boot store mirrors it");
        identity!.NodeId.Should().Be(nodeId);
        identity.DisplayName.Should().Be("Test iPhone");
        (await app.Vault.PasswordHintAsync()).Should().BeNull("the hint is inside the closed database");
        app.Gate.State.Should().Be(GateState.Closed, "reading what the unlock page shows opened nothing");
        app.Vault.IsUnlocked.Should().BeFalse();
    }

    /// <summary>An encrypted phone with Face ID turned on, left locked (database closed) as the app leaves the screen; the app process then ends.</summary>
    private static async Task<(string Dir, Guid NodeId, GuardedKeychain Keychain)> SealedPhoneWithFaceIdLeftLockedAsync(string name)
    {
        var keychain = new GuardedKeychain();
        await using var app = await SealedAppAsync(name);
        var nodeId = (await app.Vault.IdentityAsync())!.NodeId;
        await new QuickUnlock(keychain, app.Services).EnableAsync();
        await app.Lifecycle.OnBackgroundAsync();
        app.Gate.State.Should().Be(GateState.Closed);
        return (app.Dir, nodeId, keychain);
    }

    /// <summary>
    /// the phone wipes ("delete all data", "cancel and delete" of the first sync, the setup reset) delete the whole sealed file
    /// set, as the node's reset does. Deleting the database alone left the boot store behind, and the next start was the dead end "db_missing_or_empty"
    /// with nothing to tap: an encrypted Android phone was unusable until its app data was cleared in the system settings.
    /// </summary>
    [Fact]
    public async Task A_wiped_encrypted_phone_starts_again_as_a_fresh_phone()
    {
        string dir;
        await using (var app = await SealedAppAsync("phone-wipe"))
        {
            dir = app.Dir;
            await app.Lifecycle.OnBackgroundAsync();
            await app.Vault.UnlockAsync(Password);
            await WaitForOpenAsync(app);
            File.Exists(Path.Combine(dir, VaultStoreFiles.Boot)).Should().BeTrue("an encrypted phone keeps its doors in the boot store");
            File.Exists(Path.Combine(dir, PasswordHintFile.FileName)).Should().BeTrue("and its hint in the pre-unlock mirror");

            // The wipe runs on an unlocked phone, from the settings tab.
            await app.Lifecycle.ReturnToPlainAsync(dir);

            app.Gate.IsSealed.Should().BeFalse("the gate is a plain gate again");
            app.Vault.IsUnlocked.Should().BeFalse();
        }
        foreach (var name in new[] { VaultStoreFiles.Boot, VaultStoreFiles.Database, VaultStoreFiles.Chat, PasswordHintFile.FileName, VaultStoreFiles.SealJournal })
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                File.Exists(Path.Combine(dir, name + suffix)).Should().BeFalse($"{name + suffix} is part of the vault and is wiped");

        // The restart after the wipe: the folder is a fresh phone's, the welcome / setup screen comes.
        await using var next = await PhoneApp.StartAsync(dir);
        next.Start.CanStart.Should().BeTrue("a wiped phone is not a broken one");
        next.Start.Mode.Should().Be(VaultStoreMode.Plain);
        (await next.Vault.GetStateAsync()).Should().Be(AppVaultState.NoVault);
        AnyFileHoldsCanary(dir).Should().BeFalse("nothing of the wiped vault is left");
    }

    /// <summary>
    /// the iPhone's wipe of an encrypted vault (every iPhone's) ends on Welcome. The folder went, but the gate in this process
    /// was still sealed and closed, so preparing the fresh database threw: the app said "Wipe failed" after a wipe that had worked, and sat on an
    /// unlock page with no door until it was force-quit. The gate is plain again before the fresh schema is prepared.
    /// </summary>
    [Fact]
    public async Task The_wipe_of_an_encrypted_iPhone_ends_on_a_fresh_phone_in_the_same_process()
    {
        await using var app = await SealedAppAsync("phone-wipe-ios");
        app.Gate.IsSealed.Should().BeTrue();

        (await new SecurityOps(app.Services).WipeDataAsync(new FullNodePaths(app.Dir))).Should().BeTrue("the folder is gone and the fresh schema is ready");

        app.Gate.IsSealed.Should().BeFalse("nothing sealed is left to open: the gate is plain");
        (await app.Vault.GetStateAsync()).Should().Be(AppVaultState.NoVault, "the Welcome gate comes, not an unlock page with no door");
        File.Exists(Path.Combine(app.Dir, VaultStoreFiles.Boot)).Should().BeFalse();
        (await app.Vault.CreateAsync("Test iPhone", Password, "A hint after the wipe")).Should().NotBeNullOrEmpty("a new vault can be made at once");
    }

    [Fact]
    public async Task A_new_vault_made_on_this_phone_is_encrypted_by_itself_and_stays_open()
    {
        // every database is encrypted, with no button. A vault made here starts its own network, so the new sync format is
        // switched on first (with its metadata key), as a desktop's setup does, and the encryption asks the person nothing.
        await using var app = await PhoneApp.StartAsync(TestFolders.New("phone-auto-seal"));
        await app.Vault.CreateAsync("Test iPhone", Password, "A hint for the auto seal");
        var sealing = new PhoneSealing(app.Services);
        sealing.IsNeeded.Should().BeTrue("a new vault is a plain file until it is encrypted");

        await sealing.SealAsync(startsNetwork: true);

        app.Gate.IsSealed.Should().BeTrue();
        VaultStoreFiles.DetectMode(app.Dir).Should().Be(VaultStoreMode.Sealed);
        app.Session.IsUnlocked.Should().BeTrue("the person goes on into the notes");
        sealing.IsNeeded.Should().BeFalse();
        await app.Notes.SaveAsync(null, Canary, "/", Canary + " in the body");
        AnyFileHoldsCanary(app.Dir).Should().BeFalse("a raw scan of the phone shows nothing once it is encrypted");
    }

    [Fact]
    public async Task A_phone_in_a_network_on_the_old_sync_format_is_left_as_it_is_and_says_why()
    {
        // A phone never switches the new format on for a whole network: only a vault that starts its own network does.
        await using var app = await PhoneApp.StartAsync(TestFolders.New("phone-auto-seal-old"));
        await app.Vault.CreateAsync("Test iPhone", Password, "A hint for the old network");

        var act = () => new PhoneSealing(app.Services).SealAsync(startsNetwork: false);

        (await act.Should().ThrowAsync<PhoneSealingException>()).Which.Code.Should().Be(PhoneSealingException.OldNetwork);
        app.Gate.IsSealed.Should().BeFalse();
    }

    [Fact]
    public async Task An_older_quick_unlock_whose_key_does_not_open_this_vault_is_turned_off_and_the_phone_stays_closed()
    {
        await using var app = await SealedAppAsync("phone-quick-wrong");
        var keychain = new GuardedKeychain();
        var strangerDek = SecureRandom.GetBytes(32);
        var unlockKey = SecureRandom.GetBytes(32);
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(strangerDek, unlockKey);
        keychain.LegacyKey = unlockKey;
        keychain.LegacySlot = QuickUnlock.Slot(iv, wrapped);
        await app.Lifecycle.OnBackgroundAsync();
        app.Gate.State.Should().Be(GateState.Closed);

        var result = await new QuickUnlock(keychain, app.Services).UnlockAsync("Open your memory bank");

        result.Should().Be(QuickUnlockResult.TurnedOff);
        keychain.IsEnrolled.Should().BeFalse("the quick unlock is off, the password is asked for");
        app.Gate.State.Should().Be(GateState.Closed);
        app.Vault.IsUnlocked.Should().BeFalse();
        (await app.Lifecycle.UnlockWithPasswordAsync(Password)).Should().BeTrue("the password still opens it");
    }

    [Fact]
    public async Task An_older_quick_unlock_on_an_encrypted_phone_opens_the_closed_database_and_becomes_a_door()
    {
        await using var app = await SealedAppAsync("phone-quick-legacy");
        var keychain = new GuardedKeychain();
        var unlockKey = SecureRandom.GetBytes(32);
        var masterDek = app.Session.GetMasterDek();
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(masterDek, unlockKey);
        Array.Clear(masterDek);
        keychain.LegacyKey = unlockKey;
        keychain.LegacySlot = QuickUnlock.Slot(iv, wrapped);
        await app.Lifecycle.OnBackgroundAsync();
        var quick = new QuickUnlock(keychain, app.Services);

        (await quick.UnlockAsync("Open your memory bank")).Should().Be(QuickUnlockResult.Unlocked);
        await WaitForOpenAsync(app);

        keychain.LegacySlot.Should().BeNull("the wrapped master key left the Keychain");
        keychain.Secret.Should().HaveCount(32).And.NotEqual(unlockKey);
        await app.Lifecycle.OnBackgroundAsync();
        (await quick.UnlockAsync("Open your memory bank")).Should().Be(QuickUnlockResult.Unlocked, "the door opens the closed database from now on");
        await WaitForOpenAsync(app);
    }

    [Fact]
    public async Task An_older_quick_unlock_from_before_a_rotation_does_not_open_the_closed_encrypted_phone_and_the_password_does()
    {
        // the plain variant is in QuickUnlockTests; here the old master key meets the closed database itself (OpenSealedAsync).
        await using var app = await SealedAppAsync("phone-quick-legacy-rotated");
        var keychain = new GuardedKeychain();
        var unlockKey = SecureRandom.GetBytes(32);
        var oldDek = app.Session.GetMasterDek();
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(oldDek, unlockKey);
        Array.Clear(oldDek);
        keychain.LegacyKey = unlockKey;
        keychain.LegacySlot = QuickUnlock.Slot(iv, wrapped);
        var newDek = SecureRandom.GetBytes(32);
        await Rotation.AsPeerAsync(app.Services, newDek);
        await app.Lifecycle.OnBackgroundAsync();
        app.Gate.State.Should().Be(GateState.Closed);
        var quick = new QuickUnlock(keychain, app.Services);

        var result = await quick.UnlockAsync("Open your memory bank");

        result.Should().Be(QuickUnlockResult.TurnedOff, "the old item holds the master key from before the rotation");
        app.Gate.State.Should().Be(GateState.Closed, "a refused open closes the database again");
        app.Vault.IsUnlocked.Should().BeFalse();
        keychain.IsEnrolled.Should().BeFalse();
        keychain.LegacySlot.Should().BeNull();
        (await app.Lifecycle.UnlockWithPasswordAsync(Password)).Should().BeTrue("the password, once");
        await WaitForOpenAsync(app);
        var current = app.Session.GetMasterDek();
        current.Should().Equal(newDek);
        Array.Clear(current);
        await quick.EnableAsync();
        await app.Lifecycle.OnBackgroundAsync();
        (await quick.UnlockAsync("Open your memory bank")).Should().Be(QuickUnlockResult.Unlocked, "Face ID turned on again opens it");
        await WaitForOpenAsync(app);
    }

    [Fact]
    public async Task Face_ID_opens_an_encrypted_phone_at_a_cold_start_through_the_door_in_the_boot_store()
    {
        var dir = TestFolders.New("phone-quick-cold");
        var keychain = new GuardedKeychain();
        Guid id;
        await using (var app = await PhoneApp.StartAsync(dir))
        {
            id = await app.CreateVaultWithCanaryAsync();
            (await app.Seal.RunAsync(new SealOptions())).Phase.Should().Be(SealProgress.Done);
            await new QuickUnlock(keychain, app.Services).EnableAsync();
            await app.Lifecycle.OnBackgroundAsync();
        }

        // A new process: nothing of the vault is in memory, the database is closed, only the boot store can be read.
        await using var next = await PhoneApp.StartAsync(dir);
        next.Start.Mode.Should().Be(VaultStoreMode.Sealed);
        next.Gate.IsUsable.Should().BeFalse();
        var quick = new QuickUnlock(keychain, next.Services);

        (await quick.UnlockAsync("Open your memory bank")).Should().Be(QuickUnlockResult.Unlocked);
        await WaitForOpenAsync(next);

        (await next.Notes.GetAsync(id))!.Body.Should().Contain(Canary);
    }

    [Fact]
    public async Task After_a_key_rotation_Face_ID_still_opens_the_closed_encrypted_phone_and_asks_nothing_new()
    {
        await using var app = await SealedAppAsync("phone-quick-rotation");
        var keychain = new GuardedKeychain();
        var quick = new QuickUnlock(keychain, app.Services);
        await quick.EnableAsync();
        var saves = keychain.Calls;
        var newDek = SecureRandom.GetBytes(32);

        await Rotation.AsPeerAsync(app.Services, newDek);
        await app.Lifecycle.OnBackgroundAsync();
        app.Gate.State.Should().Be(GateState.Closed);

        (await quick.UnlockAsync("Open your memory bank")).Should().Be(QuickUnlockResult.Unlocked);
        await WaitForOpenAsync(app);

        keychain.Calls.Should().Be(saves + 1, "one read of the secret, nothing stored again");
        var current = app.Session.GetMasterDek();
        current.Should().Equal(newDek);
        Array.Clear(current);
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
            (await new FullVault(services).CreateAsync("Test iPhone", Password, TestVault.Hint)).Should().NotBeNullOrEmpty();
            await services.GetRequiredService<PhoneVaultLifecycle>().OnBackgroundAsync();
            new FullVault(services).IsUnlocked.Should().BeFalse("the background still locks a plain phone");
        }
        finally
        {
            start.Lease.Dispose();
        }
    }

    /// <summary>
    /// An unlock returns when the database is open; the post-unlock work (the inbox import and the mirror rewrite, spec 4.4 step 8) runs
    /// after it and publishes Open when it ends. The test waits for that end instead of asserting on the moment the task was scheduled.
    /// </summary>
    private static async Task WaitForOpenAsync(PhoneApp app)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (app.Gate.State != GateState.Open && DateTime.UtcNow < until) await Task.Delay(20);
        app.Gate.State.Should().Be(GateState.Open, "the post-unlock work publishes Open when it ends");
    }
}
