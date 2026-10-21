using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BeeMemoryBank.BlindDesktop.MacOS;
using BeeMemoryBank.BlindDesktop.MacOS.Interop;
using BeeMemoryBank.BlindDesktop.Tests;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Platforms.Apple.Keychain;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindDesktop.MacOS.Tests;

/// <summary>The AppCore contract of a per-account secret store, run over the Keychain store (on the in-memory Keychain, so it runs on any OS): two accounts never see each other's secrets, clearing one leaves the other, an item moved under another account's name is refused.</summary>
public sealed class MacOsSecretStoreContractTests : BlindSecretStoreContract
{
    private const string Service = "com.example.test-blind";
    private readonly FakeKeychainBackend _keychain = new();

    protected override IBlindSecretStoreFactory CreateFactory() => new MacOsSecretStoreFactory(_keychain, Service);

    protected override IBlindSecretStoreFactory Reopen() => new MacOsSecretStoreFactory(_keychain, Service);

    protected override void CopyBlob(BlindAccountEntry from, BlindAccountEntry to, BlindAccountNames.Role role)
    {
        var factory = new MacOsSecretStoreFactory(_keychain, Service);
        factory.WriteBlob(to, role, factory.ReadBlob(from, role) ?? throw new InvalidOperationException("nothing was saved to copy"));
    }
}

/// <summary>The AppCore contract of a per-account state store, run over the state files.</summary>
public sealed class MacOsStateStoreContractTests : BlindStateStoreContract
{
    private readonly TempFolder _root = new();

    protected override IBlindStateStoreFactory CreateFactory() => new MacOsBlindStateStoreFactory(_root.Path);

    protected override IBlindStateStoreFactory Reopen() => new MacOsBlindStateStoreFactory(_root.Path);
}

/// <summary>Where each account's items and state are, and that the 2.6 account's names are what 2.6.4 wrote.</summary>
public class MacOsAccountNamesTests
{
    private const string Service = "com.example.test-blind";
    private static readonly BlindAccountEntry Legacy = new(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");
    private static readonly BlindAccountEntry A = new("a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", BlindAccountLayouts.Dir, "A");
    private static readonly BlindAccountEntry B = new("b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2", BlindAccountLayouts.Dir, "B");

    private static byte[] Secret(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    [Fact]
    public void TheLegacyAccount_KeepsTodaysKeychainAccounts_AndTheOldStoreReadsWhatTheFactoryWrote()
    {
        var keychain = new FakeKeychainBackend();
        var factory = new MacOsSecretStoreFactory(keychain, Service);
        factory.For(Legacy).SaveIdentitySeed(Secret(1));
        factory.For(Legacy).SaveBackupKey(Secret(2));
        factory.For(Legacy).SavePairingSecret(Secret(3));

        keychain.Items.Keys.OrderBy(k => k.Item2, StringComparer.Ordinal).Select(k => (k.Item1, k.Item2)).Should().Equal(
            (Service, "backup-key-v1"), (Service, "identity-seed-v1"), (Service, "pairing-secret-v1"));
        // The store of 2.6.4 (no account at all) opens what the factory wrote, and the other way round.
        var old = new MacOsKeychainSecretStore(keychain, Service);
        old.LoadIdentitySeed().Should().Equal(Secret(1));
        old.LoadBackupKey().Should().Equal(Secret(2));
        old.SaveBackupKey(Secret(9));
        factory.For(Legacy).LoadBackupKey().Should().Equal(Secret(9));
    }

    [Fact]
    public void ANewAccount_HasItsIdInTheKeychainAccount_InTheSameService()
    {
        var keychain = new FakeKeychainBackend();
        var factory = new MacOsSecretStoreFactory(keychain, Service);

        factory.For(A).SaveIdentitySeed(Secret(7));

        keychain.Items.Keys.Should().ContainSingle().Which.Should().Be((Service, A.Id + "/identity-seed-v1"));
        keychain.Labels.Should().ContainSingle().Which.Should().Contain("identity seed");
    }

    [Fact]
    public void ClearingAnAccount_RemovesItsThreeItems_NotTheOthers()
    {
        var keychain = new FakeKeychainBackend();
        var factory = new MacOsSecretStoreFactory(keychain, Service);
        foreach (var account in new[] { Legacy, A, B })
        {
            factory.For(account).SaveIdentitySeed(Secret(1));
            factory.For(account).SaveBackupKey(Secret(2));
            factory.For(account).SavePairingSecret(Secret(3));
        }

        ((MacOsKeychainSecretStore)factory.For(A)).Clear();

        keychain.Items.Keys.Select(k => k.Item2).Should().NotContain(a => a.StartsWith(A.Id));
        keychain.Items.Should().HaveCount(6);
        factory.For(B).LoadIdentitySeed().Should().Equal(Secret(1));
    }

    [Fact]
    public void AnItemMovedUnderAnotherAccount_IsRefusedAsAlteredOutsideTheApp_NeverUsed()
    {
        var keychain = new FakeKeychainBackend();
        var factory = new MacOsSecretStoreFactory(keychain, Service);
        factory.For(A).SaveBackupKey(Secret(5));
        factory.WriteBlob(B, BlindAccountNames.Role.BackupKey, factory.ReadBlob(A, BlindAccountNames.Role.BackupKey)!);

        var act = () => factory.For(B).LoadBackupKey();

        act.Should().Throw<BlindSecretStoreException>().Which.Failure.Should().Be(BlindSecretStoreFailure.Corrupt);
    }

    [Fact]
    public void TheStoresOfTheFactory_ShareOneBackend_AndDisposingAStoreLeavesItOpen()
    {
        var keychain = new FakeKeychainBackend();
        using var factory = new MacOsSecretStoreFactory(keychain, Service);

        ((MacOsKeychainSecretStore)factory.For(A)).Dispose();
        factory.For(B).SaveIdentitySeed(Secret(4));

        factory.For(B).LoadIdentitySeed().Should().Equal(Secret(4), "disposing one account's store does not close the Keychain for the others");
    }

    [Fact]
    public void TheStateFile_IsBlindStateJson_InTheAccountsFolder_AndTheLegacyOneIsTheRootsOwn()
    {
        using var root = new TempFolder();
        var factory = new MacOsBlindStateStoreFactory(root.Path);

        factory.For(Legacy).Set("k", "legacy-value");
        factory.For(A).Set("k", "a-value");

        File.ReadAllText(Path.Combine(root.Path, "blind-state.json")).Should().Contain("legacy-value").And.NotContain("a-value");
        File.ReadAllText(Path.Combine(root.Path, "accounts", A.Id, "blind-state.json")).Should().Contain("a-value").And.NotContain("legacy-value");
        factory.For(A).Should().BeSameAs(factory.For(A), "one store object per file");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MakeAndDropStores(MacOsBlindStateStoreFactory factory, int count)
    {
        for (var i = 0; i < count; i++) _ = factory.For(NewAccount()).Get("k");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<IBlindStateStore> WriteAndDrop(MacOsBlindStateStoreFactory factory, BlindAccountEntry account)
    {
        var store = factory.For(account);
        store.Set("k", "kept in the file");
        return new WeakReference<IBlindStateStore>(store);
    }

    private static void CollectEverything()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static BlindAccountEntry NewAccount() => new(BlindAccountEntry.NewId(), BlindAccountLayouts.Dir, "A");

    /// <summary>
    /// Collects until <paramref name="holds"/> or the budget is spent, then runs <paramref name="assert"/> either way. One collect can be too early: the stores are cleaned up
    /// asynchronously (thread pool, finalizers), and a check that reads one of the tables sees the dead entries only after the sweep that the next store runs has removed them.
    /// Strict in both directions: the loop leaves the moment <paramref name="holds"/> is true, so a pass costs nothing, and it never passes by itself - <paramref name="assert"/>
    /// always has the last word, so a real leak (something holds the store) still fails after the wait, with the old message. <paramref name="poke"/> re-runs the sweep that lets
    /// the cleanup show, for the checks that read a table.
    /// </summary>
    private static void CollectUntil(Func<bool> holds, Action assert, Action? poke = null)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!holds() && Environment.TickCount64 < deadline)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            poke?.Invoke();
            Thread.Sleep(50);
        }
        assert();
    }

    [Fact]
    public void TheStateFactory_DoesNotKeepTheStoresOfAccountsThatAreGone()
    {
        using var root = new TempFolder();
        var factory = new MacOsBlindStateStoreFactory(root.Path);

        MakeAndDropStores(factory, 300);
        CollectEverything();
        _ = factory.For(NewAccount());   // the next store sweeps the dead entries out of the table

        CollectUntil(() => factory.Tracked < 5,
            () => factory.Tracked.Should().BeLessThan(5, "300 accounts were made and dropped; the table holds what is alive, not what once was"),
            () => _ = factory.For(NewAccount()));
    }

    [Fact]
    public void TheStateFactory_GivesEveryoneTheSameStoreWhileAnyoneHoldsIt_AndAFreshOneAfterwards()
    {
        using var root = new TempFolder();
        var factory = new MacOsBlindStateStoreFactory(root.Path);
        var held = factory.For(A);
        CollectEverything();
        factory.For(A).Should().BeSameAs(held, "one object per file while it is in use: two would each have their own copy of the file");

        var dropped = WriteAndDrop(factory, B);
        CollectEverything();

        CollectUntil(() => !dropped.TryGetTarget(out _), () => dropped.TryGetTarget(out _).Should().BeFalse("nothing holds it"));
        factory.For(B).Get("k").Should().Be("kept in the file", "the state is in the file, not in the object");
        GC.KeepAlive(held);
    }

    [Fact]
    public void ErasingAnAccountsState_RemovesItsFileAndItsDamagedCopies_NotAnotherAccounts()
    {
        using var root = new TempFolder();
        var factory = new MacOsBlindStateStoreFactory(root.Path);
        factory.For(Legacy).Set("k", "1");
        factory.For(A).Set("k", "2");
        File.WriteAllText(Path.Combine(root.Path, "accounts", A.Id, "blind-state.json.damaged-1"), "old");

        factory.For(A).Erase();

        Directory.GetFiles(Path.Combine(root.Path, "accounts", A.Id), "blind-state.json*").Should().BeEmpty();
        File.Exists(Path.Combine(root.Path, "blind-state.json")).Should().BeTrue();
    }

    [Fact]
    public void AnIdThatIsNotOneThisAppMade_IsRefused()
    {
        var factory = new MacOsSecretStoreFactory(new FakeKeychainBackend(), Service);
        var evil = new BlindAccountEntry("../x", BlindAccountLayouts.Dir, "evil");

        var act = () => factory.For(evil).SaveIdentitySeed(Secret(1));

        act.Should().Throw<ArgumentException>();
    }
}

/// <summary>
/// What the Keychain store says when it fails, for an account made by the app (its item names are <c>&lt;id&gt;/backup-key-v1</c>): a sentence of this code - the secret by its kind, the system's status number,
/// the advice - that the shared failure text passes on (it is marked, and it names no place), and never the account id or the item's name.
/// </summary>
public class MacOsKeychainFailureTextTests
{
    private const string Service = "com.example.test-blind";
    private static readonly BlindAccountEntry A = new("a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", BlindAccountLayouts.Dir, "A");

    private static (IBlindSecretStore Store, FakeKeychainBackend Keychain) Make()
    {
        var keychain = new FakeKeychainBackend();
        return (new MacOsSecretStoreFactory(keychain, Service).For(A), keychain);
    }

    private static void IsSaidAsItIs(Exception ex, params string[] expected)
    {
        var said = BlindFailureText.Say(ex);
        said.Should().Be(ex.Message.ReplaceLineEndings(" ").Trim(), "a sentence of this code is marked: the formatter passes it on, it does not fall back to a kind");
        said.Should().NotContain(A.Id).And.NotContain("-v1").And.NotContain("/").And.NotContain(Service);
        BlindFailureText.LooksLikeALocation(said).Should().BeFalse();
        foreach (var part in expected) said.Should().Contain(part);
        said.Length.Should().BeLessThan(BlindFailureText.MaxSentence);
    }

    [Fact]
    public void ALockedKeychain_IsToldWithTheStatusAndTheKindOfSecret_NotTheItemName()
    {
        var (store, keychain) = Make();
        store.SaveBackupKey([1, 2, 3]);
        keychain.Locked = true;

        var act = () => store.LoadBackupKey();

        var ex = act.Should().Throw<BlindSecretStoreException>().Which;
        ex.Should().BeAssignableTo<IBlindOwnSentence>();
        IsSaidAsItIs(ex, "backup key", "-25308", "User interaction is not allowed");
    }

    [Theory]
    [InlineData("identity seed")]
    [InlineData("pairing secret")]
    [InlineData("backup key")]
    public void EveryOperation_NamesTheSecretByItsKind(string kind)
    {
        var (store, keychain) = Make();
        keychain.Locked = true;
        Action[] operations = kind switch
        {
            "identity seed" => [() => store.SaveIdentitySeed([1]), () => store.LoadIdentitySeed()],
            "pairing secret" => [() => store.SavePairingSecret([1]), () => store.LoadPairingSecret(), () => store.ClearPairingSecret()],
            _ => [() => store.SaveBackupKey([1]), () => store.LoadBackupKey()],
        };

        foreach (var operation in operations)
            IsSaidAsItIs(operation.Should().Throw<BlindSecretStoreException>().Which, kind, "-25308");
    }

    [Fact]
    public void AnItemThatWasAlteredOutsideTheApp_IsToldWithTheAdvice_AndTheKindOfSecret()
    {
        var (store, keychain) = Make();
        keychain.Put(Service, BlindAccountNames.KeychainAccount(A, BlindAccountNames.Role.BackupKey), new byte[64]);   // not an envelope of ours

        var act = () => store.LoadBackupKey();

        var ex = act.Should().Throw<BlindSecretStoreException>().Which;
        ex.Failure.Should().Be(BlindSecretStoreFailure.Corrupt);
        IsSaidAsItIs(ex, "backup key", "altered outside the app", "remove this account and add it again");
    }

    [Fact]
    public void AFailedClear_IsToldByTheFirstItemThatFailed_WithoutNamingIt()
    {
        var (store, keychain) = Make();
        store.SaveIdentitySeed([1]);
        keychain.FailNext["delete"] = KeychainStatus.AuthFailed;

        var act = () => store.Clear();

        var ex = act.Should().Throw<AggregateException>().Which;
        IsSaidAsItIs(ex.InnerExceptions.Single(), "identity seed", "-25293");
        BlindFailureText.Say(ex).Should().Be(BlindFailureText.Say(ex.InnerExceptions.Single()), "an aggregate says what its first failure says");
        ex.Message.Should().NotContain(A.Id, "not even the text of the aggregate names the account");
    }

    [Fact]
    public void ASystemPhraseThatNamesAPlace_IsLeftOut_AndTheStatusStays()
    {
        var (store, keychain) = Make();
        keychain.DescribedAs = "No such keychain: /Users/someone/Library/Keychains/login.keychain-db";
        keychain.FailNext["copy"] = KeychainStatus.NoSuchKeychain;

        var act = () => store.LoadIdentitySeed();

        var ex = act.Should().Throw<BlindSecretStoreException>().Which;
        IsSaidAsItIs(ex, "identity seed", KeychainStatus.NoSuchKeychain.ToString());
        ex.Message.Should().NotContain("someone").And.NotContain("login.keychain");
    }

    [Fact]
    public void TheMessagesOfTheContractTestHelpers_NameNoPlaceEither()
    {
        var keychain = new FakeKeychainBackend();
        var factory = new MacOsSecretStoreFactory(keychain, Service);
        keychain.Locked = true;

        var read = () => factory.ReadBlob(A, BlindAccountNames.Role.BackupKey);
        var write = () => factory.WriteBlob(A, BlindAccountNames.Role.BackupKey, [1]);

        IsSaidAsItIs(read.Should().Throw<BlindSecretStoreException>().Which, "-25308");
        IsSaidAsItIs(write.Should().Throw<BlindSecretStoreException>().Which, "-25308");
    }
}

/// <summary>The seams of the macOS head for several accounts, put together the way the host does.</summary>
public class MacOsSeamsTests
{
    [Fact]
    public void CreateSeams_GivesTheFactories_TheConditions_AndTheAutostart_OverTheSameFolder()
    {
        using var root = new TempFolder();
        var seams = MacOsBlindHostServices.CreateSeams(new MacOsBlindHostOptions
        {
            ApplicationSupportRoot = root.Path,
            KeychainBackend = new FakeKeychainBackend(),
            LaunchAgentsDirectory = root.File("LaunchAgents"),
            AutostartProgramArguments = ["/usr/bin/true"],
            LoadAutostartImmediately = false,
        });

        seams.Paths.DataDirectory.Should().Be(Path.Combine(Path.GetFullPath(root.Path), "BeeMemoryBankBlind"));
        seams.Secrets.Should().BeOfType<MacOsSecretStoreFactory>();
        seams.States.Should().BeOfType<MacOsBlindStateStoreFactory>();
        seams.Conditions.Should().BeOfType<MacOsDeviceConditions>();
        seams.Autostart.Should().BeOfType<MacOsBlindAutostart>();
    }

    [Fact]
    public void ASecretFactoryGivenByATest_ReplacesTheKeychain()
    {
        using var root = new TempFolder();
        var memory = new InMemoryBlindSecretStoreFactory();

        var seams = MacOsBlindHostServices.CreateSeams(new MacOsBlindHostOptions { ApplicationSupportRoot = root.Path }, secrets: memory, conditions: new FakeConditions());

        seams.Secrets.Should().BeSameAs(memory);
        seams.Conditions.Should().BeOfType<FakeConditions>();
    }
}

internal sealed class FakeConditions : IBlindDeviceConditions
{
    public bool? IsMetered { get; set; }
    public bool? IsPowerSaving { get; set; }
}

/// <summary>"Only on Wi-Fi or other unmetered networks" and "Pause on battery saver" as macOS answers them: the path monitor of Network.framework and Low Power Mode.</summary>
public class MacOsDeviceConditionsTests
{
    private sealed class Source : IMacOsConditionsSource
    {
        public MacOsNetworkPath? Path { get; set; } = new(true, false, false);
        public bool? LowPower { get; set; } = false;
        public Exception? Failure { get; set; }
        public int Reads;

        public MacOsNetworkPath? ReadNetworkPath()
        {
            Reads++;
            return Failure is { } f ? throw f : Path;
        }

        public bool? ReadLowPowerMode() => Failure is { } f ? throw f : LowPower;
    }

    [Theory]
    [InlineData(true, false, false, false)]   // an ordinary Wi-Fi or Ethernet path
    [InlineData(true, true, false, true)]     // expensive: a hotspot, a tethered phone
    [InlineData(true, false, true, true)]     // constrained: Low Data Mode
    [InlineData(true, true, true, true)]
    public void ASatisfiedPath_IsMeteredWhenExpensiveOrConstrained(bool satisfied, bool expensive, bool constrained, bool metered)
    {
        MacOsDeviceConditions.Metered(new MacOsNetworkPath(satisfied, expensive, constrained)).Should().Be(metered);
    }

    [Fact]
    public void NoUsablePath_AndNoReportYet_AreNotGuessedEitherWay()
    {
        MacOsDeviceConditions.Metered(new MacOsNetworkPath(false, false, false)).Should().BeNull("no network: cannot tell");
        MacOsDeviceConditions.Metered(null).Should().BeNull("the monitor has not reported yet");
    }

    [Fact]
    public void TheAdapter_AnswersFromItsSource()
    {
        var source = new Source { Path = new MacOsNetworkPath(true, true, false), LowPower = true };

        var conditions = new MacOsDeviceConditions(source);

        conditions.IsMetered.Should().BeTrue();
        conditions.IsPowerSaving.Should().BeTrue();
    }

    [Fact]
    public void LowPowerMode_OnAnOlderSystem_IsCannotTell()
    {
        new MacOsDeviceConditions(new Source { LowPower = null }).IsPowerSaving.Should().BeNull();
    }

    [Fact]
    public void ASourceThatThrows_AnswersCannotTell_ItNeverBreaksTheLoop()
    {
        var conditions = new MacOsDeviceConditions(new Source { Failure = new PlatformNotSupportedException("not on this system") });

        conditions.IsMetered.Should().BeNull();
        conditions.IsPowerSaving.Should().BeNull();
    }

    [Fact]
    public void MacOsIsAskedAtMostEveryTwoSeconds()
    {
        var time = new ManualClock();
        var source = new Source();
        var conditions = new MacOsDeviceConditions(source, time);

        _ = conditions.IsMetered;
        _ = conditions.IsPowerSaving;
        _ = conditions.IsMetered;
        source.Reads.Should().Be(1);

        source.Path = new MacOsNetworkPath(true, true, false);
        conditions.IsMetered.Should().BeFalse("still the answer of a moment ago");
        time.Advance(TimeSpan.FromSeconds(3));
        conditions.IsMetered.Should().BeTrue();
        source.Reads.Should().Be(2);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void TheRealSource_OffAMac_SaysCannotTell_WithoutWaiting_AndWithoutTouchingAnything()
    {
        if (OperatingSystem.IsMacOS()) return;   // on a Mac the real source answers for real: see TheRealSource_OnAMac

        var started = DateTime.UtcNow;
        var conditions = new MacOsDeviceConditions();

        conditions.IsMetered.Should().BeNull();
        conditions.IsPowerSaving.Should().BeNull();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(2), "a monitor that could not be started is not waited for");
        SystemMacOsConditionsSource.Problem.Should().NotBeNull().And.NotContain("/", "the reason names a kind of failure, never a path");
    }

    [MacOnlyFact]
    public void TheRealSource_OnAMac_AnswersForReal()
    {
        var source = new SystemMacOsConditionsSource();

        var path = source.ReadNetworkPath();
        var low = source.ReadLowPowerMode();

        SystemMacOsConditionsSource.Problem.Should().BeNull();
        low.Should().NotBeNull("macOS 12 and later answer");
        path.Should().NotBeNull("the monitor reports the path in use within half a second");
    }
}

/// <summary>The hand-built Objective-C block that Network.framework calls. Its layout is checked here against the block ABI; that the system accepts it is checked on a Mac.</summary>
public class GlobalBlockLayoutTests
{
    private delegate void Handler(IntPtr block, IntPtr path);

    [Fact]
    public void TheBlock_HasTheLayoutOfAGlobalBlockWithNoHelpers()
    {
        Handler handler = (_, _) => { };
        var isa = new IntPtr(0x1234_5678);

        var block = new GlobalBlock(handler, isa);

        Marshal.ReadIntPtr(block.Pointer, 0).Should().Be(isa, "isa is _NSConcreteGlobalBlock");
        Marshal.ReadInt32(block.Pointer, 8).Should().Be(1 << 28, "BLOCK_IS_GLOBAL only: no copy/dispose helpers, no signature, not refcounted");
        Marshal.ReadInt32(block.Pointer, 12).Should().Be(0, "reserved");
        Marshal.ReadIntPtr(block.Pointer, 16).Should().Be(Marshal.GetFunctionPointerForDelegate(handler), "invoke is the function the delegate stands for");
        var descriptor = Marshal.ReadIntPtr(block.Pointer, 24);
        descriptor.Should().NotBe(IntPtr.Zero);
        Marshal.ReadInt64(descriptor, 0).Should().Be(0, "descriptor.reserved");
        Marshal.ReadInt64(descriptor, 8).Should().Be(32, "descriptor.size is the size of the literal: isa 8 + flags 4 + reserved 4 + invoke 8 + descriptor 8");
        GC.KeepAlive(handler);
    }
}

/// <summary>The upgrade of an installed 2.6.4 copy on a Mac: its Keychain items and its state file become the one account "legacy", under the names they always had, and keep working.</summary>
public class MacOsUpgradeRehearsalTests
{
    private const string Service = "com.example.test-blind";

    [Fact]
    public async Task A264MacInstall_BecomesAccountOne_WithItsItemsAndItsStateFileWhereTheyWere()
    {
        using var root = new TempFolder();
        var keychain = new FakeKeychainBackend();
        var paths = MacOsBlindPaths.AtDirectory(Path.Combine(root.Path, "BeeMemoryBankBlind"));

        // What 2.6.4 left: the single-account composition over the folder, a Keychain store without an account, the state file blind-state.json.
        Guid nodeId;
        var hub = new DesktopFakeHub("legacy-hub.test");
        {
            var services = new ServiceCollection();
            services.AddSingleton<IBlindSecretStore>(new MacOsKeychainSecretStore(keychain, Service));
            services.AddSingleton<IBlindStateStore>(new MacOsBlindStateStore(paths));
            services.AddSingleton<IBlindLifecycle>(new RecordingLifecycle());
            BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(paths.DataDirectory, paths.DatabasePath, null, () => "Old Mac"));
            await using var provider = services.BuildServiceProvider();
            var app = provider.GetRequiredService<IBlindAppController>();
            await app.InitializeAsync();
            BlindPhoneCode.TryParse(app.PairingCode(), out var phone).Should().BeTrue();
            app.AcceptCallCode(hub.CallCodeFor(phone!.Secret)).Should().BeNull();
            provider.GetRequiredService<BlindPhoneState>().InitialLoadDone = true;
            nodeId = app.GetStatus().NodeId!.Value;
            SqliteConnection.ClearAllPools();
        }
        var itemsBefore = keychain.Items.ToDictionary(i => i.Key, i => i.Value.ToArray());
        var stateBefore = File.ReadAllText(Path.Combine(paths.DataDirectory, "blind-state.json"));
        itemsBefore.Keys.Select(k => k.Item2).Order().Should().Equal("backup-key-v1", "identity-seed-v1");   // the pairing secret was spent when the copy was paired

        // The 2.7 host over the same folder and the same Keychain.
        await using var host = new BlindAccountHost(new BlindAccountHostOptions
        {
            RootDirectory = paths.DataDirectory,
            LegacyDatabasePath = paths.DatabasePath,
            Secrets = new MacOsSecretStoreFactory(keychain, Service),
            States = new MacOsBlindStateStoreFactory(paths.DataDirectory),
            DisplayNameFactory = () => "Old Mac",
            RootLockWait = TimeSpan.FromMilliseconds(200),
            PrimaryHandlerFactory = entry => entry.IsLegacy ? () => hub : null,
        });
        await host.StartAsync();

        var account = host.GetSnapshot().Accounts.Should().ContainSingle().Which;
        account.Entry.Id.Should().Be(BlindAccountEntry.LegacyId);
        account.Status!.NodeId.Should().Be(nodeId, "the same device identity");
        account.Status.StartError.Should().BeNull();
        account.Status.BackupKeyLost.Should().BeFalse("the legacy item names are found where they were");
        account.Status.IsPaired.Should().BeTrue("the pairing survived the upgrade");
        keychain.Items.Keys.Should().BeEquivalentTo(itemsBefore.Keys, "no item was added, renamed or removed");
        foreach (var (key, bytes) in itemsBefore) keychain.Items[key].Should().Equal(bytes, "the items are byte for byte what they were");
        File.ReadAllText(Path.Combine(paths.DataDirectory, "blind-state.json")).Should().Be(stateBefore);
        File.Exists(Path.Combine(paths.DataDirectory, "state.json")).Should().BeFalse("the Mac head's state file keeps its name");
        Directory.Exists(Path.Combine(paths.DataDirectory, "accounts")).Should().BeFalse();

        // It keeps syncing: a round against its hub applies the hub's event, under the same node id.
        hub.AddFolder();
        var legacyServices = host.ServicesFor(BlindAccountEntry.LegacyId);
        await legacyServices.GetRequiredService<BlindStartup>().EnsureReadyAsync();
        await legacyServices.GetRequiredService<IWhitelistRepository>().CreateAsync(hub.WhitelistRow());
        (await host.For(BlindAccountEntry.LegacyId).RequestSyncAsync()).Should().Be("Synced.");
        using (var connection = legacyServices.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        {
            if (connection.State != System.Data.ConnectionState.Open) connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM tbl_folder";
            using var reader = command.ExecuteReader();
            var names = new List<string>();
            while (reader.Read()) names.Add(reader.GetString(0));
            names.Should().Contain(hub.Canary);
        }
        hub.Callers().Should().Contain(nodeId);

        // A second account lives beside it, with its own items and state file; removing it leaves account 1 alone.
        var added = await host.AddAsync("Second");
        added.Ok.Should().BeTrue(added.Error);
        keychain.Items.Keys.Select(k => k.Item2).Should().Contain(added.AccountId + "/identity-seed-v1");
        File.Exists(Path.Combine(paths.DataDirectory, "accounts", added.AccountId!, "blind-state.json")).Should().BeTrue();

        await host.RemoveAsync(added.AccountId!);

        keychain.Items.Keys.Select(k => k.Item2).Should().NotContain(a => a.StartsWith(added.AccountId!), "removing an account deletes its Keychain items");
        keychain.Items.Keys.Should().BeEquivalentTo(itemsBefore.Keys);
        Directory.Exists(Path.Combine(paths.DataDirectory, "accounts", added.AccountId!)).Should().BeFalse();
        host.For(BlindAccountEntry.LegacyId).GetStatus().NodeId.Should().Be(nodeId);
    }
}
