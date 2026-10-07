using System;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// The set for a system without shell services of its own: every service says plainly that it is not there (an autostart that does not
/// exist throws instead of pretending, the power services are absent), and the window and tray work as they always did.
/// </summary>
internal sealed class UnsupportedShellPlatform : IShellPlatform
{
    public UnsupportedShellPlatform(ShellOs os = ShellOs.Other) => Os = os;

    public ShellOs Os { get; }

    public IAutostartService CreateAutostart() => new UnsupportedAutostart();

    public IPreventSleepService? CreatePreventSleep(DesktopSettingsStore settings) => null;

    public IPowerEventsService? CreatePowerEvents(SleepLockRequest lockNode, Func<bool> lockOnSleepEnabled) => null;

    public IUserNotifier? CreateNotifier() => null;

    public IFileManagerReveal FileManager { get; } = new NoFileManager();

    public string TrayIconAsset => "avares://BeeMemoryBank.Desktop/Assets/icon.png";

    public bool TrayIconIsTemplate => false;

    public string QuitMenuText => "Exit";

    public bool InterceptsApplicationQuit => false;

    public string AutostartCheckText => "Start Bee Memory Bank when I sign in";

    public string AutostartProfileNote => "Which profile opens at startup is set in Settings.";
}

internal sealed class UnsupportedAutostart : IAutostartService
{
    private const string Why = "Starting at sign-in is not supported on this system.";

    public bool IsEnabled => false;

    public void Enable() => throw new PlatformNotSupportedException(Why);

    public void Disable() => throw new PlatformNotSupportedException(Why);
}

internal sealed class NoFileManager : IFileManagerReveal
{
    public void OpenFolder(string path) { }

    public void Reveal(string path) { }
}
