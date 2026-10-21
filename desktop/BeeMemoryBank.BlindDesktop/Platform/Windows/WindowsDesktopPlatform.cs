using BeeMemoryBank.BlindDesktop.Windows;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Platform;

/// <summary>The Windows platform: only compiled into a Windows build (see the project file).</summary>
internal sealed class WindowsDesktopPlatform : IBlindDesktopPlatform
{
    private readonly WindowsBlindSeams _seams;

    public WindowsDesktopPlatform(string? dataDirectory)
    {
        _seams = WindowsBlindPlatform.Create(new WindowsBlindPaths(dataDirectory));
    }

    /// <summary>The real app's data folder (LocalApplicationData, then BeeMemoryBankBlind); nothing is created.</summary>
    internal static string DefaultDataDirectory() => WindowsBlindPaths.DefaultRoot();

    public string Name => "Windows";

    public IBlindPaths Paths => _seams.Paths;

    public IBlindSecretStoreFactory SecretStores => _seams.Secrets;

    public IBlindStateStoreFactory StateStores => _seams.States;

    public IBlindDeviceConditions DeviceConditions => _seams.Conditions;

    public IBlindAutostart Autostart => _seams.Autostart;

    public IInstanceGuard? TryAcquireInstance() => SingleInstanceGuard.TryAcquire(_seams.Paths.DataDirectory);

    public bool SignalRunningInstance() => SingleInstanceGuard.SignalRunningInstance(_seams.Paths.DataDirectory);
}
