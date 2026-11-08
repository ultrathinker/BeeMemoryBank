using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.Services;

namespace BeeMemoryBank.Desktop.MacOS;

/// <summary>What the notification centre of the system does for the notifier; the real one is <see cref="UserNotificationsPort"/>, tests have their own.</summary>
internal interface IUserNotificationsPort
{
    /// <summary>False when this process cannot use the notification centre at all (not run from a signed .app bundle).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Raised, on any thread, when the person chose something on a notice made by <see cref="PostAsync"/>: its request id and the action's
    /// identifier as the system gave it (<see cref="MacOsActionNotifier.SystemDefaultAction"/> for a click on the notice itself).
    /// </summary>
    event Action<string, string>? ActionChosen;

    /// <summary>Asks the person for permission to show notices (the system shows its question the first time only). False: not granted, or no answer in time.</summary>
    Task<bool> RequestAuthorizationAsync(TimeSpan wait, CancellationToken cancellationToken);

    /// <summary>Hands the notice to the system under <paramref name="requestId"/>; false when the system refused it.</summary>
    Task<bool> PostAsync(string requestId, string title, string body, string categoryId, IReadOnlyList<NoticeAction> actions, CancellationToken cancellationToken);
}

/// <summary>
/// A notice with buttons on macOS: <c>UNUserNotificationCenter</c>, for the signed app (a process that is not run from an app bundle has no
/// notification centre; <see cref="TryNotifyAsync"/> then says false and the caller falls back to the banner and the tray item). The banner
/// shows its buttons when the person expands it (an alert-style notice shows them at once).
///
/// <para>This class is the logic: permission first, one request id per notice, the action that comes back found by that id and passed on
/// once. The calls into the system are <see cref="UserNotificationsPort"/>.</para>
/// </summary>
public sealed class MacOsActionNotifier : IActionNotifier
{
    /// <summary>The identifier the system gives to a click on the notice itself (<c>UNNotificationDefaultActionIdentifier</c>).</summary>
    public const string SystemDefaultAction = "com.apple.UNNotificationDefaultActionIdentifier";

    /// <summary>The identifier of a notice swiped away (<c>UNNotificationDismissActionIdentifier</c>); it is not an answer.</summary>
    public const string SystemDismissAction = "com.apple.UNNotificationDismissActionIdentifier";

    /// <summary>How long the permission question may take: the first time the person has to answer the system's own dialog.</summary>
    public static readonly TimeSpan AuthorizationWait = TimeSpan.FromSeconds(20);

    /// <summary>A notice whose action never came is forgotten after this long.</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(1);

    private readonly IUserNotificationsPort _port;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, (Action<string> OnAction, DateTime At)> _waiting = new();

    public MacOsActionNotifier() : this(new UserNotificationsPort(), TimeProvider.System, Console.Error.WriteLine) { }

    internal MacOsActionNotifier(IUserNotificationsPort port, TimeProvider time, Action<string> log)
    {
        _port = port;
        _time = time;
        _log = log;
        _port.ActionChosen += OnChosen;
    }

    public async Task<bool> TryNotifyAsync(string title, string message, IReadOnlyList<NoticeAction> actions, Action<string> onAction, CancellationToken cancellationToken = default)
    {
        string? id = null;
        try
        {
            if (!_port.IsAvailable) return false;
            if (!await _port.RequestAuthorizationAsync(AuthorizationWait, cancellationToken).ConfigureAwait(false)) return false;

            Forget();
            id = "bmb.notice." + Guid.NewGuid().ToString("N");
            _waiting[id] = (onAction, _time.GetUtcNow().UtcDateTime);
            if (await _port.PostAsync(id, title, message, CategoryFor(actions), actions, cancellationToken).ConfigureAwait(false)) return true;
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        catch (Exception ex)
        {
            // The type only: the text of a notice is not repeated into a log.
            Log($"The notification could not be shown ({ex.GetType().Name}).");
        }
        if (id != null) _waiting.TryRemove(id, out _);
        return false;
    }

    /// <summary>The category of a notice: the system keeps its buttons by category, so notices with the same buttons share one.</summary>
    internal static string CategoryFor(IReadOnlyList<NoticeAction> actions) => "bmb.actions." + string.Join('+', actions.Select(a => a.Id));

    private void OnChosen(string requestId, string systemAction)
    {
        if (!_waiting.TryRemove(requestId, out var waiting)) return; // a notice of an earlier run, or one already answered
        if (systemAction == SystemDismissAction) return;
        try { waiting.OnAction(systemAction == SystemDefaultAction ? IActionNotifier.DefaultActionId : systemAction); }
        catch (Exception ex) { Log($"A notice's action failed ({ex.GetType().Name})."); }
    }

    private void Forget()
    {
        var cutoff = _time.GetUtcNow().UtcDateTime - ForgetAfter;
        foreach (var (key, value) in _waiting)
            if (value.At < cutoff) _waiting.TryRemove(key, out _);
    }

    private void Log(string line)
    {
        try { _log($"[MacOsActionNotifier] {line}"); }
        catch { /* nothing to do */ }
    }
}
