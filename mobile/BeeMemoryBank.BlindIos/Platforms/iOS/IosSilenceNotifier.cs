using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using UserNotifications;

namespace BeeMemoryBank.BlindIos.Platforms.iOS;

/// <summary>
/// Keeps the one pending "no contact with the node" notification of <see cref="BlindSilenceAlarm"/> in step with the copy: it is moved only
/// when what it depends on changed (paired or not, the time of the last sync), so a screen that refreshes every few seconds does not keep
/// pushing it away. Asks for permission once the copy is paired; without permission the screen says that no warning can come.
/// </summary>
public sealed class IosSilenceNotifier
{
    private readonly object _gate = new();
    private (bool Paired, DateTimeOffset? LastSync)? _planned;
    private bool _asked;

    /// <summary>Null until iOS has answered; then whether alerts are allowed.</summary>
    public bool? Allowed { get; private set; }

    /// <summary>When the pending warning will be shown if nothing moves it again; null when none is pending.</summary>
    public DateTimeOffset? NextWarning { get; private set; }

    public void Update(BlindAppStatus status)
    {
        var key = (status.IsPaired, status.LastSyncAt);
        lock (_gate)
        {
            if (_planned == key) return;
            _planned = key;
        }

        var center = UNUserNotificationCenter.Current;
        var notice = BlindSilenceAlarm.Plan(status, DateTimeOffset.UtcNow);
        if (notice is null)
        {
            center.RemovePendingNotificationRequests([BlindSilenceAlarm.RequestId]);
            NextWarning = null;
            return;
        }

        AskOnce();
        var content = new UNMutableNotificationContent { Title = notice.Title, Body = notice.Body, Sound = UNNotificationSound.Default };
        var seconds = Math.Max(60, (notice.FireAt - DateTimeOffset.UtcNow).TotalSeconds);
        var trigger = UNTimeIntervalNotificationTrigger.CreateTrigger(seconds, repeats: false);
        // The same identifier replaces the pending one.
        center.AddNotificationRequest(UNNotificationRequest.FromIdentifier(BlindSilenceAlarm.RequestId, content, trigger),
            error => NextWarning = error is null ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null);
    }

    /// <summary>Reads whether alerts are allowed (for the screen) without asking.</summary>
    public void Refresh() =>
        UNUserNotificationCenter.Current.GetNotificationSettings(settings =>
            SetAllowed(settings.AuthorizationStatus switch
            {
                UNAuthorizationStatus.Authorized or UNAuthorizationStatus.Provisional or UNAuthorizationStatus.Ephemeral => true,
                UNAuthorizationStatus.NotDetermined => null,
                _ => false,
            }));

    private void AskOnce()
    {
        lock (_gate)
        {
            if (_asked) return;
            _asked = true;
        }
        UNUserNotificationCenter.Current.RequestAuthorization(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound,
            (granted, _) => SetAllowed(granted));
    }

    /// <summary>
    /// iOS refuses to queue a notification of an app that may not show any: once alerts are allowed (now, or later in Settings) and none is
    /// pending, the next <see cref="Update"/> plans it again instead of waiting for the next sync to change the plan.
    /// </summary>
    private void SetAllowed(bool? allowed)
    {
        Allowed = allowed;
        if (allowed == true && NextWarning is null)
            lock (_gate) _planned = null;
    }
}
