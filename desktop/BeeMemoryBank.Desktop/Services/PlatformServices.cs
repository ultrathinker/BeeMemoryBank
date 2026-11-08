using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Like <see cref="Notify"/>, and says whether the notice was handed to the system (false: there was no window or shell to show it).
    /// Never throws. A notifier that cannot tell (a banner run in the background) leaves the default: it counts as shown. The alarm
    /// watcher uses it to try a notice again that nobody saw, instead of treating it as notified for a day.
    /// </summary>
    bool TryNotify(string title, string message)
    {
        Notify(title, message);
        return true;
    }
}

/// <summary>One button of a <see cref="IActionNotifier"/> notice.</summary>
/// <param name="Id">What <see cref="IActionNotifier.TryNotifyAsync"/> hands back when the person chooses it.</param>
/// <param name="Title">The button's text.</param>
/// <param name="OpensApp">The button brings the app to the front (the system starts it in the foreground).</param>
public sealed record NoticeAction(string Id, string Title, bool OpensApp = false);

/// <summary>
/// A notice with buttons, for a question the person answers from the notice itself (the LAN network question, the one-time LAN notice).
/// Presentation only, like <see cref="IUserNotifier"/>, but it says whether the notice was shown, because the caller then falls back to a
/// plain notice and the tray menu. Never throws.
/// </summary>
public interface IActionNotifier
{
    /// <summary>The id the system gives to a click on the notice itself, not on one of its buttons.</summary>
    const string DefaultActionId = "default";

    /// <summary>
    /// Shows the notice. <paramref name="onAction"/> is called, once, with the id of the button the person chose
    /// (<see cref="DefaultActionId"/> for a click on the notice); it is never called when the notice is dismissed.
    /// </summary>
    /// <returns>True when the system took the notice; false when it could not be shown (no permission, not an app bundle).</returns>
    Task<bool> TryNotifyAsync(string title, string message, IReadOnlyList<NoticeAction> actions, Action<string> onAction, CancellationToken cancellationToken = default);
}

/// <summary>What the "lock the node" request made when the computer went to sleep ended with.</summary>
/// <param name="Succeeded">The node was locked, or there was nothing to lock.</param>
/// <param name="Detail">Why it did not work (or that there was nothing to lock); null when it simply worked.</param>
/// <param name="HttpStatus">The node's HTTP status when it answered with one that was not a success; null otherwise (no answer, or a success).</param>
/// <param name="LogOnly">The vault was not locked, but for a reason the person cannot act on at the moment of sleep (the app did not start this node and holds no key for it): the reason is logged, no notice is shown.</param>
public sealed record SleepLockResult(bool Succeeded, string? Detail = null, int? HttpStatus = null, bool LogOnly = false);

/// <summary>The shell's request to the node to lock the vault, as the power-events services call it.</summary>
public delegate Task<SleepLockResult> SleepLockRequest(CancellationToken cancellationToken);
