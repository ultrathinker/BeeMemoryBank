using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Infrastructure.OsAutoUnlock;
using BeeMemoryBank.Infrastructure.Secrets;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Vault.Sealed;
using Dapper;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>
/// Door format v2 (docs/adr/0012-sealed-doors.md) through the real writers of a node whose boot store the mirror keeps: every writer makes a v2
/// door that names its DEK; a v1 door of the OS auto-unlock, of an auto-unlock agent and of a superadmin's sign-in becomes a v2 door when its
/// secret is used; the quick-unlock door's small API; and the wrapper pruning rule (only what the doors need when every door names its DEK,
/// every candidate while one does not).
/// </summary>
public class DoorUpgradeTests : SealedWriterFixture
{
    private async Task<MasterKeyStore> SlotAsync(int slotId) => (await KeySlots.GetAllAsync()).Single(s => s.SlotId == slotId);

    private async Task<int> AdminSlotAsync() => (await UserRepo.GetByUsernameAsync("admin"))!.KeySlotId!.Value;

    private static void ShouldBeV2(MasterKeyStore slot, byte[] dek, string because)
    {
        slot.DoorVersion.Should().Be(2, because);
        slot.DoorPublicKey.Should().HaveCount(DoorBox.PublicKeySize, because);
        slot.EncryptedMasterDek.Should().HaveCount(DoorBox.BoxSize, because);
        slot.DekFingerprint.Should().Be(DekFingerprint.Of(dek), because);
    }

    /// <summary>Rewrites a slot as the v1 wrap of an earlier release (same salt), the DEK fingerprint unknown.</summary>
    private async Task MakeV1Async(int slotId, byte[] kek)
    {
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(Session.GetMasterDek(), kek);
        using var c = Factory.CreateConnection();
        await c.ExecuteAsync(
            "UPDATE tbl_key_slot SET encrypted_master_dek = @wrapped, iv = @iv, door_version = 1, door_public_key = NULL, dek_fp = NULL WHERE slot_id = @slotId",
            new { wrapped, iv, slotId });
    }

    private async Task MakeV1WithPasswordAsync(int slotId, string password)
    {
        var slot = await SlotAsync(slotId);
        var kek = KeyDerivation.DeriveKek(password, slot.Salt!);
        await MakeV1Async(slotId, kek);
        CryptographicOperations.ZeroMemory(kek);
    }

    // ---- the writers ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryWriterOfAPasswordDoorWritesASealedDoorThatNamesItsDek_AndItsOwner()
    {
        var dek = Session.GetMasterDek();
        ShouldBeV2(await SlotAsync(await AdminSlotAsync()), dek, "the first door of a node");

        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var carolSlot = await SlotAsync(carol.KeySlotId!.Value);
        ShouldBeV2(carolSlot, dek, "a new superadmin");
        carolSlot.OwnerUserId.Should().Be(carol.Id, "the database makes the person tbl_user points at the owner");

        await Users.ChangePasswordAsync(carol.Id, "CarolPass1", "CarolPass2");
        carol = (await UserRepo.GetByIdAsync(carol.Id))!;
        var changed = await SlotAsync(carol.KeySlotId!.Value);
        ShouldBeV2(changed, dek, "a password change");
        changed.OwnerUserId.Should().Be(carol.Id);

        // A recovery key is personal (2.12): issued for carol, it names her as its owner.
        var recoveryKey = await Keys.AddRecoveryKeyAsync(carol.Id);
        var recovery = (await KeySlots.GetAllAsync()).Single(s => s.SlotType == KeySlotTypes.Recovery);
        ShouldBeV2(recovery, dek, "a new recovery key");
        recovery.OwnerUserId.Should().Be(carol.Id);

        var reset = await Users.ResetPasswordWithRecoveryKeyAsync("carol", recoveryKey, "CarolPass3");
        reset.Outcome.Should().Be(RecoveryResetOutcome.Reset);
        carol = (await UserRepo.GetByIdAsync(carol.Id))!;
        ShouldBeV2(await SlotAsync(carol.KeySlotId!.Value), dek, "a recovery reset");

        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Where(d => d.Kind == BootDoorKinds.Slot).Should().OnlyContain(d => d.DoorVersion == 2);

        Session.Lock();
        (await Session.UnlockAsync("CarolPass3")).Should().BeTrue("the sealed door opens with its password");
    }

    [Fact]
    public async Task AV1RecoveryDoor_BecomesAV2DoorWhenARecoveryResetUsesIt()
    {
        var recoveryKey = await Keys.AddRecoveryKeyAsync();
        var recovery = (await KeySlots.GetAllAsync()).Single(s => s.SlotType == KeySlotTypes.Recovery);
        await MakeV1WithPasswordAsync(recovery.SlotId, recoveryKey);
        (await SlotAsync(recovery.SlotId)).DoorVersion.Should().Be(1);

        (await Users.ResetPasswordWithRecoveryKeyAsync("admin", recoveryKey, "AdminPass2")).Outcome.Should().Be(RecoveryResetOutcome.Reset);

        ShouldBeV2(await SlotAsync(recovery.SlotId), Session.GetMasterDek(), "the recovery key's first use");
        await Mirror.RewriteAsync();   // the session of this fixture has no mirror in its scope: the watcher's rewrite
        await AssertDoorsEqualSealedSetAsync();
    }

    [Fact]
    public async Task AV1SuperadminDoor_BecomesAV2DoorAtASignInWhileTheNodeIsOpen_AndANeverUsedOneStillOpens()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        await MakeV1WithPasswordAsync(admin.KeySlotId!.Value, AdminPassword);
        await Mirror.RewriteAsync();
        (await Doors.ListAsync()).Single(d => d.SourceId == admin.KeySlotId).DoorVersion.Should().Be(1);

        Session.Lock();
        (await Session.UnlockAsync(AdminPassword)).Should().BeTrue("a v1 door that was never used opens");
        ShouldBeV2(await SlotAsync(admin.KeySlotId!.Value), Session.GetMasterDek(), "the unlock that used it seals it again");

        await MakeV1WithPasswordAsync(admin.KeySlotId!.Value, AdminPassword);
        (await Users.UpgradeKeySlotDoorAsync(admin, "a wrong password")).Should().BeFalse();
        (await SlotAsync(admin.KeySlotId!.Value)).DoorVersion.Should().Be(1, "a wrong password changes nothing");
        (await Users.UpgradeKeySlotDoorAsync(admin, AdminPassword)).Should().BeTrue();
        ShouldBeV2(await SlotAsync(admin.KeySlotId!.Value), Session.GetMasterDek(), "the sign-in of a superadmin on an open node");
        (await Users.UpgradeKeySlotDoorAsync(admin, AdminPassword)).Should().BeFalse("a v2 door that names its DEK is left alone");
        await AssertDoorsEqualSealedSetAsync();
    }

    // ---- OS auto-unlock -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OsAutoUnlock_WritesAV2Door_UpgradesAV1DoorAfterAnUnlock_AndStillUnlocks()
    {
        var dir = Path.Combine(BootDir, "data");
        Directory.CreateDirectory(dir);
        var secrets = new InMemoryUserSecretStore();
        var service = new OsAutoUnlockService(KeySlots, Session, dir, secrets);
        service.AttachSealedNode(Factory, Mirror, null);
        await service.EnableAsync();
        var slot = (await KeySlots.GetAllAsync()).Single(s => s.SlotType == KeySlotTypes.OsAutoUnlock);
        ShouldBeV2(slot, Session.GetMasterDek(), "a new OS auto-unlock door");

        var secret = secrets.Read("os-auto-unlock", "default")!;
        await MakeV1Async(slot.SlotId, secret);
        await Mirror.RewriteAsync();
        (await SlotAsync(slot.SlotId)).DoorVersion.Should().Be(1);

        await service.UpgradeAsync();

        ShouldBeV2(await SlotAsync(slot.SlotId), Session.GetMasterDek(), "the upgrade as soon as the node is open and the secret readable");
        await AssertDoorsEqualSealedSetAsync();
        var restarted = new SessionService(KeySlots);
        (await new OsAutoUnlockService(KeySlots, restarted, dir, secrets).TryAutoUnlockAsync(new NodeIdentityRepository(Factory))).Should().BeTrue();
        restarted.GetMasterDek().Should().Equal(Session.GetMasterDek());
    }

    // ---- auto-unlock agents --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AV1AutoUnlockAgent_BecomesAV2DoorAtItsFirstUse_AndAWrongKeyChangesNothing()
    {
        var dek = Session.GetMasterDek();
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var (cipher, iv, salt) = AgentKeyHelper.EncryptDekV1(apiKey, dek);
        var id = await AgentRepo.CreateAsync(new Agent
        {
            Name = "old", KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey), KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey), EncryptedDek = cipher, DekIV = iv,
            Salt = salt, KdfVersion = 1, Status = "A", OwnerUserId = admin.Id, CreatedAt = DateTime.UtcNow,
        });
        await Mirror.RewriteAsync();
        var agent = (await AgentRepo.GetByIdAsync(id))!;

        (await AgentDoors.UpgradeAsync(Factory, Mirror, agent, AgentKeyHelper.GenerateApiKey(), dek)).Should().BeFalse();
        (await AgentRepo.GetByIdAsync(id))!.KdfVersion.Should().Be(1, "another key opens nothing and changes nothing");

        (await AgentDoors.UpgradeAsync(Factory, Mirror, agent, apiKey, dek)).Should().BeTrue();

        var upgraded = (await AgentRepo.GetByIdAsync(id))!;
        upgraded.KdfVersion.Should().Be(Agent.SealedDoorKdfVersion);
        upgraded.DoorPublicKey.Should().HaveCount(DoorBox.PublicKeySize);
        upgraded.EncryptedDek.Should().HaveCount(DoorBox.BoxSize);
        upgraded.DekFingerprint.Should().Be(DekFingerprint.Of(dek));
        Doors_OpenAgent(upgraded, apiKey).Should().Equal(dek);
        await AssertDoorsEqualSealedSetAsync();
        var door = (await Doors.FindAgentAsync(upgraded.KeyHash)).Single();
        door.DoorVersion.Should().Be(2);
        AgentDoors.OpenBootDoor(door, apiKey).Should().Equal(dek, "the boot copy opens with the same key");
        (await AgentDoors.UpgradeAsync(Factory, Mirror, upgraded, apiKey, dek)).Should().BeFalse("a v2 door is left alone");
    }

    private static byte[] Doors_OpenAgent(Agent agent, string apiKey) => BeeMemoryBank.Crypto.Doors.OpenAgent(agent, apiKey);

    // ---- quick unlock ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AQuickUnlockDoorIsCreatedOpenedAndRemoved_FromA32ByteSecret_AndNoPasswordPathTriesIt()
    {
        var dek = Session.GetMasterDek();
        var secret = QuickUnlockDoors.NewSecret();
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;

        var slotId = await QuickUnlockDoors.CreateAsync(KeySlots, Factory, Mirror, secret, dek, admin.Id, "Ann's iPhone");

        var slot = await SlotAsync(slotId);
        slot.SlotType.Should().Be(KeySlotTypes.QuickUnlock);
        ShouldBeV2(slot, dek, "a quick-unlock door");
        slot.Salt.Should().BeNull("no password path ever tries it");
        slot.OwnerUserId.Should().Be(admin.Id);
        slot.Label.Should().Be("Ann's iPhone");
        await AssertDoorsEqualSealedSetAsync();

        (await QuickUnlockDoors.TryOpenAsync(KeySlots, slotId, secret)).Should().Equal(dek);
        (await QuickUnlockDoors.TryOpenAsync(KeySlots, slotId, QuickUnlockDoors.NewSecret())).Should().BeNull();
        (await QuickUnlockDoors.TryOpenAsync(KeySlots, await AdminSlotAsync(), secret)).Should().BeNull("only a quick-unlock slot is opened here");
        var boot = await QuickUnlockDoors.TryOpenBootDoorAsync(Doors, slotId, secret);
        boot!.Value.MasterDek.Should().Equal(dek);
        ((Func<Task>)(() => QuickUnlockDoors.TryOpenAsync(KeySlots, slotId, new byte[16]))).Should().ThrowAsync<ArgumentException>().Wait();

        (await QuickUnlockDoors.RemoveAsync(KeySlots, Mirror, slotId)).Should().BeTrue();
        (await KeySlots.GetAllAsync()).Should().NotContain(s => s.SlotId == slotId);
        await AssertDoorsEqualSealedSetAsync();
        (await QuickUnlockDoors.RemoveAsync(KeySlots, Mirror, slotId)).Should().BeFalse();
    }

    // ---- the wrapper pruning rule ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePruningRule_KeepsOnlyTheWrappersTheDoorsName_AndEveryCandidateWhileOneDoorIsUnknown()
    {
        var oldDek = Session.GetMasterDek();
        var newDek = RandomNumberGenerator.GetBytes(32);
        Session.SwapMasterDek((byte[])newDek.Clone());   // the session now holds both: the new one current, the old one retired
        var (oldFp, newFp) = (DekFingerprint.Of(oldDek), DekFingerprint.Of(newDek));
        var admin = await SlotAsync(await AdminSlotAsync());
        admin.DekFingerprint.Should().Be(oldFp);

        // The admin's door still holds the old DEK and says so: both wrappers are needed.
        await Mirror.RewriteAsync();
        (await Wrappers.ListAsync()).Select(w => w.DekFingerprint).Should().BeEquivalentTo([oldFp, newFp]);

        // The door is sealed to the new DEK (as a rewrap does): every door names the new DEK, so the old wrapper goes, although the session
        // still holds the old DEK.
        var kek = KeyDerivation.DeriveKek(AdminPassword, admin.Salt!);
        (await SlotDoors.ReplaceSlotDoorAsync(KeySlots, Mirror, BeeMemoryBank.Crypto.Doors.WithDoor(admin,
            BeeMemoryBank.Crypto.Doors.Seal(admin.SlotType, kek, admin.Salt, newDek)), admin.EncryptedMasterDek)).Should().BeTrue();
        (await Wrappers.ListAsync()).Select(w => w.DekFingerprint).Should().Equal([newFp], "with every fingerprint known only the needed wrappers remain");

        // A door whose DEK is unknown (a v1 wrap of an earlier release): every candidate is kept again, as before 2.12.
        var (osWrapped, osIv) = MasterKeyManager.WrapMasterDek(oldDek, RandomNumberGenerator.GetBytes(32));
        await SlotDoors.CreateSlotAsync(KeySlots, Factory, Mirror,
            new MasterKeyStore { SlotType = KeySlotTypes.OsAutoUnlock, EncryptedMasterDek = osWrapped, IV = osIv, CreatedAt = DateTime.UtcNow }, null);
        await Mirror.RewriteAsync();
        (await Wrappers.ListAsync()).Select(w => w.DekFingerprint).Should().BeEquivalentTo([oldFp, newFp]);

        // An unknown door on a node that holds no DEK (closing): nothing is pruned.
        Session.Lock();
        await Mirror.RewriteAsync();
        (await Wrappers.ListAsync()).Select(w => w.DekFingerprint).Should().BeEquivalentTo([oldFp, newFp]);
    }

    [Fact]
    public async Task AV2DoorAndAV2AgentWithAnUnknownDek_NameItAtTheirFirstUse_InTheBootStoreToo_AndTheOldWrapperIsPruned()
    {
        // the fingerprint back-fill of a door that is already v2 must reach the boot store (a rewrite), or the boot door keeps
        // dek_fp NULL and the wrappers of older keys are never pruned.
        var oldDek = Session.GetMasterDek();
        var newDek = RandomNumberGenerator.GetBytes(32);
        Session.SwapMasterDek((byte[])newDek.Clone());
        var (oldFp, newFp) = (DekFingerprint.Of(oldDek), DekFingerprint.Of(newDek));
        var admin = await SlotAsync(await AdminSlotAsync());
        var kek = KeyDerivation.DeriveKek(AdminPassword, admin.Salt!);
        (await SlotDoors.ReplaceSlotDoorAsync(KeySlots, Mirror, BeeMemoryBank.Crypto.Doors.WithDoor(admin,
            BeeMemoryBank.Crypto.Doors.Seal(admin.SlotType, kek, admin.Salt, newDek)), admin.EncryptedMasterDek)).Should().BeTrue();
        CryptographicOperations.ZeroMemory(kek);

        var secret = QuickUnlockDoors.NewSecret();
        var quick = await QuickUnlockDoors.CreateAsync(KeySlots, Factory, Mirror, secret, newDek);
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var agent = new Agent
        {
            Name = "bot", KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey), KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey), Status = "A",
            OwnerUserId = (await UserRepo.GetByUsernameAsync("admin"))!.Id, CreatedAt = DateTime.UtcNow,
        };
        AgentDoors.SealInto(agent, apiKey, newDek);
        var agentId = await AgentRepo.CreateAsync(agent);
        using (var c = Factory.CreateConnection())
        {
            await c.ExecuteAsync("UPDATE tbl_key_slot SET dek_fp = NULL WHERE slot_id = @quick", new { quick });
            await c.ExecuteAsync("UPDATE tbl_agent SET dek_fp = NULL WHERE id = @agentId", new { agentId });
        }
        await Mirror.RewriteAsync();
        (await Wrappers.ListAsync()).Select(w => w.DekFingerprint).Should().BeEquivalentTo([oldFp, newFp], "two doors do not name their DEK");

        (await SlotDoors.UpgradeOpenedSlotAsync(KeySlots, Mirror, await SlotAsync(quick), secret, newDek)).Should().BeFalse("a v2 door is not replaced");
        (await AgentDoors.UpgradeAsync(Factory, Mirror, (await AgentRepo.GetByIdAsync(agentId))!, apiKey, newDek)).Should().BeTrue();

        (await SlotAsync(quick)).DekFingerprint.Should().Be(newFp);
        (await AgentRepo.GetByIdAsync(agentId))!.DekFingerprint.Should().Be(newFp);
        var boot = await Doors.ListAsync();
        boot.Single(d => d.Kind == BootDoorKinds.Slot && d.SourceId == quick).DekFingerprint.Should().Be(newFp, "the boot copy names the DEK too");
        boot.Single(d => d.Kind == BootDoorKinds.Agent && d.SourceId == agentId).DekFingerprint.Should().Be(newFp);
        (await Wrappers.ListAsync()).Select(w => w.DekFingerprint).Should().Equal([newFp], "every door names the new DEK: the old wrapper is pruned");
        await AssertDoorsEqualSealedSetAsync();
        CryptographicOperations.ZeroMemory(oldDek);
    }

    [Fact]
    public async Task TheMasterPasswordChange_KeepsTheOwnerAndTheMakerOfTheDoor_FromTheMomentTheNewDoorIsWritten()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync("UPDATE tbl_key_slot SET created_by_user_id = 77, label = 'pc' WHERE slot_id = @id", new { id = admin.KeySlotId });
        var before = (await KeySlots.GetAllAsync()).Select(s => s.SlotId).ToHashSet();

        await Keys.ChangePasswordAsync(AdminPassword, "AdminPass2");

        var door = (await KeySlots.GetAllAsync()).Single(s => !before.Contains(s.SlotId));
        door.OwnerUserId.Should().Be(admin.Id);
        door.CreatedByUserId.Should().Be(77, "the maker of the door is not lost by a password change");
        door.Label.Should().Be("pc");
    }

    [Fact]
    public async Task ADoorThatNamesAnOlderDek_KeepsThatWrapper_EvenWhenTheSessionNoLongerHoldsIt()
    {
        var oldDek = Session.GetMasterDek();
        await Mirror.RewriteAsync();
        var newDek = RandomNumberGenerator.GetBytes(32);
        await Mirror.AddDbKeyWrapperAsync(newDek);
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync("UPDATE tbl_key_slot SET dek_fp = @fp", new { fp = DekFingerprint.Of(oldDek) });

        // A restart: the session holds only the new DEK; the straggler's door still names the old one.
        Session.Lock();
        Session.UnlockWithDek((byte[])newDek.Clone());
        await Mirror.RewriteAsync();

        (await Wrappers.ListAsync()).Select(w => w.DekFingerprint).Should().BeEquivalentTo([DekFingerprint.Of(oldDek), DekFingerprint.Of(newDek)],
            "a sealed node never loses the wrapper a remaining door needs: it opens before the database, before any lazy rewrap");
    }
}
