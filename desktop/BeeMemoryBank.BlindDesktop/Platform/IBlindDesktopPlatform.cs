using BeeMemoryBank.BlindMobile.Services.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindDesktop.Platform;

/// <summary>
/// What the operating system adds to the cross-platform host: the private folder and the AppCore seams that depend on the OS
/// (secrets, state, device conditions, autostart), the way "only one copy runs" is kept, and the few presentation facts that differ
/// (where the error log goes, which icon the tray shows, what the tray is called). The Windows implementation sits in the Windows
/// platform assembly, the macOS one in the macOS adapters; <see cref="PlatformSelector"/> chooses the one the host was built for, and
/// nothing else in the host asks which operating system it runs on.
/// </summary>
public interface IBlindDesktopPlatform
{
    /// <summary>Human name of the OS for the log and the window ("Windows").</summary>
    string Name { get; }

    /// <summary>The app-private root, its database file and its other paths.</summary>
    IBlindPaths Paths { get; }

    /// <summary>Registers the OS-dependent seams: <see cref="IBlindSecretStore"/>, <see cref="IBlindStateStore"/>, and <see cref="IBlindAutostart"/>.</summary>
    void AddSeams(IServiceCollection services);

    /// <summary>
    /// Takes "the one running copy" for this data folder: the guard if this process is the first, null if another copy already runs.
    /// Windows uses a named mutex and event; Unix has neither (in .NET) and uses a file lock and a socket file in the app's private folder.
    /// </summary>
    IInstanceGuard? TryAcquireInstance();

    /// <summary>Asks the copy that is running (if any) to show its window. False if nobody answered.</summary>
    bool SignalRunningInstance();

    /// <summary>What the status area of the desktop is called in the window's own words: "tray" (Windows), "menu bar" (macOS).</summary>
    string StatusAreaName => "tray";

    /// <summary>The Avalonia resource (<c>avares://...</c>) of the icon the tray / menu bar shows. A template-style monochrome one on macOS.</summary>
    string TrayIconAsset => "avares://BeeMemoryBank.BlindDesktop/Assets/icon.png";

    /// <summary>True when <see cref="TrayIconAsset"/> is a monochrome template image (black with alpha) that the system must tint for the light and dark menu bar. macOS only; the shell has to say so, or the system draws the black pixels as they are.</summary>
    bool TrayIconIsTemplate => false;

    /// <summary>Where the app's own error log goes; null means the default file in the temp folder.</summary>
    string? ErrorLogPath => null;

    /// <summary>OS-specific items of <c>--self-check</c> (folder rules, which seams were picked); the common checks are the host's.</summary>
    IReadOnlyList<SelfCheckItem> SelfCheck() => [];
}

/// <summary>"Only one copy runs": held for the life of the process; a second start asks the first one to show its window.</summary>
public interface IInstanceGuard : IDisposable
{
    /// <summary>Calls <paramref name="onActivate"/> (on a pool thread) every time a second start asks for the window.</summary>
    void Listen(Action onActivate);
}

/// <summary>One line of the headless self-check.</summary>
public sealed record SelfCheckItem(string Name, bool Ok, string Detail);

/// <summary>Chooses the platform implementation this host was built with.</summary>
public static class PlatformSelector
{
    /// <param name="dataDirectory">A private root other than the default (tests and the self-check use a scratch folder), or null.</param>
    public static IBlindDesktopPlatform Create(string? dataDirectory = null)
    {
#if BLIND_WINDOWS
        return new WindowsDesktopPlatform(dataDirectory);
#elif BLIND_MACOS
        return new MacOsDesktopPlatform(dataDirectory);
#else
        throw new PlatformNotSupportedException(
            "This build has no platform adapters. A head for another operating system adds its implementation of IBlindDesktopPlatform here.");
#endif
    }

    /// <summary>
    /// The folder the real app keeps its data in, computed WITHOUT creating anything. <c>--self-check</c> uses it to refuse that folder
    /// (and anything inside it) before it writes a single byte.
    /// </summary>
    public static string DefaultDataDirectory()
    {
#if BLIND_WINDOWS
        return WindowsDesktopPlatform.DefaultDataDirectory();
#elif BLIND_MACOS
        return MacOsDesktopPlatform.DefaultDataDirectory();
#else
        throw new PlatformNotSupportedException("This build has no platform adapters.");
#endif
    }
}
