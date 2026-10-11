using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Infrastructure.AutoUnlock;
using BeeMemoryBank.Infrastructure.OsAutoUnlock;
using BeeMemoryBank.Infrastructure.Secrets;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>
/// Red tests 15.2.3 and 15.2.4 for the writers of unlock material that live in the libraries (docs/design/step-b-spec.md 3.6, 4.3): after
/// each operation the boot doors equal the sealed set and the trigger version moved. The Web endpoint and the CLI command of the agents are
/// in the Integration and Cli test projects; the rotation is in <see cref="SealedRotationTests"/>.
/// </summary>
public class SealedWritersTests : SealedWriterFixture
{
    private async Task<int> SlotOf(User user) => (await UserRepo.GetByIdAsync(user.Id))!.KeySlotId!.Value;

    [Fact]
    public async Task TheBootDoorsEqualTheSealedSetBeforeAnyWriter()
    {
        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Should().ContainSingle(d => d.SlotType == "user");
    }

    [Fact]
    public async Task CreatingASuperadminAddsItsDoor()
    {
        var before = await SealedVersionAsync();

        await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);

        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Where(d => d.SlotType == "user").Should().HaveCount(2);
        (await SealedVersionAsync()).Should().BeGreaterThan(before, "the trigger of migration 040 moved the version");
    }

    [Fact]
    public async Task CreatingAnOrdinaryUserAddsNoDoor()
    {
        var doorsBefore = (await Doors.ListAsync()).Count;

        await Users.CreateUserAsync("bob", "Bob", "BobPass1x", UserRoles.User);

        (await Doors.ListAsync()).Should().HaveCount(doorsBefore);
        await AssertDoorsEqualSealedSetAsync();
    }

    [Fact]
    public async Task ReplacingAPasswordReplacesTheDoor_AndTheOldCopyIsGone()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var oldSlot = await SlotOf(carol);
        var before = await SealedVersionAsync();

        await Users.AdminChangePasswordAsync(carol.Id, "CarolPass2");

        var newSlot = await SlotOf(carol);
        newSlot.Should().NotBe(oldSlot);
        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Should().NotContain(d => d.Kind == BootDoorKinds.Slot && d.SourceId == oldSlot, "the old password's door is retired");
        (await Doors.ListAsync()).Should().Contain(d => d.Kind == BootDoorKinds.Slot && d.SourceId == newSlot);
        (await SealedVersionAsync()).Should().BeGreaterThan(before);
    }

    [Fact]
    public async Task ChangingYourOwnPasswordReplacesTheDoor()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var oldSlot = await SlotOf(carol);

        await Users.ChangePasswordAsync(carol.Id, "CarolPass1", "CarolPass3");

        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Should().NotContain(d => d.SourceId == oldSlot && d.Kind == BootDoorKinds.Slot);
    }

    [Fact]
    public async Task DeletingASuperadminRetiresTheirDoor()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var slot = await SlotOf(carol);

        await Users.DeleteUserAsync(carol.Id);

        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Should().NotContain(d => d.SourceId == slot && d.Kind == BootDoorKinds.Slot);
    }

    [Fact]
    public async Task DemotingASuperadminRetiresTheirSlotAndTheirAgentsDoors()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var slot = await SlotOf(carol);
        var agent = await CreateWrappedAgentAsync(carol.Id, "carols-agent");
        await Mirror.RewriteAsync();
        (await Doors.ListAsync()).Should().Contain(d => d.Kind == BootDoorKinds.Agent && d.SourceId == agent);

        await Users.UpdateUserAsync(carol.Id, "Carol", UserRoles.User);

        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Should().NotContain(d => d.Kind == BootDoorKinds.Slot && d.SourceId == slot);
        (await Doors.ListAsync()).Should().NotContain(d => d.Kind == BootDoorKinds.Agent && d.SourceId == agent, "a demoted user's agents lose their wrapped DEK");
    }

    [Fact]
    public async Task ProvisioningTheSlotOfAPromotedUserAtLoginAddsItsDoor()
    {
        var bob = await Users.CreateUserAsync("bob", "Bob", "BobPass1x", UserRoles.User);
        await Users.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin);
        var promoted = (await UserRepo.GetByIdAsync(bob.Id))!;
        promoted.KeySlotId.Should().BeNull("promotion cannot build the slot, it has no password");

        (await Users.ProvisionMissingKeySlotAsync(promoted, "BobPass1x")).Should().BeTrue();

        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Should().Contain(d => d.Kind == BootDoorKinds.Slot && d.SourceId == promoted.KeySlotId!.Value);
    }

    [Fact]
    public async Task TheLegacyPasswordChangeReplacesTheDoorInOneTransaction()
    {
        var oldSlot = (await KeySlots.GetAllAsync()).Single().SlotId;
        var before = await SealedVersionAsync();

        await Keys.ChangePasswordAsync(AdminPassword, "AdminPass2");

        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Should().NotContain(d => d.Kind == BootDoorKinds.Slot && d.SourceId == oldSlot);
        (await SealedVersionAsync()).Should().BeGreaterThan(before);
    }

    [Fact]
    public async Task ARecoveryKeyIsADoorWhenItIsCreated_AndWhenItIsRemoved()
    {
        await Keys.AddRecoveryKeyAsync();
        await AssertDoorsEqualSealedSetAsync();
        var recovery = (await Doors.ListAsync()).Single(d => d.SlotType == "recovery");
        recovery.Salt.Should().NotBeNull();

        await Keys.RemoveSlotAsync((int)recovery.SourceId);

        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Should().NotContain(d => d.SlotType == "recovery");
    }

    [Fact]
    public async Task RegeneratingTheRecoveryKeyLeavesOneRecoveryDoor()
    {
        await Keys.AddRecoveryKeyAsync();
        var first = (await Doors.ListAsync()).Single(d => d.SlotType == "recovery");

        // The endpoint's regenerate: the old recovery slot goes, a new one is created.
        await Keys.RemoveSlotAsync((int)first.SourceId);
        await Keys.AddRecoveryKeyAsync();

        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Single(d => d.SlotType == "recovery").SourceId.Should().NotBe(first.SourceId);
    }

    [Fact]
    public async Task APasswordSlotAddedByKeyManagementIsADoor()
    {
        await Keys.AddPasswordSlotAsync("user", "SecondPass1");

        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Where(d => d.SlotType == "user").Should().HaveCount(2);
    }

    [Fact]
    public async Task ADoorWrittenByAnUnforeseenWriterIsMirroredByTheWatcher()
    {
        // Raw SQL, no mirror call: the trigger moves the version and the watcher notices (spec 4.3 backstop).
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync(
                @"INSERT INTO tbl_key_slot (slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism, created_at)
                  VALUES ('user', zeroblob(49), zeroblob(12), zeroblob(32), 65536, 3, 4, 'now')");
        (await Doors.ListAsync()).Should().HaveCount(1, "the boot store has not been told");

        var watcher = new UnlockMaterialWatcher(Mirror, Gate);
        (await watcher.TickAsync()).Should().BeTrue();

        await AssertDoorsEqualSealedSetAsync();
    }

    [Fact]
    public async Task APlainNodeWritesNoBootRowsThroughTheWriters()
    {
        Gate.IsSealed = false;
        var before = (await Doors.ListAsync()).Select(d => d.ContentSha256).ToList();

        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        await Users.AdminChangePasswordAsync(carol.Id, "CarolPass2");
        await Keys.AddRecoveryKeyAsync();
        await Users.DeleteUserAsync(carol.Id);

        (await Doors.ListAsync()).Select(d => d.ContentSha256).Should().Equal(before, "a plain node never touches the boot store through the writers");
    }

    // --- 15.2.4: kill points of the write protocol (spec 4.3 table) ---------------------------------------------------------

    [Fact]
    public async Task AFailureAfterThePendingDoorRollsTheSlotBack_AndTheNextRewriteDropsTheDoor()
    {
        // The sealed insert is rolled back when the pending-door write throws; the node keeps exactly the doors it had.
        var failing = new FailingMirror(Mirror, failOn: nameof(IUnlockMaterialMirror.AddPendingDoorAsync));
        var users = new UserService(UserRepo, KeySlots, Session, Roles, ServiceFor(), agentRepo: AgentRepo, factory: Factory, mirror: failing);
        var slotsBefore = (await KeySlots.GetAllAsync()).Count;

        var act = async () => await users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await KeySlots.GetAllAsync()).Should().HaveCount(slotsBefore, "the sealed transaction did not commit");
        await AssertDoorsEqualSealedSetAsync();
    }

    [Fact]
    public async Task ACrashAfterTheSealedCommitAndBeforeTheRewriteLeavesBothDoors_AndTheOldPasswordStillOpens()
    {
        // addition/replacement row of the table in 4.3: old doors + the pending new one; never a lock-out.
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var oldSlot = await SlotOf(carol);
        var oldDoor = (await Doors.ListAsync()).Single(d => d.SourceId == oldSlot && d.Kind == BootDoorKinds.Slot);

        // The writer got as far as the pending door of the new password and the sealed commit; the process died before the rewrite.
        var newSlot = new MasterKeyStore { SlotType = "user", EncryptedMasterDek = [1, 2, 3], IV = [4, 5, 6], Salt = oldDoor.Salt,
            ArgonMemory = 65536, ArgonIterations = 3, ArgonParallelism = 4, CreatedAt = DateTime.UtcNow };
        await SlotDoors.CreateSlotAsync(KeySlots, Factory, Mirror, newSlot, null);
        // (CreateSlotAsync committed and left the pending copy: the rewrite is the caller's job and never ran.)

        var doors = await Doors.ListAsync();
        doors.Should().Contain(d => d.SourceId == oldSlot, "the old password's door is still there");
        doors.Should().Contain(d => d.SourceId == newSlot.SlotId && d.Pending);

        await Mirror.RewriteAsync();   // the next unlock/watcher tick

        (await Doors.ListAsync()).Should().OnlyContain(d => !d.Pending);
        await AssertDoorsEqualSealedSetAsync();
    }

    [Fact]
    public async Task ARemovalWhoseSealedDeleteFailsPutsTheDoorBack()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var slot = await SlotOf(carol);
        var failingRepo = new ThrowingDeleteSlots(KeySlots);

        var act = async () => await SlotDoors.DeleteSlotAsync(failingRepo, Mirror, slot);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await Doors.ListAsync()).Should().Contain(d => d.SourceId == slot && d.Kind == BootDoorKinds.Slot, "rule 2 ran first, the rewrite after the failure copied the door back");
        await AssertDoorsEqualSealedSetAsync();
    }

    [Fact]
    public async Task ARemovalRetiresTheDoorBeforeTheSealedDeleteCommits()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var slot = await SlotOf(carol);
        var observed = new List<bool>();
        var observing = new ObservingDeleteSlots(KeySlots, async () =>
            observed.Add((await Doors.ListAsync()).Any(d => d.SourceId == slot && d.Kind == BootDoorKinds.Slot)));

        await SlotDoors.DeleteSlotAsync(observing, Mirror, slot);

        observed.Should().Equal(false);   // at the moment of the sealed delete the boot door was already gone
    }

    // --- OS auto-unlock (11.1): the slot is a door --------------------------------------------------------------------------

    [Fact]
    public async Task OsAutoUnlockIsADoorWhileItIsOn_AndNotAfterItIsTurnedOff()
    {
        var dir = Path.Combine(BootDir, "data");
        Directory.CreateDirectory(dir);
        var service = new OsAutoUnlockService(KeySlots, Session, dir, new InMemoryUserSecretStore());
        service.AttachSealedNode(Factory, Mirror, null);

        await service.EnableAsync();
        await AssertDoorsEqualSealedSetAsync();
        var door = (await Doors.ListAsync()).Single(d => d.SlotType == "os_auto_unlock");
        door.Salt.Should().BeNull();

        // Turned on again: one slot, one door, the new copy.
        await service.EnableAsync();
        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Where(d => d.SlotType == "os_auto_unlock").Should().ContainSingle().Which.SourceId.Should().NotBe(door.SourceId);

        (await service.DisableAsync()).Should().BeTrue();
        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Should().NotContain(d => d.SlotType == "os_auto_unlock");
    }

    [Fact]
    public async Task OsAutoUnlockOnAClosedSealedNodeReadsTheBootDoorAndUnlocksThroughTheSealedPath()
    {
        var dir = Path.Combine(BootDir, "data");
        Directory.CreateDirectory(dir);
        var secrets = new InMemoryUserSecretStore();
        var service = new OsAutoUnlockService(KeySlots, Session, dir, secrets);
        service.AttachSealedNode(Factory, Mirror, null);
        await service.EnableAsync();
        var dek = Session.GetMasterDek();

        // A restarted process: a locked session, the database closed, the door in the boot store.
        var host = new FakeSealedHost(Doors) { Usable = false };
        var restarted = new OsAutoUnlockService(KeySlots, new SessionService(KeySlots), dir, secrets);
        restarted.AttachSealedNode(Factory, Mirror, host);

        (await restarted.IsEnabledAsync()).Should().BeTrue("the status reads the boot door while the database is closed");
        (await restarted.TryAutoUnlockAsync(new NodeIdentityRepository(Factory))).Should().BeTrue();

        host.Unlocked.Should().ContainSingle();
        host.Unlocked[0].Dek.Should().Equal(dek);
        host.Unlocked[0].Door!.SlotType.Should().Be("os_auto_unlock");
        host.Fingerprints.Should().ContainSingle().Which.Fingerprint.Should().Be(DekFingerprint.Of(dek));
    }

    [Fact]
    public async Task OsAutoUnlockOnAClosedSealedNodeFailsClosedWhenTheSealedUnlockRefuses()
    {
        var dir = Path.Combine(BootDir, "data");
        Directory.CreateDirectory(dir);
        var secrets = new InMemoryUserSecretStore();
        var service = new OsAutoUnlockService(KeySlots, Session, dir, secrets);
        service.AttachSealedNode(Factory, Mirror, null);
        await service.EnableAsync();

        var host = new FakeSealedHost(Doors) { Usable = false, Accept = false };
        var restarted = new OsAutoUnlockService(KeySlots, new SessionService(KeySlots), dir, secrets);
        restarted.AttachSealedNode(Factory, Mirror, host);

        (await restarted.TryAutoUnlockAsync(new NodeIdentityRepository(Factory))).Should().BeFalse();
        host.Fingerprints.Should().BeEmpty();

        // No host registered at all (the unlock delegate is missing): a sealed node stays locked.
        var withoutHost = new OsAutoUnlockService(KeySlots, new SessionService(KeySlots), dir, secrets);
        withoutHost.AttachSealedNode(Factory, Mirror, new SealedAutoUnlockHost(Gate, Doors, null));
        Gate.State = VaultState.Closed;
        (await withoutHost.TryAutoUnlockAsync(new NodeIdentityRepository(Factory))).Should().BeFalse();
    }

    [Fact]
    public async Task TheUpdateHandoffOnAClosedSealedNodeGoesThroughTheSealedUnlock()
    {
        var dir = Path.Combine(BootDir, "data");
        Directory.CreateDirectory(dir);
        var secrets = new InMemoryUserSecretStore();
        var now = DateTime.UtcNow;
        var handoff = new UpdateUnlockHandoff(Session, dir, () => now, secrets);
        (await handoff.WriteAsync()).Should().BeTrue();
        var dek = Session.GetMasterDek();

        var host = new FakeSealedHost(Doors) { Usable = false };
        var restarted = new UpdateUnlockHandoff(new SessionService(KeySlots), dir, () => now.AddMinutes(1), secrets);
        restarted.AttachSealedNode(host);

        (await restarted.TryConsumeAsync(new NodeIdentityRepository(Factory))).Should().BeTrue();

        host.Unlocked.Should().ContainSingle();
        host.Unlocked[0].Dek.Should().Equal(dek);
        host.Unlocked[0].Door.Should().BeNull("the hand-off is not a door (spec 4.1)");
        secrets.Read("update-unlock", "default").Should().BeNull("one use");
    }

    // --- helpers ---------------------------------------------------------------------------------------------------------

    private FolderAccessService ServiceFor() => new(new ServiceCollection()
        .AddSingleton<IDbConnectionFactory>(_ => Factory)
        .AddScoped<IUserRepository>(_ => UserRepo)
        .AddScoped<IRoleRepository>(_ => Roles)
        .AddScoped<IRoleAclRepository>(_ => new RoleAclRepository(Factory))
        .AddScoped<IFolderAclRepository>(_ => new FolderAclRepository(Factory))
        .AddScoped<IFolderRepository>(_ => new FolderRepository(Factory, ScopeHolder))
        .AddScoped(_ => ScopeHolder)
        .BuildServiceProvider());

    private sealed class FailingMirror(IUnlockMaterialMirror inner, string failOn) : IUnlockMaterialMirror
    {
        public Task RewriteAsync(CancellationToken ct = default) => inner.RewriteAsync(ct);
        public Task AddPendingDoorAsync(PendingDoor door, CancellationToken ct = default) =>
            failOn == nameof(AddPendingDoorAsync) ? throw new InvalidOperationException("boot store unavailable") : inner.AddPendingDoorAsync(door, ct);
        public Task RetireDoorAsync(string kind, long sourceId, CancellationToken ct = default) => inner.RetireDoorAsync(kind, sourceId, ct);
        public Task AddDbKeyWrapperAsync(byte[] masterDek, CancellationToken ct = default) => inner.AddDbKeyWrapperAsync(masterDek, ct);
        public Task WriteNewBootStoreAsync(string bootPath, IDbConnectionFactory vault, byte[] databaseKey, int dbkGeneration,
            IReadOnlyList<byte[]> masterDeks, CancellationToken ct = default) => inner.WriteNewBootStoreAsync(bootPath, vault, databaseKey, dbkGeneration, masterDeks, ct);
    }

    private class SlotsDecorator(IKeySlotRepository inner) : IKeySlotRepository
    {
        public virtual Task<List<MasterKeyStore>> GetAllAsync() => inner.GetAllAsync();
        public virtual Task<int> CreateAsync(MasterKeyStore slot, System.Data.IDbTransaction? transaction = null) => inner.CreateAsync(slot, transaction);
        public virtual Task DeleteAsync(int slotId, System.Data.IDbTransaction? transaction = null) => inner.DeleteAsync(slotId, transaction);
        public virtual Task UpdateSlotKeyAsync(int slotId, byte[] encryptedDek, byte[] iv) => inner.UpdateSlotKeyAsync(slotId, encryptedDek, iv);
        public virtual Task EnsureOsAutoUnlockUniqueIndexAsync() => inner.EnsureOsAutoUnlockUniqueIndexAsync();
    }

    private sealed class ThrowingDeleteSlots(IKeySlotRepository inner) : SlotsDecorator(inner)
    {
        public override Task DeleteAsync(int slotId, System.Data.IDbTransaction? transaction = null) => throw new InvalidOperationException("sealed delete failed");
    }

    private sealed class ObservingDeleteSlots(IKeySlotRepository inner, Func<Task> observe) : SlotsDecorator(inner)
    {
        public override async Task DeleteAsync(int slotId, System.Data.IDbTransaction? transaction = null)
        {
            await observe();
            await base.DeleteAsync(slotId, transaction);
        }
    }

    internal sealed class FakeSealedHost(IBootDoorStore doors) : ISealedAutoUnlockHost
    {
        public bool Usable { get; set; }
        public bool Accept { get; set; } = true;
        public List<(byte[] Dek, BootDoor? Door)> Unlocked { get; } = [];
        public List<(long DoorId, string Fingerprint)> Fingerprints { get; } = [];
        public bool IsSealed => true;
        public bool IsUsable => Usable;
        public async Task<IReadOnlyList<BootDoor>> ListDoorsAsync(CancellationToken ct = default) => await doors.ListAsync(ct);
        public Task<bool> TryUnlockWithDekAsync(byte[] masterDek, BootDoor? door, CancellationToken ct = default)
        {
            Unlocked.Add(((byte[])masterDek.Clone(), door));
            return Task.FromResult(Accept);
        }
        public Task SetDekFingerprintAsync(long doorId, string dekFingerprint, CancellationToken ct = default)
        {
            Fingerprints.Add((doorId, dekFingerprint));
            return Task.CompletedTask;
        }
    }
}
