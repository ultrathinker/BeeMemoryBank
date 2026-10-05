using System;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>The operating systems the shell tells apart.</summary>
public enum ShellOs { Windows, MacOs, Other }

/// <summary>
/// Everything the shell needs from the operating system, behind one object chosen by OS in one place (<see cref="ShellPlatforms"/>).
/// The shared code asks this object and never asks which OS it is on.
/// </summary>
public interface IShellPlatform
{
    ShellOs Os { get; }

    IAutostartService CreateAutostart();

    /// <summary>Null when the system has no way to keep the computer awake (the setting is then shown disabled).</summary>
    IPreventSleepService? CreatePreventSleep(DesktopSettingsStore settings);

    /// <summary>Null when the system has no sleep notification the shell can use. <paramref name="lockNode"/> asks the node to lock the vault.</summary>
    IPowerEventsService? CreatePowerEvents(SleepLockRequest lockNode);

    IFileManagerReveal FileManager { get; }

    /// <summary>The <c>avares://</c> resource of the tray / menu-bar icon.</summary>
    string TrayIconAsset { get; }

    /// <summary>The icon is a monochrome template image that the system tints (macOS menu bar).</summary>
    bool TrayIconIsTemplate { get; }

    /// <summary>The tray menu's last item.</summary>
    string QuitMenuText { get; }

    /// <summary>
    /// The system's own Quit (the macOS application menu's Cmd+Q and the Dock menu) ends the application without asking the shell, so the
    /// shell has to catch that request and stop the node first. False where the window's close button is the only other way out.
    /// </summary>
    bool InterceptsApplicationQuit { get; }

    string AutostartCheckText { get; }

    string AutostartProfileNote { get; }
}

/// <summary>Chooses the <see cref="IShellPlatform"/> of the running system.</summary>
public static class ShellPlatforms
{
    private static readonly Lazy<IShellPlatform> Detected = new(() => For(Detect()));

    /// <summary>The platform of the running system.</summary>
    public static IShellPlatform Current => Detected.Value;

    public static ShellOs Detect() =>
        OperatingSystem.IsWindows() ? ShellOs.Windows
        : OperatingSystem.IsMacOS() ? ShellOs.MacOs
        : ShellOs.Other;

    /// <summary>The platform for an OS, whatever the running one is (tests build all of them).</summary>
    public static IShellPlatform For(ShellOs os) => os switch
    {
        ShellOs.Windows => new WindowsShellPlatform(),
        ShellOs.MacOs => new MacOS.MacOsShellPlatform(),
        _ => new UnsupportedShellPlatform(os),
    };
}
