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

    // Security regression test for finding C1: the recovery slot's salt column must NOT be (or
    // be derivable as) the recovery key itself. Before the fix, AddRecoveryKeyAsync passed the
    // recovery key's own raw bytes as the Argon2id salt, so tbl_key_slot.salt WAS the recovery
    // key in plaintext — anyone who could read the database could reconstruct it with no
    // password at all. The fix generates an independent random salt (KeyDerivation.GenerateSalt),
    // exactly like every other slot type.
    [Fact]
    public async Task RecoveryKey_SaltIsIndependentOfTheRecoveryKeyItself()
    {
        var recoveryKey = await KeyManagement.AddRecoveryKeyAsync();
        var recoveryKeyBytes = Convert.FromBase64String(recoveryKey);

        var slots = await GetKeySlotRepoAsync().GetAllAsync();
        var recoverySlot = slots.Single(s => s.SlotType == "recovery");

        recoverySlot.Salt.Should().NotBeNull();
        recoverySlot.Salt.Should().NotEqual(recoveryKeyBytes,
            "the salt must be independent random material — if it equals the recovery key " +
            "itself, anyone with read access to tbl_key_slot can reconstruct the recovery key " +
            "from the salt column alone and unwrap the vault without ever knowing the real secret");
    }

    // Regression test for finding L2: CreateAsync(newSlot) and DeleteAsync(oldSlot) now run in
    // one SQLite transaction. The specific bug being guarded against was a crash between the two
    // statements leaving BOTH the old and the new password permanently valid (i.e. two "user"
    // slots on disk after a "single" password change). We can't inject a real mid-transaction
    // crash here, but we CAN assert the end state a correct atomic rotation must produce: exactly
    // one slot, never two.
    [Fact]
    public async Task ChangePassword_LeavesExactlyOneSlot_NeverBothOldAndNew()
    {
        var before = await GetKeySlotRepoAsync().GetAllAsync();
        before.Should().HaveCount(1);

        await KeyManagement.ChangePasswordAsync("oldPassword", "newPassword");

        var after = await GetKeySlotRepoAsync().GetAllAsync();
        after.Should().HaveCount(1,
            "the old slot's deletion and the new slot's creation must commit together — a " +
            "surviving old slot alongside the new one would mean the revoked password still works");
    }

    // Regression test for finding L2's RepointKeySlotAsync ordering: InitializationService points
    // the admin user's KeySlotId at the very slot ChangePasswordAsync rotates, so this exercises
    // the same repoint path the legacy mobile flow depends on to keep tbl_user in sync with the
    // now-current slot after the atomic create+delete.
    [Fact]
    public async Task ChangePassword_RepointsUsersKeySlotToTheNewSlot()
    {
        var userRepo = new BeeMemoryBank.Storage.Sqlite.UserRepository(Factory);
        var adminBefore = await userRepo.GetByUsernameAsync("admin");
        adminBefore.Should().NotBeNull();
        var oldSlotId = adminBefore!.KeySlotId;
        oldSlotId.Should().NotBeNull();

        await KeyManagement.ChangePasswordAsync("oldPassword", "newPassword");

        var adminAfter = await userRepo.GetByUsernameAsync("admin");
        adminAfter!.KeySlotId.Should().NotBeNull().And.NotBe(oldSlotId);

        var slots = await GetKeySlotRepoAsync().GetAllAsync();
        slots.Should().ContainSingle(s => s.SlotId == adminAfter.KeySlotId);
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
