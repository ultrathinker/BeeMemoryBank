using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.FullIos.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// The tests' stand-in for the phone's UIKit transcoder (iOS-only, not compiled here): it keeps the
/// bytes as they are, so a test picture stays a PNG and the storage/resolution path is what is tested.
/// </summary>
internal sealed class TestImageTranscoder : IImageTranscoder
{
    public (byte[] data, bool converted) ConvertToJpeg(byte[] input, string contentType) => (input, false);
    public byte[] DownscaleJpeg(byte[] input, int maxDimension) => input;
    public byte[]? ShrinkJpegToFit(byte[] input, long maxBytes) => input;
}

/// <summary>The app's own composition on a new folder, prepared like the app prepares it at start.</summary>
internal sealed class TestVault : IAsyncDisposable
{
    public const string Password = "Correct-Horse-9";

    /// <summary>The mandatory password hint used by the tests.</summary>
    public const string Hint = "A test hint, not the password";

    public required ServiceProvider Services { get; init; }
    public required FullNodePaths Paths { get; init; }
    public FullVault Vault => new(Services);
    public NotesService Notes => new(Services);

    public static async Task<TestVault> PrepareAsync(string name)
    {
        var paths = new FullNodePaths(TestFolders.New(name));
        var collection = new ServiceCollection().AddFullNode(paths);
        // The iOS transcoder is compiled only on the phone; the tests supply their own (see above).
        collection.AddSingleton<IImageTranscoder, TestImageTranscoder>();
        var services = collection.BuildServiceProvider();
        await FullNodeServices.PrepareAsync(services);
        return new TestVault { Services = services, Paths = paths };
    }

    /// <summary>A new vault, created and open; the recovery key it showed.</summary>
    public static async Task<(TestVault Vault, string RecoveryKey)> CreateAsync(string name)
    {
        var vault = await PrepareAsync(name);
        var recovery = await vault.Vault.CreateAsync("Test iPhone", Password, Hint);
        return (vault, recovery);
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
