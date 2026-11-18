using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Vault.Sealed;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>
/// Release 2.12 (plan WS-B2): on a sealed node, PersonDoors and UnknownOwnerDoors retire the boot-store copies of the doors they revoke in the
/// write-protocol order, so a cold start with a revoked person's password/recovery key, or with an ownerless key, no longer opens the node.
/// </summary>
public class SealedPersonAndUnknownOwnerDoorsTests : SealedWriterFixture
{
    [Fact]
    public async Task PersonDoors_RetiresThePersonsBootDoors_AndTheirKeysNoLongerOpen()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var carolKey = await Keys.AddRecoveryKeyAsync(carol.Id);
        var carolAgent = await CreateWrappedAgentAsync(carol.Id, "carols-agent");
        await Mirror.RewriteAsync();
        await AssertDoorsEqualSealedSetAsync();
        var carolSlot = (await UserRepo.GetByIdAsync(carol.Id))!.KeySlotId!.Value;

        await PersonDoors.RevokeAllOfAsync(Factory, carol.Id, KeySlots, Mirror);

        await AssertDoorsEqualSealedSetAsync();
        var doors = await Doors.ListAsync();
        doors.Should().NotContain(d => d.Kind == BootDoorKinds.Slot && d.SourceId == carolSlot);
        doors.Should().NotContain(d => d.Kind == BootDoorKinds.Agent && d.SourceId == carolAgent);
        doors.Should().NotContain(d => d.SlotType == KeySlotTypes.Recovery, "carol's recovery key door is retired (she had the only one)");
        Session.Lock();
        (await Session.UnlockAsync("CarolPass1")).Should().BeFalse("carol's password door is retired");
        (await Session.UnlockAsync(carolKey)).Should().BeFalse("carol's recovery key door is retired");
    }

    [Fact]
    public async Task PersonDoors_LeavesTheOtherPersonsBootDoors()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var adminKey = await Keys.AddRecoveryKeyAsync(admin.Id);
        await Keys.AddRecoveryKeyAsync(carol.Id);
        await Mirror.RewriteAsync();
        var adminSlot = (await UserRepo.GetByIdAsync(admin.Id))!.KeySlotId!.Value;

        await PersonDoors.RevokeAllOfAsync(Factory, carol.Id, KeySlots, Mirror);

        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Should().Contain(d => d.Kind == BootDoorKinds.Slot && d.SourceId == adminSlot);
        Session.Lock();
        (await Session.UnlockAsync("AdminPass1")).Should().BeTrue();
        (await Session.UnlockAsync(adminKey)).Should().BeTrue();
    }

    [Fact]
    public async Task UnknownOwnerDoors_RetiresTheOwnerlessBootDoors_AndKeepsTheOwnedOnes()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var ownedKey = await Keys.AddRecoveryKeyAsync(admin.Id);
        var ownerless = await AddOwnerlessRecoverySlotAsync();
        await Mirror.RewriteAsync();
        await AssertDoorsEqualSealedSetAsync();

        await UnknownOwnerDoors.RevokeAllAsync(Factory, KeySlots, AgentRepo, UserRepo, Mirror);

        await AssertDoorsEqualSealedSetAsync();
        var doors = await Doors.ListAsync();
        doors.Should().NotContain(d => d.Kind == BootDoorKinds.Slot && d.SourceId == ownerless);
        doors.Should().Contain(d => d.SlotType == KeySlotTypes.Recovery, "the owned key's door remains");
        Session.Lock();
        (await Session.UnlockAsync(ownedKey)).Should().BeTrue("the owned key still opens the node");
    }

    private async Task<int> AddOwnerlessRecoverySlotAsync()
    {
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek(Convert.ToBase64String(SecureRandom.GetBytes(32)), salt);
        var dek = Session.GetMasterDek();
        try
        {
            var (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, kek);
            var slot = new MasterKeyStore
            {
                SlotType = KeySlotTypes.Recovery, EncryptedMasterDek = wrapped, IV = iv, Salt = salt,
                ArgonMemory = CryptoConstants.DefaultArgonMemory, ArgonIterations = CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = CryptoConstants.DefaultArgonParallelism, CreatedAt = DateTime.UtcNow, DoorVersion = 1, OwnerUserId = null,
            };
            slot.SlotId = await KeySlots.CreateAsync(slot);
            return slot.SlotId;
        }
        finally { Array.Clear(kek); Array.Clear(dek); }
    }
}
