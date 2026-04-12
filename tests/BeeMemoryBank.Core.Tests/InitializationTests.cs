namespace BeeMemoryBank.Core.Tests;

public class InitializationTests : TestFixture
{
    [Fact]
    public async Task IsInitialized_BeforeInit_ReturnsFalse()
    {
        var result = await InitService.IsInitializedAsync();
        result.Should().BeFalse();
    }

    [Fact]
    public async Task Initialize_CreatesNodeIdentityAndMasterKey()
    {
        await InitService.InitializeAsync("Desktop", "myPassword");

        var initialized = await InitService.IsInitializedAsync();
        initialized.Should().BeTrue();

        // Session should unlock after initialization
        var unlocked = await Session.UnlockAsync("myPassword");
        unlocked.Should().BeTrue();
    }

    [Fact]
    public async Task Initialize_Twice_Throws()
    {
        await InitService.InitializeAsync("Node1", "pass");

        var act = async () => await InitService.InitializeAsync("Node2", "pass2");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
