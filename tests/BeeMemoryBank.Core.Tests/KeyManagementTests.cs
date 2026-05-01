namespace BeeMemoryBank.Core.Tests;

public class KeyManagementTests : TestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "oldPassword");
        await Session.UnlockAsync("oldPassword");
    }

    [Fact]
    public async Task ChangePassword_OldKeyInvalid_NewKeyWorks()
    {
        await KeyManagement.ChangePasswordAsync("oldPassword", "newPassword");
        Session.Lock();

        var withOld = await Session.UnlockAsync("oldPassword");
        withOld.Should().BeFalse();

        var withNew = await Session.UnlockAsync("newPassword");
        withNew.Should().BeTrue();
    }

    [Fact]
    public async Task ChangePassword_WrongOldPassword_Throws()
    {
        var act = async () => await KeyManagement.ChangePasswordAsync("wrong", "new");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RecoveryKey_CanUnlockAfterPasswordLost()
    {
        var recoveryKey = await KeyManagement.AddRecoveryKeyAsync();

        // "Forget" password — create a new session on the same storage
        Session.Lock();

        // Unlock via recovery key
        var unlocked = await Session.UnlockAsync(recoveryKey);
        unlocked.Should().BeTrue();
        Session.IsUnlocked.Should().BeTrue();
    }

    [Fact]
    public async Task RemoveLastSlot_Throws()
    {
        var slots = await GetKeySlotRepoAsync().GetAllAsync();
        slots.Should().HaveCount(1);

        var act = async () => await KeyManagement.RemoveSlotAsync(slots[0].SlotId);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RemoveSlot_WithMultipleSlots_Succeeds()
    {
        var recoveryKey = await KeyManagement.AddRecoveryKeyAsync();

        var slotsBefore = await GetKeySlotRepoAsync().GetAllAsync();
        slotsBefore.Should().HaveCount(2);

        var recoverySlot = slotsBefore.First(s => s.SlotType == "recovery");
        await KeyManagement.RemoveSlotAsync(recoverySlot.SlotId);

        var slotsAfter = await GetKeySlotRepoAsync().GetAllAsync();
        slotsAfter.Should().HaveCount(1);
        slotsAfter.Should().OnlyContain(s => s.SlotType == "user");
    }

    private BeeMemoryBank.Storage.Sqlite.KeySlotRepository GetKeySlotRepoAsync()
        => new(Factory);
}
