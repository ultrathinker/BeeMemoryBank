using BeeMemoryBank.BlindDesktop.Windows;
using BeeMemoryBank.BlindMobile.Services.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindDesktop.Platform;

/// <summary>The Windows platform: only compiled into a Windows build (see the project file).</summary>
internal sealed class WindowsDesktopPlatform(string? dataDirectory) : IBlindDesktopPlatform
{
    private readonly WindowsBlindPaths _paths = new(dataDirectory);

    /// <summary>The real app's data folder (LocalApplicationData, then BeeMemoryBankBlind); nothing is created.</summary>
    internal static string DefaultDataDirectory() => WindowsBlindPaths.DefaultRoot();

    public string Name => "Windows";

    public IBlindPaths Paths => _paths;

    public void AddSeams(IServiceCollection services) => services.AddWindowsBlindSeams(_paths);

    public IInstanceGuard? TryAcquireInstance() => SingleInstanceGuard.TryAcquire(_paths.DataDirectory);

    public bool SignalRunningInstance() => SingleInstanceGuard.SignalRunningInstance(_paths.DataDirectory);
}
