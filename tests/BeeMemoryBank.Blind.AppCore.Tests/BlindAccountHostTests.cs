using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>The account host over the real AppCore composition (real SQLite in a temp folder) and in-memory stores: adding, adopting a 2.6 install, editing, call-code routing, damage.</summary>
public sealed class BlindAccountHostTests : IDisposable
{
    private readonly AccountRig _rig = new();
    private string RegistryFile => Path.Combine(_rig.Root, "accounts.json");

    public void Dispose() => _rig.Dispose();

    private async Task<BlindAccountHost> StartedAsync(int max = BlindAccountHost.DefaultMaxAccounts)
    {
        var host = _rig.Host(maxAccounts: max);
        await host.StartAsync();
        return host;
    }

    private BlindAccountEntry Entry(BlindAccountHost host, string id) => host.GetSnapshot().Accounts.Single(a => a.Entry.Id == id).Entry;

    /// <summary>
    /// Makes sure the account really made its identity: an add can carry a start error of the moment (a fresh database file that would not open under
    /// load) and still return an id — the error lands on the card, not in the add's result. A test that then clears the keys would clear keys that
    /// were never made, and the restarted account would mint a new identity instead of saying its keys are lost. Initializing again is what the
    /// screen does when the app opens (a failed setup run is on the card, and the next open tries again); the bound fails naming the start error.
    /// </summary>
    private static async Task IdentityMadeAsync(BlindAccountHost host, string accountId)
    {
        var controller = host.For(accountId);
        for (var round = 0; round < 3 && controller.GetStatus().NodeId is null; round++)
            await controller.InitializeAsync();
        var status = controller.GetStatus();
        status.NodeId.Should().NotBeNull($"the account was added, but it never made its identity (start error: {status.StartError ?? "none"})");
    }

    // ---- a fresh install and adding ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFreshInstall_WritesAnEmptyRegistry_AndOffersToAddAnAccount()
    {
        await using var host = await StartedAsync();

        var snapshot = host.GetSnapshot();
        snapshot.Accounts.Should().BeEmpty();
        snapshot.RegistryProblem.Should().BeNull();
        snapshot.CanAdd.Should().BeTrue();
        snapshot.MaxAccounts.Should().Be(8);
        File.Exists(RegistryFile).Should().BeTrue("the registry exists from the first start on, so that a later loss of it is noticed");
        Directory.Exists(Path.Combine(_rig.Root, "accounts")).Should().BeFalse();
        File.Exists(_rig.LegacyDb).Should().BeFalse("no copy of the 2.6 kind is made behind the person's back");
    }

    [Fact]
    public async Task AddingAnAccount_MakesItsOwnFolder_Database_Identity_AndKeys()
    {
        await using var host = await StartedAsync();

        var added = await host.AddAsync("Friend's copy");

        added.Ok.Should().BeTrue(added.Error);
        var id = added.AccountId!;
        var entry = Entry(host, id);
        entry.Layout.Should().Be(BlindAccountLayouts.Dir);
        entry.Phase.Should().Be(BlindAccountPhases.Active);
        entry.Name.Should().Be("Friend's copy");
        entry.AddedAt.Should().NotBeNull();
        BlindAccountEntry.IsValidId(id).Should().BeTrue();
        Directory.Exists(Path.Combine(_rig.Root, "accounts", id)).Should().BeTrue();
        File.Exists(Path.Combine(_rig.Root, "accounts", id, "beememorybank.db")).Should().BeTrue();
        File.Exists(_rig.LegacyDb).Should().BeFalse("a new account never writes into the root");

        var status = host.For(id).GetStatus();
        status.StartError.Should().BeNull();
        status.NodeId.Should().NotBeNull();
        status.DisplayName.Should().Be("Test device");
        status.IsPaired.Should().BeFalse();
        status.AwaitingAnswer.Should().BeTrue("the old pairing: this account shows a phone code until the computer answers");
        host.For(id).PairingCode().Should().StartWith("bmb-blind-phone:");
        _rig.Secrets.For(entry).LoadIdentitySeed().Should().NotBeNull("the keys went to the account's own slot in the host's secret store");
    }

    [Fact]
    public async Task TwoAccounts_HaveTwoIdentities_TwoFolders_TwoPairingCodes_AndTwoKeySets()
    {
        await using var host = await StartedAsync();

        var a = (await host.AddAsync("A")).AccountId!;
        var b = (await host.AddAsync("B")).AccountId!;

        a.Should().NotBe(b);
        host.For(a).GetStatus().NodeId!.Value.Should().NotBe(host.For(b).GetStatus().NodeId!.Value);
        host.For(a).PairingCode().Should().NotBe(host.For(b).PairingCode());
        var seedA = _rig.Secrets.For(Entry(host, a)).LoadIdentitySeed();
        var seedB = _rig.Secrets.For(Entry(host, b)).LoadIdentitySeed();
        seedA.Should().NotEqual(seedB);
        _rig.Secrets.For(Entry(host, a)).LoadBackupKey().Should().NotEqual(_rig.Secrets.For(Entry(host, b)).LoadBackupKey());
        _rig.Secrets.For(Entry(host, a)).LoadPairingSecret().Should().NotEqual(_rig.Secrets.For(Entry(host, b)).LoadPairingSecret());
        Directory.GetDirectories(Path.Combine(_rig.Root, "accounts")).Should().HaveCount(2);
        host.GetSnapshot().Accounts.Select(x => x.Entry.Order).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        ReferenceEquals(host.ServicesFor(a), host.ServicesFor(b)).Should().BeFalse("one service provider per account");
        ReferenceEquals(host.Get<BlindHttpClientProvider>(a), host.Get<BlindHttpClientProvider>(b)).Should().BeFalse("one HTTP client and pin registry per account");
        ReferenceEquals(host.Get<BlindPhoneState>(a), host.Get<BlindPhoneState>(b)).Should().BeFalse();
    }

    [Fact]
    public async Task TheCap_RefusesTheAccountOverIt_WithASentence()
    {
        await using var host = await StartedAsync(max: 2);
        (await host.AddAsync("one")).Ok.Should().BeTrue();
        (await host.AddAsync("two")).Ok.Should().BeTrue();
        host.GetSnapshot().CanAdd.Should().BeFalse();

        var third = await host.AddAsync("three");

        third.Ok.Should().BeFalse();
        third.AccountId.Should().BeNull();
        third.Error.Should().Contain("at most 2");
        host.GetSnapshot().Accounts.Should().HaveCount(2);
        Directory.GetDirectories(Path.Combine(_rig.Root, "accounts")).Should().HaveCount(2, "a refused add leaves nothing behind");
    }

    [Fact]
    public async Task TheDefaultCap_IsEight()
    {
        await using var host = await StartedAsync();
        host.MaxAccounts.Should().Be(8);
        for (var i = 0; i < 8; i++) (await host.AddAsync(null)).Ok.Should().BeTrue();
        (await host.AddAsync("ninth")).Error.Should().Contain("at most 8");
        host.GetSnapshot().Accounts.Select(a => a.Entry.Name).Should().Equal(Enumerable.Range(1, 8).Select(i => "Account " + i));
    }

    [Theory]
    [InlineData("a\u0007b")]
    [InlineData("x\ny")]
    public async Task ANameWithControlCharacters_IsRefused(string name)
    {
        await using var host = await StartedAsync();
        var result = await host.AddAsync(name);
        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("control characters");
        host.GetSnapshot().Accounts.Should().BeEmpty();
    }

    [Fact]
    public async Task ATooLongName_IsRefused_AndABlankOneBecomesAccountN()
    {
        await using var host = await StartedAsync();
        (await host.AddAsync(new string('x', 65))).Error.Should().Contain("64");
        var blank = await host.AddAsync("   ");
        blank.Ok.Should().BeTrue();
        Entry(host, blank.AccountId!).Name.Should().Be("Account 1");
    }

    [Fact]
    public async Task AddingBeforeTheStart_IsRefusedPolitely()
    {
        await using var host = _rig.Host();
        var result = await host.AddAsync("early");
        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("starting");
    }

    // ---- editing -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Edits_AreWritten_AndSurviveARestart()
    {
        string a, b;
        await using (var host = await StartedAsync())
        {
            a = (await host.AddAsync("A")).AccountId!;
            b = (await host.AddAsync("B")).AccountId!;
            host.Rename(a, "  Renamed  ").Should().BeNull();
            host.SetLimits(a, new BlindAccountLimits(2L << 30, 200L << 20, 5000)).Should().BeNull();
            host.SetSwitches(a, unmeteredOnly: true, pauseOnPowerSaving: true).Should().BeNull();
            host.SetPaused(b, true).Should().BeNull();
            host.SetDeviceLimits(new BlindDeviceLimits(10L << 30, 1L << 30)).Should().BeNull();
        }

        await using var restarted = await StartedAsync();
        var snapshot = restarted.GetSnapshot();
        var entryA = snapshot.Accounts.Single(x => x.Entry.Id == a).Entry;
        entryA.Name.Should().Be("Renamed");
        entryA.EffectiveLimits.Should().Be(new BlindAccountLimits(2L << 30, 200L << 20, 5000));
        entryA.UnmeteredOnly.Should().BeTrue();
        entryA.PauseOnPowerSaving.Should().BeTrue();
        entryA.PausedByUser.Should().BeFalse();
        snapshot.Accounts.Single(x => x.Entry.Id == b).Entry.PausedByUser.Should().BeTrue();
        snapshot.Device.Should().Be(new BlindDeviceLimits(10L << 30, 1L << 30));
        entryA.Limits.Should().NotBeNull();
    }

    [Fact]
    public async Task ClearingTheLimits_StoresNone_AndZeroStaysZero()
    {
        await using var host = await StartedAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        host.SetLimits(a, new BlindAccountLimits(0, 0, 0)).Should().BeNull();
        Entry(host, a).EffectiveLimits.Should().Be(new BlindAccountLimits(0, 0, 0), "zero means paused, not unlimited");

        host.SetLimits(a, BlindAccountLimits.None).Should().BeNull();
        Entry(host, a).Limits.Should().BeNull();
        Entry(host, a).EffectiveLimits.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task EditsThatAreNotAllowed_ReturnASentence_AndChangeNothing()
    {
        await using var host = await StartedAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var before = File.ReadAllBytes(RegistryFile);

        host.SetLimits(a, new BlindAccountLimits(MaxStoredBytes: -1)).Should().Contain("negative");
        host.SetLimits(a, new BlindAccountLimits(MaxRequestsPerDay: -5)).Should().Contain("negative");
        host.SetDeviceLimits(new BlindDeviceLimits(MaxTrafficBytesPerDay: -1)).Should().Contain("negative");
        host.Rename(a, "").Should().Contain("name");
        host.Rename(a, "bad\u0001").Should().Contain("control");
        host.Rename("0123456789abcdef0123456789abcdef", "x").Should().Contain("no such account");
        host.SetPaused("legacy", true).Should().Contain("no such account");

        File.ReadAllBytes(RegistryFile).Should().Equal(before);
        var act = () => host.For("0123456789abcdef0123456789abcdef");
        act.Should().Throw<KeyNotFoundException>();
    }

    // ---- adopting a 2.6 install --------------------------------------------------------------------------------------------------------------

    private Task<(Guid NodeId, byte[] Seed, FakeBlindHub Hub)> Make26InstallAsync() => Legacy26.MakeAsync(_rig);

    [Fact]
    public async Task Adopting26_WritesExactlyOneFile_AndMovesNothing()
    {
        var install = await Make26InstallAsync();
        var before = AccountRig.Fingerprint(_rig.Root);

        // The process "dies" the moment the adoption has written: what the first start left behind is all that it wrote.
        await using (var host = _rig.Host(crash: new CrashAtStep("adopt:done")))
        {
            var act = () => host.StartAsync();
            await act.Should().ThrowAsync<SimulatedCrash>();
        }

        var after = AccountRig.Fingerprint(_rig.Root);
        after.Keys.Except(before.Keys).Should().Equal("accounts.json");
        before.Keys.Except(after.Keys).Should().BeEmpty("nothing was removed or renamed");
        foreach (var path in before.Keys) after[path].Should().Be(before[path], path + " was not touched");
        Directory.Exists(Path.Combine(_rig.Root, "accounts")).Should().BeFalse();
        _rig.Secrets.For(new BlindAccountEntry("legacy", BlindAccountLayouts.Root, "x")).LoadIdentitySeed().Should().Equal(install.Seed, "no key was re-made");

        // And the next start runs account 1 exactly as 2.6 did: same identity, still paired.
        await using var next = await StartedAsync();
        var card = next.GetSnapshot().Accounts.Should().ContainSingle().Subject;
        card.Entry.Id.Should().Be("legacy");
        card.Entry.Layout.Should().Be(BlindAccountLayouts.Root);
        card.Entry.Name.Should().Be("legacy-hub.test", "the account is named after the computer it calls");
        card.Entry.Phase.Should().Be(BlindAccountPhases.Active);
        card.Entry.EffectiveLimits.IsEmpty.Should().BeTrue("account 1 after the upgrade has no limits");
        card.Entry.UnmeteredOnly.Should().BeFalse();
        card.Status!.NodeId.Should().Be(install.NodeId);
        card.Status.IsPaired.Should().BeTrue();
        card.Status.InitialLoadDone.Should().BeTrue();
        card.Status.Endpoint.Should().Be(install.Hub.Origin);
        card.Status.StartError.Should().BeNull();
        next.For("legacy").GetStatus().NodeId.Should().Be(install.NodeId);
    }

    [Fact]
    public async Task Adopting_IsIdempotent_ARestartDoesNotRewriteTheRegistry()
    {
        await Make26InstallAsync();
        await using (var first = await StartedAsync()) { }
        var bytes = File.ReadAllBytes(RegistryFile);
        var stamp = File.GetLastWriteTimeUtc(RegistryFile);

        await using (var second = await StartedAsync())
        {
            second.GetSnapshot().Accounts.Should().ContainSingle(a => a.Entry.Id == "legacy");
        }

        File.ReadAllBytes(RegistryFile).Should().Equal(bytes);
        File.GetLastWriteTimeUtc(RegistryFile).Should().Be(stamp);
    }

    [Fact]
    public async Task AnAccountAddedAfterTheAdoption_LivesNextToAccountOne_WithoutTouchingIt()
    {
        var install = await Make26InstallAsync();
        await using var host = await StartedAsync();

        var added = await host.AddAsync("Friend");

        added.Ok.Should().BeTrue();
        host.GetSnapshot().Accounts.Select(a => a.Entry.Id).Should().Equal("legacy", added.AccountId);
        host.For("legacy").GetStatus().NodeId.Should().Be(install.NodeId);
        host.For(added.AccountId!).GetStatus().NodeId!.Value.Should().NotBe(install.NodeId);
        File.Exists(Path.Combine(_rig.Root, "accounts", added.AccountId!, "beememorybank.db")).Should().BeTrue();
        File.ReadAllBytes(Path.Combine(_rig.Root, "media", "photo.bin")).Should().Equal([1, 2, 3, 4]);
    }

    [Fact]
    public async Task AStateWithoutADatabase_And_ADatabaseWithoutAState_AreBoth_A26Install()
    {
        var legacy = new BlindAccountEntry(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "x");
        _rig.States.For(legacy).Set("bmb.blind.node_id", Guid.NewGuid().ToString("D"));
        await using (var host = await StartedAsync())
            host.GetSnapshot().Accounts.Should().ContainSingle(a => a.Entry.Id == "legacy", "an identity in the state is a copy");

        using var other = new AccountRig();
        File.WriteAllBytes(other.LegacyDb, [0x53, 0x51, 0x4c]);
        var otherHost = other.Host();
        await using (otherHost)
        {
            await otherHost.StartAsync();
            otherHost.GetSnapshot().Accounts.Should().ContainSingle(a => a.Entry.Id == "legacy", "a database with content is a copy");
        }
    }

    [Fact]
    public async Task AnEmptyDatabaseFile_AndAnEmptyState_AreNotACopy()
    {
        File.WriteAllBytes(_rig.LegacyDb, []);
        await using var host = await StartedAsync();
        host.GetSnapshot().Accounts.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnreadableLegacyState_WritesNoRegistry_AndSaysSo()
    {
        var throwing = new ThrowingStates();
        var options = new BlindAccountHostOptions
        {
            RootDirectory = _rig.Root, LegacyDatabasePath = _rig.LegacyDb, Secrets = _rig.Secrets, States = throwing,
        };
        await using var broken = new BlindAccountHost(options);

        await broken.StartAsync();

        File.Exists(RegistryFile).Should().BeFalse("an empty registry written over an unreadable state would hide the real install");
        var snapshot = broken.GetSnapshot();
        snapshot.RegistryProblem.Should().Contain("could not be read");
        snapshot.CanAdd.Should().BeFalse();
        (await broken.AddAsync("x")).Ok.Should().BeFalse();
    }

    // ---- a damaged registry ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADamagedRegistry_OpensOnASentence_AndTouchesNoAccount()
    {
        string a, b;
        await using (var host = await StartedAsync())
        {
            a = (await host.AddAsync("A")).AccountId!;
            b = (await host.AddAsync("B")).AccountId!;
        }
        SqliteConnection.ClearAllPools();
        File.WriteAllText(RegistryFile, "{ \"version\": 1, \"accounts\": [ {");
        var before = AccountRig.Fingerprint(_rig.Root);

        await using var damaged = await StartedAsync();

        var snapshot = damaged.GetSnapshot();
        snapshot.RegistryProblem.Should().Contain("damaged").And.Contain("Nothing was started or changed");
        snapshot.Accounts.Should().BeEmpty("nothing may be started from a registry that cannot be read");
        snapshot.CanAdd.Should().BeFalse();
        (await damaged.AddAsync("new")).Error.Should().Contain("damaged");
        damaged.Rename(a, "x").Should().Contain("damaged");
        damaged.SetDeviceLimits(new BlindDeviceLimits(1, 1)).Should().Contain("damaged");
        var act = () => damaged.For(b);
        act.Should().Throw<KeyNotFoundException>("no runtime was started");

        var after = AccountRig.Fingerprint(_rig.Root);
        after.Should().BeEquivalentTo(before, "not a file was created, changed or removed: the folders are still there for a person to look at");
        Directory.GetDirectories(Path.Combine(_rig.Root, "accounts")).Should().HaveCount(2);
    }

    [Fact]
    public async Task AMissingRegistry_NextToAccountFolders_IsTreatedAsDamaged_NotStartedEmptyOverThem()
    {
        string a;
        await using (var host = await StartedAsync()) a = (await host.AddAsync("A")).AccountId!;
        SqliteConnection.ClearAllPools();
        File.Move(RegistryFile, RegistryFile + ".saved");

        await using var restarted = await StartedAsync();

        restarted.GetSnapshot().RegistryProblem.Should().Contain("missing");
        File.Exists(RegistryFile).Should().BeFalse("no empty registry was written over the folder");
        Directory.Exists(Path.Combine(_rig.Root, "accounts", a)).Should().BeTrue();
    }

    [Fact]
    public async Task ARegistryFromANewerVersion_IsNotRewrittenByThisOne()
    {
        Directory.CreateDirectory(_rig.Root);
        File.WriteAllText(RegistryFile, "{\"version\":2,\"accounts\":[{\"id\":\"legacy\",\"layout\":\"root\",\"name\":\"x\"}],\"future\":true}");
        var before = File.ReadAllBytes(RegistryFile);

        await using var host = await StartedAsync();

        host.GetSnapshot().RegistryProblem.Should().Contain("another version");
        (await host.AddAsync("x")).Ok.Should().BeFalse();
        File.ReadAllBytes(RegistryFile).Should().Equal(before);
    }

    // ---- one account does not stop the others ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAccountWhoseKeysAreGone_SaysSo_AndTheOthersRun_AndNoKeyIsMadeUpForIt()
    {
        string a, b;
        BlindAccountEntry entryA;
        await using (var host = await StartedAsync())
        {
            a = (await host.AddAsync("A")).AccountId!;
            b = (await host.AddAsync("B")).AccountId!;
            entryA = Entry(host, a);
            await IdentityMadeAsync(host, a);
        }
        SqliteConnection.ClearAllPools();
        _rig.Secrets.For(entryA).Clear();

        await using var restarted = await StartedAsync();

        var card = restarted.For(a).GetStatus();
        card.BackupKeyLost.Should().BeTrue(
            "the card says its keys are missing: remove the account and add it again (start error: {0}, keystore: {1}, node: {2})",
            card.StartError ?? "none", card.KeyStoreUnavailable ?? "none", card.NodeId?.ToString() ?? "none");
        restarted.For(a).GetStatus().KeyStoreUnavailable.Should().BeNull("a missing key is not a store that does not answer");
        _rig.Secrets.For(entryA).LoadIdentitySeed().Should().BeNull("a missing key never mints a new one over existing data");
        _rig.Secrets.For(entryA).LoadBackupKey().Should().BeNull();
        restarted.For(b).GetStatus().StartError.Should().BeNull();
        restarted.For(b).GetStatus().NodeId.Should().NotBeNull();
        restarted.GetSnapshot().Accounts.Should().HaveCount(2);
    }

    [Fact]
    public async Task AnAccountThatCannotBeBuilt_GetsACardSentence_AndTheOthersStillStart()
    {
        string a, b;
        await using (var host = await StartedAsync())
        {
            a = (await host.AddAsync("A")).AccountId!;
            b = (await host.AddAsync("B")).AccountId!;
        }
        SqliteConnection.ClearAllPools();
        var folderA = Path.Combine(_rig.Root, "accounts", a);
        Directory.Delete(folderA, recursive: true);
        File.WriteAllText(folderA, "a file where the folder should be");

        await using var restarted = await StartedAsync();

        var cards = restarted.GetSnapshot().Accounts;
        cards.Single(c => c.Entry.Id == a).Problem.Should().Contain("Needs repair").And.Contain("taken by a file");
        cards.Single(c => c.Entry.Id == a).Status.Should().BeNull();
        cards.Single(c => c.Entry.Id == b).Problem.Should().BeNull();
        restarted.For(b).GetStatus().NodeId.Should().NotBeNull();
        var act = () => restarted.For(a);
        act.Should().Throw<KeyNotFoundException>();
    }

    // ---- the computer's answer ---------------------------------------------------------------------------------------------------------------

    private string CodeFor(BlindAccountHost host, string accountId, FakeBlindHub hub) =>
        hub.CallCodeFor(_rig.Secrets.For(Entry(host, accountId)).LoadPairingSecret()!);

    [Fact]
    public async Task ACallCode_GoesToTheAccountWhoseSecretMadeIt_AndToNoOtherThatAwaitsAnAnswer()
    {
        await using var host = await StartedAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var b = (await host.AddAsync("B")).AccountId!;
        var hubB = new FakeBlindHub("hub-b.test");

        host.AcceptCallCode(CodeFor(host, b, hubB)).Should().BeNull();

        host.For(b).GetStatus().IsPaired.Should().BeTrue();
        host.For(b).GetStatus().Endpoint.Should().Be(hubB.Origin);
        host.For(a).GetStatus().IsPaired.Should().BeFalse("account A's secret did not make this code");
        host.For(a).GetStatus().AwaitingAnswer.Should().BeTrue("A still waits for its own answer");
        host.For(b).GetStatus().AwaitingAnswer.Should().BeFalse("B's secret is spent");
    }

    [Fact]
    public async Task ACallCode_MadeForNoAccount_IsRefused_AndNobodyIsTouched()
    {
        await using var host = await StartedAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var b = (await host.AddAsync("B")).AccountId!;
        var stranger = new FakeBlindHub("stranger.test").CallCodeFor(new byte[32]);

        host.AcceptCallCode(stranger).Should().Contain("not made for any account");

        host.For(a).GetStatus().IsPaired.Should().BeFalse();
        host.For(b).GetStatus().IsPaired.Should().BeFalse();
        host.For(a).GetStatus().AwaitingAnswer.Should().BeTrue();
        host.For(b).GetStatus().AwaitingAnswer.Should().BeTrue();
    }

    [Fact]
    public async Task ACallCode_GivenToTheWrongAccountsOwnController_IsRefusedToo()
    {
        await using var host = await StartedAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var b = (await host.AddAsync("B")).AccountId!;

        host.For(a).AcceptCallCode(CodeFor(host, b, new FakeBlindHub("hub-b.test"))).Should().Contain("not made for this phone");

        host.For(a).GetStatus().IsPaired.Should().BeFalse();
    }

    [Fact]
    public async Task AGarbledCode_AndNoAccountWaiting_HaveTheirOwnSentences()
    {
        await using var host = await StartedAsync();
        host.AcceptCallCode("bmb-blind-call:?nonsense").Should().Contain("not a connection code");
        var a = (await host.AddAsync("A")).AccountId!;
        var hub = new FakeBlindHub("hub.test");
        host.AcceptCallCode(CodeFor(host, a, hub)).Should().BeNull();

        host.AcceptCallCode(hub.CallCodeFor(new byte[32])).Should().Contain("No account on this device is waiting");
    }

    [Fact]
    public async Task ARePairingAccount_AwaitsAnAnswerAgain_AndAcceptsOnlyItsNewSecret()
    {
        await using var host = await StartedAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var b = (await host.AddAsync("B")).AccountId!;
        var first = new FakeBlindHub("first.test");
        host.AcceptCallCode(CodeFor(host, a, first)).Should().BeNull();
        host.AcceptCallCode(CodeFor(host, b, new FakeBlindHub("b.test"))).Should().BeNull();

        var oldSecret = _rig.Secrets.For(Entry(host, a)).LoadPairingSecret();
        oldSecret.Should().BeNull("spent");
        host.For(a).StartRePair();
        var second = new FakeBlindHub("second.test");

        host.AcceptCallCode(CodeFor(host, a, second)).Should().BeNull();
        host.For(a).GetStatus().Endpoint.Should().Be(second.Origin);
        host.For(b).GetStatus().Endpoint.Should().Be("https://b.test:5610");
    }

    [Fact]
    public async Task AnAcceptedCode_SaysWhichAccountTookIt_EvenWhenSeveralPairedAccountsWaitForANewAnswerAtOnce()
    {
        await using var host = await StartedAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var b = (await host.AddAsync("B")).AccountId!;
        host.AcceptCallCode(CodeFor(host, a, new FakeBlindHub("a1.test"))).Should().BeNull();
        host.AcceptCallCode(CodeFor(host, b, new FakeBlindHub("b1.test"))).Should().BeNull();
        host.For(a).StartRePair();
        host.For(b).StartRePair();
        // Both are paired and both wait for a new answer: "the first waiting account that is paired" is A, whoever the code was made for.
        host.For(a).GetStatus().Should().Match<BlindAppStatus>(s => s.IsPaired && s.AwaitingAnswer);
        host.For(b).GetStatus().Should().Match<BlindAppStatus>(s => s.IsPaired && s.AwaitingAnswer);
        var newHub = new FakeBlindHub("b2.test");

        var result = host.AcceptCallCodeWithAccount(CodeFor(host, b, newHub));

        result.Ok.Should().BeTrue();
        result.AccountId.Should().Be(b, "the account whose own pairing secret made the code took it");
        host.For(b).GetStatus().Endpoint.Should().Be(newHub.Origin);
        host.For(a).GetStatus().AwaitingAnswer.Should().BeTrue("A still waits for its own answer");
        host.For(a).GetStatus().Endpoint.Should().Be("https://a1.test:5610");
    }

    [Fact]
    public async Task ARefusedCode_NamesNoAccount_AndTheSentenceIsTheOldOne()
    {
        await using var host = await StartedAsync();
        await host.AddAsync("A");

        var refused = host.AcceptCallCodeWithAccount(new FakeBlindHub("stranger.test").CallCodeFor(new byte[32]));

        refused.Ok.Should().BeFalse();
        refused.AccountId.Should().BeNull();
        refused.Error.Should().Be(host.AcceptCallCode(new FakeBlindHub("stranger.test").CallCodeFor(new byte[32])));
        host.AcceptCallCodeWithAccount("bmb-blind-call:?nonsense").Error.Should().Contain("not a connection code");
    }

    // ---- wiping one account ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DisconnectAndWipe_OfOneAccount_StartsThatAccountOverAndLeavesTheOtherAlone()
    {
        await using var host = await StartedAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var b = (await host.AddAsync("B")).AccountId!;
        var nodeA = host.For(a).GetStatus().NodeId;
        var nodeB = host.For(b).GetStatus().NodeId;
        var seedA = _rig.Secrets.For(Entry(host, a)).LoadIdentitySeed();
        var seedB = _rig.Secrets.For(Entry(host, b)).LoadIdentitySeed();
        var folderB = AccountRig.Fingerprint(Path.Combine(_rig.Root, "accounts", b));
        folderB.Should().NotBeEmpty();

        await host.For(a).DisconnectAndWipeAsync();

        // The host builds a fresh runtime for A in the background: a new identity and a new phone code.
        await WaitAsync(() => host.For(a).GetStatus().NodeId is { } id && id != nodeA);
        host.For(a).GetStatus().NodeId!.Value.Should().NotBe(nodeA!.Value);
        _rig.Secrets.For(Entry(host, a)).LoadIdentitySeed().Should().NotEqual(seedA);
        host.GetSnapshot().Accounts.Select(c => c.Entry.Id).Should().Contain([a, b], "the account is still there; it is a first-run copy again");
        host.For(b).GetStatus().NodeId.Should().Be(nodeB);
        _rig.Secrets.For(Entry(host, b)).LoadIdentitySeed().Should().Equal(seedB);
        host.For(b).GetStatus().StartError.Should().BeNull();
        (await host.For(b).RequestSyncAsync()).Should().Be("Not paired yet.", "B's runtime still answers after A's wipe");
    }

    [Fact]
    public async Task TheWipe_NeedsTheFolderOfTheCopy_ThereIsNoFallbackToTheAppsDataFolder()
    {
        var act = () => BlindPhoneReset.WipeAsync(new ServiceCollection().BuildServiceProvider());
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*folder of the copy*");
    }

    [Fact]
    public async Task TheWipe_DeletesTheBaselinesToo_AndUsesTheFolderTheCompositionWasBuiltFor()
    {
        var folder = Path.Combine(_rig.Root, "wipe-me");
        Directory.CreateDirectory(Path.Combine(folder, "baselines", "current"));
        File.WriteAllBytes(Path.Combine(folder, "baselines", "current", "baseline.bin"), [1]);
        File.WriteAllText(Path.Combine(folder, "usage.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "unrelated.txt"), "not ours");
        var services = new ServiceCollection();
        services.AddSingleton<IBlindPaths>(new TestPaths(folder));
        services.AddSingleton<IBlindPhoneKeys>(_ => new InMemoryBlindSecretStoreFactory().For(new BlindAccountEntry("legacy", BlindAccountLayouts.Root, "x")));
        services.AddSingleton<IBlindLifecycle, NoopLifecycle>();
        services.AddSingleton(new BlindPhoneState(new InMemoryBlindStateStoreFactory().For(new BlindAccountEntry("legacy", BlindAccountLayouts.Root, "x"))));

        await BlindPhoneReset.WipeAsync(services.BuildServiceProvider());

        Directory.Exists(Path.Combine(folder, "baselines")).Should().BeFalse("the sealed baseline is part of the copy");
        File.Exists(Path.Combine(folder, "usage.json")).Should().BeFalse();
        File.Exists(Path.Combine(folder, "unrelated.txt")).Should().BeTrue("only what the copy owns is deleted");
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------------------------

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the condition did not become true");
            await Task.Delay(25);
        }
    }

    private sealed class TestPaths(string folder) : IBlindPaths
    {
        public string DataDirectory => folder;
        public string DatabasePath => Path.Combine(folder, "beememorybank.db");
    }

    private sealed class CrashAtStep(string step) : IBlindCrashPoints
    {
        public void Reach(string reached)
        {
            if (reached == step) throw new SimulatedCrash(step);
        }
    }

    private sealed class ThrowingStates : IBlindStateStoreFactory
    {
        public IBlindStateStore For(BlindAccountEntry account) => new Throwing();

        private sealed class Throwing : IBlindStateStore
        {
            public string? Get(string key) => throw new UnauthorizedAccessException("the preferences are locked");
            public void Set(string key, string? value) => throw new UnauthorizedAccessException("the preferences are locked");
        }
    }
}
