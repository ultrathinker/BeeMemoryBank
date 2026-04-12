namespace BeeMemoryBank.Core.Tests;

public class SessionTests : TestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("TestNode", "correctPassword");
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
}
