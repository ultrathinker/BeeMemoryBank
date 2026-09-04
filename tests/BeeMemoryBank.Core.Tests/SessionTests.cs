namespace BeeMemoryBank.Core.Tests;

public class SessionTests : TestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "correctPassword");
    }

    [Fact]
    public async Task Unlock_WithCorrectPassword_Succeeds()
    {
        var result = await Session.UnlockAsync("correctPassword");
        result.Should().BeTrue();
        Session.IsUnlocked.Should().BeTrue();
    }

    [Fact]
    public async Task Unlock_WithWrongPassword_Fails()
    {
        var result = await Session.UnlockAsync("wrongPassword");
        result.Should().BeFalse();
        Session.IsUnlocked.Should().BeFalse();
    }

    [Fact]
    public async Task Lock_AfterUnlock_LocksSession()
    {
        await Session.UnlockAsync("correctPassword");
        Session.IsUnlocked.Should().BeTrue();

        Session.Lock();
        Session.IsUnlocked.Should().BeFalse();
    }

    [Fact]
    public async Task GetMasterDek_WhenLocked_Throws()
    {
        // Session is locked
        var act = () => Session.GetMasterDek();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task GetMasterDek_WhenUnlocked_ReturnsSameKey()
    {
        await Session.UnlockAsync("correctPassword");
        var dek1 = Session.GetMasterDek();
        var dek2 = Session.GetMasterDek();
        dek1.Should().Equal(dek2);
    }

    // ─── VerifyMasterPasswordAsync ──────────────────────────────────────────
    //
    // Re-authentication before a dangerous operation (node reset, snapshot restore, whitelist URL
    // change) used to call UnlockAsync as a password check. That unlocks the process-wide session
    // for every user and agent as a side effect of merely ASKING — so a caller probing the reset
    // endpoint with a correct password left the vault open even when the operation then failed, and
    // the checks that follow the password ("are you a superadmin?") ran too late to prevent it.

    [Fact]
    public async Task VerifyMasterPassword_WithCorrectPassword_Succeeds_ButLeavesTheVaultLocked()
    {
        var result = await Session.VerifyMasterPasswordAsync("correctPassword");

        result.Should().BeTrue();
        Session.IsUnlocked.Should().BeFalse("verification must not be a side-door unlock");
    }

    [Fact]
    public async Task VerifyMasterPassword_WithWrongPassword_Fails()
    {
        var result = await Session.VerifyMasterPasswordAsync("wrongPassword");

        result.Should().BeFalse();
        Session.IsUnlocked.Should().BeFalse();
    }

    [Fact]
    public async Task VerifyMasterPassword_WhileUnlocked_DoesNotDisturbTheOpenSession()
    {
        await Session.UnlockAsync("correctPassword");
        var dekBefore = Session.GetMasterDek();

        (await Session.VerifyMasterPasswordAsync("correctPassword")).Should().BeTrue();
        (await Session.VerifyMasterPasswordAsync("wrongPassword")).Should().BeFalse();

        Session.IsUnlocked.Should().BeTrue("a re-auth check must never lock a live session");
        Session.GetMasterDek().Should().Equal(dekBefore);
    }
}
