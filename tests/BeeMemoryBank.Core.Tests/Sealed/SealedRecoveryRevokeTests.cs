using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Vault.Sealed;

namespace BeeMemoryBank.Core.Tests.Sealed;

/// <summary>
/// Release 2.12 (plan WS-B2): revoking a recovery key on a sealed node retires its boot-store door in the write-protocol order, so a cold start
/// with the revoked key no longer opens the node. Revoking every ownerless key does the same for each of them.
/// </summary>
public class SealedRecoveryRevokeTests : SealedWriterFixture
{
    [Fact]
    public async Task RevokingARecoveryKey_RemovesItsBootDoor_AndTheKeyNoLongerOpens()
    {
        var key = await Keys.AddRecoveryKeyAsync();
        await AssertDoorsEqualSealedSetAsync();
        var recovery = (await Doors.ListAsync()).Single(d => d.SlotType == KeySlotTypes.Recovery);

        await Keys.RemoveSlotAsync((int)recovery.SourceId);

        await AssertDoorsEqualSealedSetAsync();
        (await Doors.ListAsync()).Should().NotContain(d => d.Kind == BootDoorKinds.Slot && d.SourceId == recovery.SourceId,
            "the revoked key's door is retired from the boot store");
        Session.Lock();
        (await Session.UnlockAsync(key)).Should().BeFalse("a cold start with the revoked key finds no door");
    }

    [Fact]
    public async Task RevokingAllOwnerlessKeys_RemovesTheirBootDoors_AndKeepsTheOwnedOnes()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var ownedKey = await Keys.AddRecoveryKeyAsync(admin.Id);
        // Two ownerless (pre-2.12) keys, written straight into the sealed set and mirrored.
        var ownerless1 = await AddOwnerlessRecoverySlotAsync();
        var ownerless2 = await AddOwnerlessRecoverySlotAsync();
        await Mirror.RewriteAsync();
        await AssertDoorsEqualSealedSetAsync();

        var count = await Keys.RevokeUnknownOwnerRecoveryKeysAsync();

        count.Should().Be(2);
        await AssertDoorsEqualSealedSetAsync();
        var doors = await Doors.ListAsync();
        doors.Should().NotContain(d => d.Kind == BootDoorKinds.Slot && d.SourceId == ownerless1);
        doors.Should().NotContain(d => d.Kind == BootDoorKinds.Slot && d.SourceId == ownerless2);
        doors.Should().Contain(d => d.SlotType == KeySlotTypes.Recovery, "the owned key's door remains");
        Session.Lock();
        (await Session.UnlockAsync(ownedKey)).Should().BeTrue("the owned key still opens the node");
    }

    private async Task<int> AddOwnerlessRecoverySlotAsync()
    {
        var salt = BeeMemoryBank.Crypto.KeyDerivation.GenerateSalt();
        var kek = BeeMemoryBank.Crypto.KeyDerivation.DeriveKek(Convert.ToBase64String(BeeMemoryBank.Crypto.SecureRandom.GetBytes(32)), salt);
        var dek = Session.GetMasterDek();
        try
        {
            var (wrapped, iv) = BeeMemoryBank.Crypto.MasterKeyManager.WrapMasterDek(dek, kek);
            var slot = new MasterKeyStore
            {
                SlotType = KeySlotTypes.Recovery, EncryptedMasterDek = wrapped, IV = iv, Salt = salt,
                ArgonMemory = BeeMemoryBank.Crypto.CryptoConstants.DefaultArgonMemory,
                ArgonIterations = BeeMemoryBank.Crypto.CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = BeeMemoryBank.Crypto.CryptoConstants.DefaultArgonParallelism,
                CreatedAt = DateTime.UtcNow, DoorVersion = 1, OwnerUserId = null,
            };
            slot.SlotId = await KeySlots.CreateAsync(slot);
            return slot.SlotId;
        }
        finally { Array.Clear(kek); Array.Clear(dek); }
    }
}
