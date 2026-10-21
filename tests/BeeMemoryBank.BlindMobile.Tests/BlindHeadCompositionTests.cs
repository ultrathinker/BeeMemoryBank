using System.Collections.Concurrent;
using System.Security.Cryptography;
using BeeMemoryBank.BlindMobile.Platforms.Android;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// The Android head composed as the app composes it (<see cref="BlindAndroidHead.HostOptions"/>, <see cref="BlindDevice"/>) over software keys and in-memory preferences: two accounts in one process with their own
/// names, a removal that really clears the keys and the state, the upgrade of an install of 2.6.4 (account 1, nothing moved or renamed, keeps syncing), and the work plan the accounts' switches give.
/// The platform parts that only a phone can run - the Keystore calls, the SharedPreferences, WorkManager - are one small class each and are not in here.
/// </summary>
public sealed class BlindHeadCompositionTests : IDisposable
{
    private readonly TempRoot _root = new();
    private readonly SoftwareKeyWrap _keystore = new();
    private readonly FakePreferences _preferences = new();
    private readonly FakeBlindDeviceConditions _conditions = new() { IsMetered = false, IsPowerSaving = false };
    private readonly List<BlindDevice> _devices = [];

    public void Dispose()
    {
        foreach (var device in _devices) device.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _root.Dispose();
    }

    private BlindDevice NewDevice(Action<IServiceCollection, BlindAccountEntry>? overrides = null)
    {
        var device = new BlindDevice(BlindAndroidHead.HostOptions(
            _root.Path, _keystore, _preferences.Open, _conditions, displayNameFactory: () => "Test phone", overrides: overrides));
        _devices.Add(device);
        return device;
    }

    private static async Task<string> AddAsync(BlindDevice device, string name)
    {
        var result = await device.Accounts.AddAsync(name);
        result.Ok.Should().BeTrue(result.Error);
        return result.AccountId!;
    }

    // ---- two accounts in one process ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFreshInstall_HasNoAccount_AndNothingOfAnAccountIsWrittenInTheRoot()
    {
        var device = NewDevice();

        await device.StartAsync();

        device.Accounts.GetSnapshot().Accounts.Should().BeEmpty();
        device.Accounts.GetSnapshot().CanAdd.Should().BeTrue();
        _preferences.Files.Should().BeEmpty("no account, no preferences file");
        Directory.GetFiles(_root.Path, "bmb_blind_*").Should().BeEmpty();
    }

    [Fact]
    public async Task TwoAccounts_HaveTheirOwnKeystoreEntries_Blobs_PreferencesAndIdentity_AndNeitherNameIsAccountOnes()
    {
        var device = NewDevice();
        await device.StartAsync();

        var a = await AddAsync(device, "Alpha");
        var b = await AddAsync(device, "Beta");

        foreach (var id in new[] { a, b })
        {
            foreach (var blob in new[] { "bmb_blind_seed.bin", "bmb_blind_pairing.bin", "bmb_blind_backup.bin" })
                File.Exists(_root.Combine("accounts", id, "keys", blob)).Should().BeTrue($"{id}/{blob}");
            _keystore.Keys.Keys.Should().Contain([$"bmb_blind_seed_v1.{id}", $"bmb_blind_pairing_v1.{id}", $"bmb_blind_backup_v1.{id}"]);
            _preferences.Files.Keys.Should().Contain($"bmb_blind_state.{id}");
        }
        _keystore.Keys.Keys.Should().NotContain("bmb_blind_seed_v1", "account 1's names are used by account 1 alone");
        _preferences.Files.Keys.Should().NotContain("bmb_blind_state");
        Directory.GetFiles(_root.Path, "bmb_blind_*").Should().BeEmpty("a new account writes nothing in the root");
        var nodeA = device.Accounts.For(a).GetStatus().NodeId;
        var nodeB = device.Accounts.For(b).GetStatus().NodeId;
        nodeA.Should().NotBeNull();
        nodeB.Should().NotBeNull();
        nodeA!.Value.Should().NotBe(nodeB!.Value, "every account is its own blind copy");
    }

    [Fact]
    public async Task RemovingAnAccount_ClearsItsKeystoreEntries_Blobs_AndPreferences_AndLeavesTheOtherWorking()
    {
        var device = NewDevice();
        await device.StartAsync();
        var a = await AddAsync(device, "Alpha");
        var b = await AddAsync(device, "Beta");

        await device.Accounts.RemoveAsync(b);

        Directory.Exists(_root.Combine("accounts", b)).Should().BeFalse();
        _keystore.Deleted.Should().Contain([$"bmb_blind_seed_v1.{b}", $"bmb_blind_pairing_v1.{b}", $"bmb_blind_backup_v1.{b}"]);
        _keystore.Keys.Keys.Should().NotContain(k => k.EndsWith(b, StringComparison.Ordinal));
        _preferences.Files.Keys.Should().NotContain($"bmb_blind_state.{b}");
        _keystore.Keys.Keys.Should().Contain($"bmb_blind_seed_v1.{a}", "the other account keeps its keys");
        _preferences.Files.Keys.Should().Contain($"bmb_blind_state.{a}");
        device.Accounts.GetSnapshot().Accounts.Should().ContainSingle().Which.Entry.Id.Should().Be(a);
        device.Accounts.For(a).GetStatus().NodeId.Should().NotBeNull();
    }

    [Fact]
    public async Task AKeystoreThatDoesNotDeleteAnEntry_LeavesTheAccountDeleting_AndTheNextStartFinishesTheRemoval()
    {
        var keystore = new FlakyKeyWrap();
        string a, b;
        await using (var device = new BlindDevice(BlindAndroidHead.HostOptions(_root.Path, keystore, _preferences.Open, _conditions, displayNameFactory: () => "Test phone")))
        {
            await device.StartAsync();
            a = await AddAsync(device, "Alpha");
            b = await AddAsync(device, "Beta");
            keystore.FailDelete = true;

            var remove = () => device.Accounts.RemoveAsync(b);

            await remove.Should().ThrowAsync<AggregateException>();
            var card = device.Accounts.GetSnapshot().Accounts.Single(c => c.Entry.Id == b);
            card.Entry.Phase.Should().Be(BlindAccountPhases.Deleting, "the account is not reported removed while a Keystore entry of it is still there");
            keystore.Inner.Keys.Keys.Should().Contain($"bmb_blind_seed_v1.{b}");
            card.Problem.Should().NotContain("android.security").And.NotContain(b, "the platform's words and the alias do not reach the card");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        keystore.FailDelete = false;

        await using var restarted = new BlindDevice(BlindAndroidHead.HostOptions(_root.Path, keystore, _preferences.Open, _conditions, displayNameFactory: () => "Test phone"));
        await restarted.StartAsync();

        restarted.Accounts.GetSnapshot().Accounts.Should().ContainSingle().Which.Entry.Id.Should().Be(a);
        keystore.Inner.Keys.Keys.Should().NotContain(k => k.EndsWith(b, StringComparison.Ordinal), "every entry of the removed account is confirmed gone");
        Directory.Exists(_root.Combine("accounts", b)).Should().BeFalse();
    }

    [Fact]
    public async Task APreferencesCommitThatFails_LeavesTheAccountDeleting_AndItsStateIsErasedWhenThePhoneAnswersAgain()
    {
        var device = NewDevice();
        await device.StartAsync();
        var b = await AddAsync(device, "Beta");
        _preferences.CommitSucceeds = false;

        var remove = () => device.Accounts.RemoveAsync(b);

        await remove.Should().ThrowAsync<AggregateException>();
        device.Accounts.GetSnapshot().Accounts.Single().Entry.Phase.Should().Be(BlindAccountPhases.Deleting, "state the phone did not erase is state still on the phone");
        _preferences.Files.Keys.Should().Contain($"bmb_blind_state.{b}");

        _preferences.CommitSucceeds = true;
        await device.Accounts.RemoveAsync(b);

        device.Accounts.GetSnapshot().Accounts.Should().BeEmpty();
        _preferences.Files.Keys.Should().NotContain($"bmb_blind_state.{b}");
        _keystore.Keys.Keys.Should().NotContain(k => k.EndsWith(b, StringComparison.Ordinal));
    }

    [Fact]
    public async Task APreferencesClearThatThrows_LeavesTheAccountDeleting_TheFileIsStillAskedToGo_AndTheRetryFinishes()
    {
        var device = NewDevice();
        await device.StartAsync();
        var b = await AddAsync(device, "Beta");
        _preferences.ClearThrows = true;

        var remove = () => device.Accounts.RemoveAsync(b);

        await remove.Should().ThrowAsync<AggregateException>();
        device.Accounts.GetSnapshot().Accounts.Single().Entry.Phase.Should().Be(BlindAccountPhases.Deleting);
        _preferences.DeleteCalls.Should().BeGreaterThan(0, "the erase did not stop at the throwing step");

        _preferences.ClearThrows = false;
        await device.Accounts.RemoveAsync(b);

        device.Accounts.GetSnapshot().Accounts.Should().BeEmpty();
        _preferences.Files.Keys.Should().NotContain($"bmb_blind_state.{b}");
    }

    [Fact]
    public async Task APreferencesFileThatCannotBeDeleted_LeavesTheAccountDeleting()
    {
        var device = NewDevice();
        await device.StartAsync();
        var b = await AddAsync(device, "Beta");
        _preferences.DeleteSucceeds = false;

        var remove = () => device.Accounts.RemoveAsync(b);

        await remove.Should().ThrowAsync<AggregateException>();
        device.Accounts.GetSnapshot().Accounts.Single().Entry.Phase.Should().Be(BlindAccountPhases.Deleting);

        _preferences.DeleteSucceeds = true;
        await device.Accounts.RemoveAsync(b);

        device.Accounts.GetSnapshot().Accounts.Should().BeEmpty();
        _preferences.Files.Keys.Should().NotContain($"bmb_blind_state.{b}");
    }

    [Fact]
    public async Task HostileTextsThatPassTheGate_AreRefusedByThePreviewWithoutThrowing_AndNothingIsAdded()
    {
        var device = NewDevice();
        await device.StartAsync();
        // What gets past the length gate and is still not an invitation: control characters, bidi overrides, a huge number, percent escapes, a different scheme, a lone surrogate, the right scheme with nothing after it.
        var hostile = new[]
        {
            "bmb-invite:", "bmb-invite:?", "bmb-invite:?\0\0\0", "bmb-invite:?x=‮gnp.exe", "‮bmb-invite:?x=1", "BMB-INVITE:?%00%ff%zz",
            "bmb-invite:?v=99999999999999999999999999&x=" + new string('A', 1900), "https://evil.example/bmb-invite:?x=1", "javascript:alert(1)",
            new string('\u0007', 2048), string.Concat(Enumerable.Repeat("bmb-invite:?", 170)), "bmb-invite:?" + new string((char)0xD800, 100),
        };

        foreach (var text in hostile)
        {
            var preview = device.Accounts.PreviewInvitation(text);

            preview.CanAdd.Should().BeFalse($"a hostile text ({text.Length} characters) is not an invitation");
            preview.Refusal.Should().NotBeNullOrWhiteSpace();
        }
        device.Accounts.GetSnapshot().Accounts.Should().BeEmpty("a preview adds nothing");
    }

    [Fact]
    public async Task APreviewOfATextOverTheLimit_IsRefusedByTheCoreToo_WhateverTheGateDid()
    {
        var device = NewDevice();
        await device.StartAsync();

        var preview = device.Accounts.PreviewInvitation("bmb-invite:?" + new string('a', BlindInvitation.MaxTextLength));

        preview.CanAdd.Should().BeFalse("the guard at the page is a second line, not the only one");
    }

    [Fact]
    public async Task AccountsAddedAfterARestart_AreOpenedWithTheirOwnKeys()
    {
        var first = NewDevice();
        await first.StartAsync();
        var a = await AddAsync(first, "Alpha");
        var nodeBefore = first.Accounts.For(a).GetStatus().NodeId;
        await first.DisposeAsync();
        _devices.Remove(first);

        var second = NewDevice();
        await second.StartAsync();

        second.Accounts.For(a).GetStatus().NodeId.Should().Be(nodeBefore, "the same identity, read back through the same Keystore entries and preferences");
        second.Accounts.For(a).GetStatus().BackupKeyLost.Should().BeFalse();
    }

    // ---- the upgrade of an install of 2.6.4 --------------------------------------------------------------------------------------------------

    private sealed class DummyLifecycle : IBlindLifecycle
    {
        public void StopBackgroundWork() { }
        public void StopBackupService() { }
        public void RestartAfterWipe() { }
    }

    private sealed class RecordingSync : IBlindPhoneSync
    {
        public ConcurrentQueue<BlindCallCode> Targets { get; } = new();

        public Task SyncOnceAsync(BlindCallCode target, CancellationToken ct)
        {
            Targets.Enqueue(target);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// What a phone with the 2.6.4 app holds: made by the 2.6 composition itself (one account, the legacy store names, <c>bmb_blind_state</c>), first start included, then paired with a computer. Returns the node id of the copy.
    /// </summary>
    private async Task<(Guid NodeId, BlindCallCode CallCode)> InstallVersion264Async(bool initialLoadDone = true)
    {
        var legacy = new BlindAccountEntry(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");
        var state = _preferences.Open("bmb_blind_state");
        var secrets = new KeystoreBlindSecretStoreFactory(_root.Path, _keystore).For(legacy);

        var services = new ServiceCollection();
        BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(_root.Path, _root.Combine("beememorybank.db"), DisplayNameFactory: () => "Old phone"));
        services.AddSingleton<IBlindStateStore>(state);
        services.AddSingleton(secrets);
        services.AddSingleton<IBlindLifecycle, DummyLifecycle>();
        await using (var provider = services.BuildServiceProvider())
        {
            await provider.GetRequiredService<IBlindAppController>().InitializeAsync();

            var (hubKey, _) = Ed25519Signer.GenerateKeyPair();
            var callCode = BlindCallCode.Create("https://old-computer.example.org:5610", Guid.NewGuid(), Base64UrlOf(RandomNumberGenerator.GetBytes(32)), hubKey, RandomNumberGenerator.GetBytes(32));
            var phone = provider.GetRequiredService<BlindPhoneState>();
            phone.CallCode = callCode;
            phone.InitialLoadDone = initialLoadDone;
            phone.Schedule = BlindBackupSchedule.Off;
            return (phone.NodeId!.Value, callCode);
        }
    }

    private static string Base64UrlOf(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);

    /// <summary>Every file under the folder (SQLite's sidecars come and go with its connections and are not data).</summary>
    private static HashSet<string> FilesOf(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal) && !f.EndsWith("-journal", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(folder, f)).ToHashSet();

    private static Dictionary<string, string> Hashes(string folder, params string[] patterns) =>
        patterns.SelectMany(p => Directory.GetFiles(folder, p, SearchOption.TopDirectoryOnly))
            .ToDictionary(f => Path.GetFileName(f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    [Fact]
    public async Task AnInstallOf264_BecomesExactlyOneAccount_WithNothingMovedRenamedOrRekeyed_AndKeepsSyncing()
    {
        var (nodeId, callCode) = await InstallVersion264Async();
        var blobsBefore = Hashes(_root.Path, "bmb_blind_*.bin");
        var aliasesBefore = _keystore.Keys.Keys.OrderBy(k => k).ToList();
        var wrapsBefore = _keystore.Wraps.Count;
        var prefsBefore = _preferences.Files["bmb_blind_state"].ToDictionary(p => p.Key, p => p.Value);
        var filesBefore = FilesOf(_root.Path);
        blobsBefore.Keys.Should().BeEquivalentTo(["bmb_blind_seed.bin", "bmb_blind_pairing.bin", "bmb_blind_backup.bin"], "the fixture is the 2.6.4 layout: the blobs are in the root");
        aliasesBefore.Should().Equal("bmb_blind_backup_v1", "bmb_blind_pairing_v1", "bmb_blind_seed_v1");

        var sync = new RecordingSync();
        var device = NewDevice((services, _) => services.AddSingleton<IBlindPhoneSync>(sync));
        await device.StartAsync();

        // exactly one account, account 1, in the root
        var accounts = device.Accounts.GetSnapshot();
        accounts.RegistryProblem.Should().BeNull();
        accounts.RepairIssues.Should().BeEmpty();
        var one = accounts.Accounts.Should().ContainSingle().Which;
        one.Entry.Id.Should().Be(BlindAccountEntry.LegacyId);
        one.Entry.Layout.Should().Be(BlindAccountLayouts.Root);
        one.Entry.EffectiveLimits.IsEmpty.Should().BeTrue("account 1 after the upgrade has no limit");
        one.Entry.UnmeteredOnly.Should().BeFalse();
        one.Entry.PauseOnPowerSaving.Should().BeFalse();
        var status = device.Accounts.For(BlindAccountEntry.LegacyId).GetStatus();
        status.NodeId.Should().Be(nodeId, "the same blind node, not a new one");
        status.IsPaired.Should().BeTrue();
        status.InitialLoadDone.Should().BeTrue();
        status.BackupKeyLost.Should().BeFalse("the key under the old alias still opens");
        status.StartError.Should().BeNull();

        // nothing moved, renamed or re-keyed
        Hashes(_root.Path, "bmb_blind_*.bin").Should().Equal(blobsBefore);
        _keystore.Keys.Keys.OrderBy(k => k).Should().Equal(aliasesBefore, "no new Keystore entry was made");
        _keystore.Deleted.Should().BeEmpty();
        _keystore.Wraps.Count.Should().Be(wrapsBefore, "no secret was written again");
        _preferences.Files.Keys.Should().BeEquivalentTo(["bmb_blind_state"]);
        _preferences.Files["bmb_blind_state"].Should().Contain(prefsBefore, "every value of the old state is still there");
        Directory.Exists(_root.Combine("accounts")).Should().BeFalse();
        var filesAfter = FilesOf(_root.Path);
        filesBefore.Except(filesAfter).Should().BeEmpty("no file was moved or renamed");
        File.Exists(_root.Combine("accounts.json")).Should().BeTrue("the registry is the one new file that matters");

        // keeps syncing: the sync worker's pass reaches the account with its old call code
        await device.SyncScheduler.TickAsync();
        sync.Targets.Should().ContainSingle().Which.Address.Should().Be(callCode.Address);
        device.Accounts.For(BlindAccountEntry.LegacyId).GetStatus().LastSyncAt.Should().NotBeNull();

        // a second account next to it touches nothing of account 1
        var added = await AddAsync(device, "Second");
        Hashes(_root.Path, "bmb_blind_*.bin").Should().Equal(blobsBefore);
        _keystore.Keys.Keys.Should().Contain([.. aliasesBefore, $"bmb_blind_seed_v1.{added}"]);
        _preferences.Files["bmb_blind_state"].Should().Contain(prefsBefore);
    }

    [Fact]
    public async Task TheUpgradedInstall_StartsAgainTheSameWay_ARestartChangesNothing()
    {
        await InstallVersion264Async();
        var first = NewDevice();
        await first.StartAsync();
        var registry = File.ReadAllText(_root.Combine("accounts.json"));
        await first.DisposeAsync();
        _devices.Remove(first);

        var second = NewDevice();
        await second.StartAsync();

        File.ReadAllText(_root.Combine("accounts.json")).Should().Be(registry, "the adoption is idempotent");
        second.Accounts.GetSnapshot().Accounts.Should().ContainSingle();
    }

    // ---- the work plan -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheWatch_AsksForUnmeteredAndBatteryOnlyWhenEveryAccountSaysSo_AndWhenAFirstLoadIsNeeded()
    {
        await InstallVersion264Async(initialLoadDone: false);
        var device = NewDevice();
        await device.StartAsync();
        var seen = new ConcurrentQueue<BlindWorkRequirements>();
        var heavyNow = 0;

        device.Watch(r => seen.Enqueue(r), () => Interlocked.Increment(ref heavyNow), quiet: TimeSpan.FromMilliseconds(20));

        seen.Should().NotBeEmpty().And.OnlyContain(r => r == BlindWorkRequirements.Default, "account 1 after the upgrade: any network, any battery");
        heavyNow.Should().Be(1, "the paired copy has no first load: it is asked for at once, not at the next hourly slot");

        device.Accounts.SetSwitches(BlindAccountEntry.LegacyId, unmeteredOnly: true, pauseOnPowerSaving: true).Should().BeNull();
        await EventuallyAsync(() => seen.Contains(new BlindWorkRequirements(true, true)), "the periodic requests follow the only account's switches");

        var second = await AddAsync(device, "Second");
        device.Accounts.SetSwitches(second, unmeteredOnly: true, pauseOnPowerSaving: false).Should().BeNull();
        await EventuallyAsync(() => seen.Contains(new BlindWorkRequirements(true, false)), "one account that does not pause on battery saver is enough to keep that wait off");
        heavyNow.Should().Be(1, "the same copy is not asked for twice");
    }

    private static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        condition().Should().BeTrue(what);
    }

    [Fact]
    public void WhatTheAccountsWait_ForTheTwoPeriodicRequests_IsDecidedByAccountsThatUseTheNetwork()
    {
        BlindAccountEntry Account(string phase = BlindAccountPhases.Active, bool unmetered = false, bool battery = false, bool paused = false) =>
            new(BlindAccountEntry.NewId(), BlindAccountLayouts.Dir, "x", phase, UnmeteredOnly: unmetered, PauseOnPowerSaving: battery, PausedByUser: paused);

        BlindWorkPlan.For([]).Should().Be(BlindWorkRequirements.Default, "no account: nothing to wait for");
        BlindWorkPlan.For([Account()]).Should().Be(BlindWorkRequirements.Default);
        BlindWorkPlan.For([Account(unmetered: true, battery: true)]).Should().Be(new BlindWorkRequirements(true, true));
        BlindWorkPlan.For([Account(unmetered: true), Account(unmetered: true)]).Should().Be(new BlindWorkRequirements(true, false));
        BlindWorkPlan.For([Account(unmetered: true), Account()]).Should().Be(BlindWorkRequirements.Default, "one account that wants any network keeps the wake-ups on any network");
        BlindWorkPlan.For([Account(battery: true), Account(battery: true, unmetered: true)]).Should().Be(new BlindWorkRequirements(false, true));

        // accounts that use no network do not decide
        BlindWorkPlan.For([Account(unmetered: true), Account(paused: true)]).Should().Be(new BlindWorkRequirements(true, false), "paused by the person");
        BlindWorkPlan.For([Account(unmetered: true), Account(BlindAccountPhases.Deleting)]).Should().Be(new BlindWorkRequirements(true, false));
        BlindWorkPlan.For([Account(unmetered: true), Account(BlindAccountPhases.Refused), Account(BlindAccountPhases.Expired), Account(BlindAccountPhases.RemovedByOwner)])
            .Should().Be(new BlindWorkRequirements(true, false));
        BlindWorkPlan.For([Account(BlindAccountPhases.AwaitingConfirmation, unmetered: true), Account(BlindAccountPhases.Pending, unmetered: true)])
            .Should().Be(new BlindWorkRequirements(true, false), "an invitation that waits for its owner asks the network too");
        BlindWorkPlan.For([Account(paused: true), Account(BlindAccountPhases.Refused)]).Should().Be(BlindWorkRequirements.Default);
    }
}
