using System.Text.Json;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// The repair state and its explicit actions: what the host lists when the registry and the disk disagree, the three calls that settle it (adopt the 2.6 root, adopt a folder,
/// forget an account whose folder is gone), their refusals, their atomicity, and the marker that tells an account that was set up from one that never was.
/// </summary>
public sealed class BlindAccountRepairTests : IDisposable
{
    private readonly AccountRig _rig = new();

    private string AccountsDir => Path.Combine(_rig.Root, "accounts");
    private string RegistryFile => Path.Combine(_rig.Root, "accounts.json");

    public void Dispose() => _rig.Dispose();

    private async Task<BlindAccountHost> StartedAsync(BlindAccountHost? host = null)
    {
        host ??= _rig.Host();
        await host.StartAsync();
        return host;
    }

    private static BlindAccountEntry Dir(string id, string name = "x", int order = 0) => new(id, BlindAccountLayouts.Dir, name, Order: order);

    private async Task<(string A, string B)> TwoAccountsStoppedAsync()
    {
        string a, b;
        await using (var host = await StartedAsync())
        {
            a = (await host.AddAsync("A")).AccountId!;
            b = (await host.AddAsync("B")).AccountId!;
        }
        SqliteConnection.ClearAllPools();
        return (a, b);
    }

    /// <summary>A 2.6 install in the root and a registry that lists only a pending account B: account 1 is on disk and the list omits it.</summary>
    private async Task<(Guid NodeId, byte[] Seed, string B)> LegacyOmittedAsync()
    {
        var (node, seed, _) = await Legacy26.MakeAsync(_rig);
        var b = BlindAccountEntry.NewId();
        new BlindAccountRegistry(_rig.Root).Update(_ => new BlindAccountsFile(Accounts: [Dir(b, "B", 1)]));
        return (node, seed, b);
    }

    // ---- the marker --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheEstablishedMarker_IsWrittenOnceTheIdentityIsMade_AndForTheAdoptedCopy()
    {
        await Legacy26.MakeAsync(_rig);
        await using var host = await StartedAsync();
        var id = (await host.AddAsync("A")).AccountId!;

        var entries = host.GetSnapshot().Accounts.ToDictionary(c => c.Entry.Id, c => c.Entry);

        entries["legacy"].Established.Should().BeTrue("a copy found on disk has an identity by definition");
        entries[id].Established.Should().BeTrue("the identity was made at the start");
        var json = JsonDocument.Parse(File.ReadAllText(RegistryFile)).RootElement.GetProperty("accounts");
        json.EnumerateArray().Should().OnlyContain(e => e.GetProperty("established").GetBoolean());
    }

    [Fact]
    public async Task AnAccountThatCouldNotMakeItsIdentity_IsStillOnRecordAsSetUp_BecauseTheRecordComesBeforeTheIdentity()
    {
        await using var host = _rig.Host(secrets: new RefusingSeed(_rig.Secrets));
        await host.StartAsync();

        await host.AddAsync("A");

        var card = host.GetSnapshot().Accounts.Single();
        card.Status!.StartError.Should().NotBeNull("the key store refused the identity seed");
        card.Entry.Established.Should().BeTrue("the record is written once the folder exists and before an identity can be made in it: a start that fails half way must not leave a window in which a lost folder would be made again over the keys");
    }

    private sealed class RefusingSeed(IBlindSecretStoreFactory inner) : IBlindSecretStoreFactory
    {
        public IBlindSecretStore For(BlindAccountEntry account) => new Store(inner.For(account));

        private sealed class Store(IBlindSecretStore real) : IBlindSecretStore
        {
            public void SaveIdentitySeed(byte[] seed) => throw new IOException("the key store is not answering");
            public byte[]? LoadIdentitySeed() => real.LoadIdentitySeed();
            public void SaveBackupKey(byte[] key) => real.SaveBackupKey(key);
            public byte[]? LoadBackupKey() => real.LoadBackupKey();
            public void SavePairingSecret(byte[] secret) => real.SavePairingSecret(secret);
            public byte[]? LoadPairingSecret() => real.LoadPairingSecret();
            public void ClearPairingSecret() => real.ClearPairingSecret();
            public void Clear() => real.Clear();
        }
    }

    // ---- the repair state --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheRepairState_ListsWhatIsWrong_WithTheKindAndTheAccount_AndTheViewCarriesIt()
    {
        var (_, _, b) = await LegacyOmittedAsync();
        var orphan = BlindAccountEntry.NewId();
        Directory.CreateDirectory(Path.Combine(AccountsDir, orphan));
        File.WriteAllText(Path.Combine(AccountsDir, orphan, "data.txt"), "x");

        await using var host = await StartedAsync();

        var snapshot = host.GetSnapshot();
        snapshot.RegistryProblem.Should().Contain("do not agree");
        snapshot.CanAdd.Should().BeFalse();
        snapshot.RepairIssues.Select(i => (i.Kind, i.AccountId)).Should().BeEquivalentTo(
        [
            (BlindRepairKinds.LegacyRootNotListed, (string?)null),
            (BlindRepairKinds.FolderNotListed, orphan),
        ]);
        snapshot.RepairIssues.Should().OnlyContain(i => i.CanAdopt && !i.CanForget && i.Description.Length > 20);
        var view = BlindAccountsView.Build(snapshot, TimeZoneInfo.Utc, DateTimeOffset.UtcNow);
        view.Repairs.Should().HaveCount(2);
        view.Problem.Should().NotBeNull();
        var started = () => host.ServicesFor(b);
        started.Should().Throw<KeyNotFoundException>("nothing starts in a repair state");
    }

    [Fact]
    public async Task OneListedAccountThatNeedsRepair_DoesNotStopTheOthers()
    {
        var (a, b) = await TwoAccountsStoppedAsync();
        Directory.Delete(Path.Combine(AccountsDir, a), recursive: true);

        await using var host = await StartedAsync();

        var snapshot = host.GetSnapshot();
        snapshot.RegistryProblem.Should().BeNull("the list itself is not in doubt: one account lost its folder");
        snapshot.CanAdd.Should().BeTrue();
        var issue = snapshot.RepairIssues.Should().ContainSingle().Subject;
        (issue.Kind, issue.AccountId, issue.CanForget, issue.CanAdopt).Should().Be((BlindRepairKinds.FolderMissing, a, true, false));
        snapshot.Accounts.Single(c => c.Entry.Id == a).Problem.Should().Contain("Needs repair");
        host.For(b).GetStatus().NodeId.Should().NotBeNull("B runs");
    }

    [Fact]
    public async Task AnAdoptedInstallThatWasRemoved_LeavesNoRepairStateAtTheNextStart()
    {
        await Legacy26.MakeAsync(_rig);
        await using (var host = await StartedAsync())
        {
            host.GetSnapshot().Accounts.Should().ContainSingle(c => c.Entry.Id == "legacy");
            await host.RemoveAsync("legacy");
        }
        SqliteConnection.ClearAllPools();

        await using var next = await StartedAsync();

        next.GetSnapshot().RegistryProblem.Should().BeNull();
        next.GetSnapshot().RepairIssues.Should().BeEmpty("a removed copy leaves nothing in the root for the list to omit");
        next.GetSnapshot().Accounts.Should().BeEmpty();
    }

    // ---- adopting the 2.6 root ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AdoptingTheLegacyRoot_ListsItFirst_StartsIt_MovesNothing_AndEndsTheRepairState()
    {
        var (node, seed, b) = await LegacyOmittedAsync();
        var media = File.ReadAllBytes(Path.Combine(_rig.Root, "media", "photo.bin"));
        var backup = File.ReadAllBytes(Path.Combine(_rig.Root, "blind-backups", "backup-1.bmbandroid"));
        await using var host = await StartedAsync();
        host.GetSnapshot().RepairIssues.Should().ContainSingle(i => i.Kind == BlindRepairKinds.LegacyRootNotListed);
        var changed = 0;
        host.Changed += () => Interlocked.Increment(ref changed);

        var result = await host.AdoptLegacyRootAsync();

        result.Should().BeNull();
        changed.Should().BeGreaterThan(0);
        var snapshot = host.GetSnapshot();
        snapshot.RegistryProblem.Should().BeNull();
        snapshot.RepairIssues.Should().BeEmpty();
        snapshot.Accounts.Select(c => c.Entry.Id).Should().Equal("legacy", b);
        snapshot.Accounts[0].Entry.Established.Should().BeTrue();
        snapshot.Accounts[0].Status!.NodeId.Should().Be(node, "the copy's own identity, not a new one");
        _rig.Secrets.For(snapshot.Accounts[0].Entry).LoadIdentitySeed().Should().Equal(seed);
        File.ReadAllBytes(Path.Combine(_rig.Root, "media", "photo.bin")).Should().Equal(media);
        File.ReadAllBytes(Path.Combine(_rig.Root, "blind-backups", "backup-1.bmbandroid")).Should().Equal(backup);
        host.For(b).GetStatus().NodeId.Should().NotBeNull("the other account started once nothing was left to settle");
        snapshot.CanAdd.Should().BeTrue();
    }

    [Fact]
    public async Task AdoptingTheLegacyRoot_WhenThereIsNothingToAdopt_ChangesNothing()
    {
        var (a, _) = await TwoAccountsStoppedAsync();
        await using var host = await StartedAsync();
        var before = File.ReadAllBytes(RegistryFile);

        var result = await host.AdoptLegacyRootAsync();

        result.Should().Contain("nothing to adopt");
        File.ReadAllBytes(RegistryFile).Should().Equal(before);
        host.For(a).GetStatus().NodeId.Should().NotBeNull();
    }

    [Fact]
    public async Task AnAdoptionThatDiesInTheWrite_LeavesTheRegistryAsItWas_AndTheNextStartAsksAgain()
    {
        await LegacyOmittedAsync();
        var crash = new ArmedCrash("registry:tmp-written");
        var dying = _rig.Host(crash: crash);
        await dying.StartAsync();
        var before = File.ReadAllBytes(RegistryFile);
        crash.Armed = true;

        var act = () => dying.AdoptLegacyRootAsync();
        await act.Should().ThrowAsync<SimulatedCrash>();
        await dying.DisposeAsync();
        SqliteConnection.ClearAllPools();

        File.ReadAllBytes(RegistryFile).Should().Equal(before, "the rename never happened: the list is as it was");
        await using var next = await StartedAsync();
        next.GetSnapshot().RepairIssues.Should().ContainSingle(i => i.Kind == BlindRepairKinds.LegacyRootNotListed);
        (await next.AdoptLegacyRootAsync()).Should().BeNull();
        next.GetSnapshot().Accounts.Select(c => c.Entry.Id).Should().Contain("legacy");
    }

    // ---- adopting a folder -------------------------------------------------------------------------------------------------------------------

    /// <summary>Two accounts, stopped, and then A struck out of the list: A's folder, keys and state are all still there, and the list no longer names it.</summary>
    private async Task<(string A, byte[] Seed, Guid Node)> OrphanedAsync()
    {
        var (a, _) = await TwoAccountsStoppedAsync();
        var entry = Dir(a);
        var seed = _rig.Secrets.For(entry).LoadIdentitySeed()!;
        var node = new BlindPhoneState(_rig.States.For(entry)).NodeId!.Value;
        new BlindAccountRegistry(_rig.Root).Update(file => file with { Accounts = file.Entries.Where(e => e.Id != a).ToList() });
        return (a, seed, node);
    }

    [Fact]
    public async Task AdoptingAFolder_ListsItAsAnAccount_StartsIt_AndItKeepsItsOwnIdentity()
    {
        var (a, seed, node) = await OrphanedAsync();
        await using var host = await StartedAsync();
        host.GetSnapshot().RepairIssues.Should().ContainSingle(i => i.Kind == BlindRepairKinds.FolderNotListed && i.AccountId == a);

        var result = await host.AdoptFolderAsync(a, "Found copy");

        result.Should().BeNull();
        var snapshot = host.GetSnapshot();
        snapshot.RegistryProblem.Should().BeNull();
        snapshot.RepairIssues.Should().BeEmpty();
        var card = snapshot.Accounts.Single(c => c.Entry.Id == a);
        card.Entry.Name.Should().Be("Found copy");
        card.Entry.Established.Should().BeTrue("the folder holds an account that was set up");
        card.Status!.NodeId.Should().Be(node);
        _rig.Secrets.For(card.Entry).LoadIdentitySeed().Should().Equal(seed);
    }

    [Fact]
    public async Task AnEmptyFolderThatIsAdopted_GetsTheDefaultName_AndIsSetUpByItsStart()
    {
        await TwoAccountsStoppedAsync();
        var empty = BlindAccountEntry.NewId();
        Directory.CreateDirectory(Path.Combine(AccountsDir, empty));
        await using var host = await StartedAsync();

        (await host.AdoptFolderAsync(empty)).Should().BeNull();

        var entry = host.GetSnapshot().Accounts.Single(c => c.Entry.Id == empty).Entry;
        entry.Name.Should().Be("Recovered copy");
        entry.Established.Should().BeTrue("by the time the adoption finished its start, the account had made its identity");
    }

    [Fact]
    public async Task AdoptingAFolder_IsRefusedWhenItIsNotWaiting_IsNotAnAccountFolder_OrTheDeviceIsFull()
    {
        var (a, _) = await TwoAccountsStoppedAsync();
        var orphan = BlindAccountEntry.NewId();
        Directory.CreateDirectory(Path.Combine(AccountsDir, orphan));
        await using var host = await StartedAsync(_rig.Host(maxAccounts: 2));
        var before = File.ReadAllBytes(RegistryFile);

        (await host.AdoptFolderAsync(a)).Should().Contain("not waiting");
        (await host.AdoptFolderAsync(BlindAccountEntry.NewId())).Should().Contain("not waiting");
        (await host.AdoptFolderAsync("legacy")).Should().Contain("not the folder of an account");
        (await host.AdoptFolderAsync("..")).Should().Contain("not the folder of an account");
        (await host.AdoptFolderAsync(orphan, new string('x', 200))).Should().Contain("longer than");
        (await host.AdoptFolderAsync(orphan)).Should().Contain("at most 2 accounts");

        File.ReadAllBytes(RegistryFile).Should().Equal(before, "no refusal changed the list");
        host.GetSnapshot().RepairIssues.Should().ContainSingle(i => i.AccountId == orphan);
    }

    [Fact]
    public async Task ALinkNamedLikeAnAccount_IsListedAsUnsafe_NeverAdopted_AndNeverFollowed()
    {
        var (a, _) = await TwoAccountsStoppedAsync();
        var elsewhere = Path.Combine(_rig.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "precious.txt"), "not an account's");
        var name = BlindAccountEntry.NewId();
        if (!TestLinks.TryCreateDirectoryLink(Path.Combine(AccountsDir, name), elsewhere)) return;
        await using var host = await StartedAsync();

        var issue = host.GetSnapshot().RepairIssues.Should().ContainSingle().Subject;
        (issue.Kind, issue.AccountId, issue.CanAdopt).Should().Be((BlindRepairKinds.UnsafePath, name, false));
        (await host.AdoptFolderAsync(name)).Should().Contain("not waiting");
        var services = () => host.ServicesFor(a);
        services.Should().Throw<KeyNotFoundException>("an unlisted link is a repair state like any other: nothing starts");
        File.ReadAllText(Path.Combine(elsewhere, "precious.txt")).Should().Be("not an account's");
        TestLinks.IsLink(Path.Combine(AccountsDir, name)).Should().BeTrue();
    }

    // ---- forgetting an account whose folder is gone ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ForgettingAMissingAccount_TakesTheEntryOut_ClearsItsKeysAndState_AndNothingElse()
    {
        var (a, b) = await TwoAccountsStoppedAsync();
        var entryA = Dir(a);
        var seedB = _rig.Secrets.For(Dir(b)).LoadIdentitySeed();
        Directory.Delete(Path.Combine(AccountsDir, a), recursive: true);
        await using var host = await StartedAsync();
        host.GetSnapshot().RepairIssues.Should().ContainSingle(i => i.Kind == BlindRepairKinds.FolderMissing && i.AccountId == a);
        _rig.Secrets.BlobCount(entryA).Should().BeGreaterThan(0, "until the person acts, the keys are where they were");

        var result = await host.ForgetMissingAccountAsync(a);

        result.Should().BeNull();
        var snapshot = host.GetSnapshot();
        snapshot.Accounts.Select(c => c.Entry.Id).Should().Equal(b);
        snapshot.RepairIssues.Should().BeEmpty();
        _rig.Secrets.BlobCount(entryA).Should().Be(0);
        _rig.States.HasState(entryA).Should().BeFalse();
        _rig.Secrets.For(Dir(b)).LoadIdentitySeed().Should().Equal(seedB);
        Directory.Exists(Path.Combine(AccountsDir, a)).Should().BeFalse();
    }

    [Fact]
    public async Task ForgettingIsRefused_WhenTheFolderIsThereAgain_OrTheAccountIsNotWaiting()
    {
        var (a, b) = await TwoAccountsStoppedAsync();
        var folderA = Path.Combine(AccountsDir, a);
        Directory.Delete(folderA, recursive: true);
        await using var host = await StartedAsync();
        var before = File.ReadAllBytes(RegistryFile);

        Directory.CreateDirectory(folderA); // a restored backup: the folder came back after the start
        (await host.ForgetMissingAccountAsync(a)).Should().Contain("there again");
        (await host.ForgetMissingAccountAsync(b)).Should().Contain("not waiting");
        (await host.ForgetMissingAccountAsync(BlindAccountEntry.NewId())).Should().Contain("not waiting");
        (await host.ForgetMissingAccountAsync("legacy")).Should().Contain("not waiting");

        File.ReadAllBytes(RegistryFile).Should().Equal(before);
        _rig.Secrets.BlobCount(Dir(a)).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AFailureToClearTheKeys_AfterTheEntryIsGone_IsToldAndLeavesNoHalfAccount()
    {
        var (a, b) = await TwoAccountsStoppedAsync();
        Directory.Delete(Path.Combine(AccountsDir, a), recursive: true);
        var failing = new FailingClear(_rig.Secrets);
        await using var host = await StartedAsync(_rig.Host(secrets: failing));

        var result = await host.ForgetMissingAccountAsync(a);

        result.Should().Contain("was forgotten").And.Contain("could not be cleared");
        host.GetSnapshot().Accounts.Select(c => c.Entry.Id).Should().Equal(b);
        host.GetSnapshot().RepairIssues.Should().BeEmpty();
    }

    private sealed class FailingClear(IBlindSecretStoreFactory inner) : IBlindSecretStoreFactory
    {
        public IBlindSecretStore For(BlindAccountEntry account) => new Store(inner.For(account));

        private sealed class Store(IBlindSecretStore real) : IBlindSecretStore
        {
            public void SaveIdentitySeed(byte[] seed) => real.SaveIdentitySeed(seed);
            public byte[]? LoadIdentitySeed() => real.LoadIdentitySeed();
            public void SaveBackupKey(byte[] key) => real.SaveBackupKey(key);
            public byte[]? LoadBackupKey() => real.LoadBackupKey();
            public void SavePairingSecret(byte[] secret) => real.SavePairingSecret(secret);
            public byte[]? LoadPairingSecret() => real.LoadPairingSecret();
            public void ClearPairingSecret() => real.ClearPairingSecret();
            public void Clear() => throw new IOException("the key store is not answering");
        }
    }

    // ---- refusals while something else is wrong ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task RepairActions_AreRefused_WhileTheRegistryIsDamaged_OrAnotherCopyHoldsTheFolder()
    {
        await Legacy26.MakeAsync(_rig);
        await using (var first = await StartedAsync())
        {
            await using var second = _rig.Host();
            await second.StartAsync();

            (await second.AdoptLegacyRootAsync()).Should().Contain("Another copy");
            (await second.AdoptFolderAsync(BlindAccountEntry.NewId())).Should().Contain("Another copy");
            (await second.ForgetMissingAccountAsync(BlindAccountEntry.NewId())).Should().Contain("Another copy");
        }
        SqliteConnection.ClearAllPools();
        File.WriteAllText(RegistryFile, "{ not json");

        await using var damaged = await StartedAsync();

        (await damaged.AdoptLegacyRootAsync()).Should().Contain("damaged");
        (await damaged.AdoptFolderAsync(BlindAccountEntry.NewId())).Should().Contain("damaged");
        (await damaged.ForgetMissingAccountAsync(BlindAccountEntry.NewId())).Should().Contain("damaged");
        File.ReadAllText(RegistryFile).Should().Be("{ not json", "a damaged registry is never written over");
    }

    [Fact]
    public async Task RepairActions_AreRefusedBeforeTheHostHasStarted()
    {
        await using var host = _rig.Host();

        (await host.AdoptLegacyRootAsync()).Should().Contain("still starting");
        (await host.AdoptFolderAsync(BlindAccountEntry.NewId())).Should().Contain("still starting");
        (await host.ForgetMissingAccountAsync(BlindAccountEntry.NewId())).Should().Contain("still starting");
    }
}
