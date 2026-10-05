using System;
using System.Threading;
using System.Threading.Tasks;

namespace BeeMemoryBank.Desktop.Services;

// The services of the shell that differ per operating system, as small interfaces. The Windows classes
// (AutostartService, PreventSleepService, PowerEventsService, WindowsFileManager) and the macOS ones
// (desktop/BeeMemoryBank.Desktop/MacOS) implement them; ShellPlatforms picks the set by OS in one place, so
// the shared code (Program, App, MainWindow, the windows) has no OS switches of its own.

/// <summary>Start the app when the person signs in.</summary>
public interface IAutostartService
{
    /// <summary>True when the app is set to start at sign-in <b>and</b> that setting points at this copy of the app.</summary>
    bool IsEnabled { get; }

    /// <summary>Turns autostart on. Throws when it could not be done: it never reports success it did not achieve.</summary>
    void Enable();

    /// <summary>Turns autostart off (a no-op when it is off). Throws when it could not be done.</summary>
    void Disable();

    /// <summary>
    /// Something that did not go as asked but did not fail the last change (for example the login item is in place but the system
    /// declined to load it right now). Null when the last change went exactly as asked.
    /// </summary>
    string? LastWarning => null;
}

/// <summary>Keeps the computer from falling asleep while the setting is on.</summary>
public interface IPreventSleepService
{
    /// <summary>The setting (persisted). Setting it applies it at once.</summary>
    bool IsEnabled { get; set; }

    /// <summary>Applies the stored setting to the system (on start-up).</summary>
    void ApplyState();

    /// <summary>Gives the system its sleep back without touching the stored setting (on exit).</summary>
    void DisableSleepPreventionOnly();
}

/// <summary>Tells the shell that the computer is about to sleep. Disposing stops the monitoring.</summary>
public interface IPowerEventsService : IDisposable
{
    void Start();
}

/// <summary>Shows a folder or a file in the system's file manager (Explorer, Finder).</summary>
public interface IFileManagerReveal
{
    /// <summary>Opens the folder (it is created first when it does not exist yet).</summary>
    void OpenFolder(string path);

    /// <summary>Opens the file manager with the file (or folder) selected.</summary>
    void Reveal(string path);
}

/// <summary>
/// A notice outside the app's own windows. Presentation only: nothing may depend on it being seen, a refused permission or a missing
/// tool is swallowed, and it never throws.
/// </summary>
public interface IUserNotifier
{
    void Notify(string title, string message);
}

/// <summary>What the "lock the node" request made when the computer went to sleep ended with.</summary>
/// <param name="Succeeded">The node was locked, or there was nothing to lock.</param>
/// <param name="Detail">Why it did not work (or that there was nothing to lock); null when it simply worked.</param>
/// <param name="HttpStatus">The node's HTTP status when it answered with one that was not a success; null otherwise (no answer, or a success).</param>
/// <param name="LogOnly">The vault was not locked, but for a reason the person cannot act on at the moment of sleep (the app did not start this node and holds no key for it): the reason is logged, no notice is shown.</param>
public sealed record SleepLockResult(bool Succeeded, string? Detail = null, int? HttpStatus = null, bool LogOnly = false);

/// <summary>The shell's request to the node to lock the vault, as the power-events services call it.</summary>
public delegate Task<SleepLockResult> SleepLockRequest(CancellationToken cancellationToken);
