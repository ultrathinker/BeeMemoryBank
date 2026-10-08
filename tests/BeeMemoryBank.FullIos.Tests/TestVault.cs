using BeeMemoryBank.FullIos.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>The app's own composition on a new folder, prepared like the app prepares it at start.</summary>
internal sealed class TestVault : IAsyncDisposable
{
    public const string Password = "Correct-Horse-9";

    public required ServiceProvider Services { get; init; }
    public required FullNodePaths Paths { get; init; }
    public FullVault Vault => new(Services);
    public NotesService Notes => new(Services);

    public static async Task<TestVault> PrepareAsync(string name)
    {
        var paths = new FullNodePaths(TestFolders.New(name));
        var services = new ServiceCollection().AddFullNode(paths).BuildServiceProvider();
        await FullNodeServices.PrepareAsync(services);
        return new TestVault { Services = services, Paths = paths };
    }

    /// <summary>A new vault, created and open; the recovery key it showed.</summary>
    public static async Task<(TestVault Vault, string RecoveryKey)> CreateAsync(string name)
    {
        var vault = await PrepareAsync(name);
        var recovery = await vault.Vault.CreateAsync("Test iPhone", Password);
        return (vault, recovery);
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
