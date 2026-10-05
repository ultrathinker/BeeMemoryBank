using BeeMemoryBank.Desktop.Services;

namespace BeeMemoryBank.Desktop.MacOS;

/// <summary>
/// The macOS set: a per-user LaunchAgent, an IOKit power assertion, an IOKit sleep notification, Finder (<c>open</c>), banners through
/// <c>osascript</c>, a monochrome template image in the menu bar, and a Quit that goes through the shell's graceful stop. Creating the
/// services calls nothing native: that happens when they are used, and only on a Mac.
/// </summary>
internal sealed class MacOsShellPlatform : IShellPlatform
{
    private readonly MacOsNotifier _notifier = new();

    public ShellOs Os => ShellOs.MacOs;

    public IAutostartService CreateAutostart() => new MacOsAutostart();

    public IPreventSleepService? CreatePreventSleep(DesktopSettingsStore settings) => new MacOsPreventSleep(settings);

    public IPowerEventsService? CreatePowerEvents(SleepLockRequest lockNode) => new MacOsSleepMonitor(lockNode, _notifier);

    public IFileManagerReveal FileManager { get; } = new MacOsFileManager();

    /// <summary>The menu-bar image: black with alpha, 36 px for an 18 pt menu bar at 2x (the 18 px 1x file sits beside it).</summary>
    public string TrayIconAsset => "avares://BeeMemoryBank.Desktop/Assets/tray-template@2x.png";

    public bool TrayIconIsTemplate => true;

    public string QuitMenuText => "Quit Bee Memory Bank";

    public bool InterceptsApplicationQuit => true;

    public string AutostartCheckText => "Start Bee Memory Bank when I sign in to this Mac";

    public string AutostartProfileNote => "Which profile opens when you sign in is set in Settings.";
}
