using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using UserNotifications;

namespace BeeMemoryBank.BlindIos.Platforms.iOS;

/// <summary>
/// Keeps the pending "no contact" notifications of <see cref="BlindSilenceAlarm"/> in step with the accounts, ONE per account. Every decision is portable and tested: <see cref="SilenceRequestBook"/>
/// plans (only when what a notification depends on changed, so a screen that refreshes every few seconds does not keep pushing it away) and <see cref="SilenceReconciler"/> runs the whole read /
/// withdraw / add of an update, and the look at the requests that fired, as ONE ordered queue - from the screens and from a background wake alike - so that a late withdrawal can never delete a newer
/// add. This class only gives the reconciler the notification center and tells the screen whether alerts are allowed. Without permission the screen says that no warning can come.
/// </summary>
public sealed class IosSilenceNotifier
{
    private readonly SilenceReconciler _reconciler;

    public IosSilenceNotifier() => _reconciler = new SilenceReconciler(new NativeCenter(this));

    /// <summary>Null until iOS has answered; then whether alerts are allowed.</summary>
    public bool? Allowed { get; private set; }

    /// <summary>When the pending warning of the account will be shown if nothing moves it again; null when none is pending.</summary>
    public DateTimeOffset? NextWarningOf(string accountId) => _reconciler.Book.ShowsAt(BlindSilenceAlarm.RequestIdFor(Entry(accountId)));

    /// <summary>Hands the snapshot to the ordered queue and returns at once; the work is done in its turn (a snapshot that waits is replaced by a newer one).</summary>
    public void Update(BlindAccountsSnapshot snapshot) => _ = _reconciler.UpdateAsync(snapshot);

    /// <summary>Reads whether alerts are allowed (for the screen) without asking.</summary>
    public void Refresh() =>
        UNUserNotificationCenter.Current.GetNotificationSettings(settings =>
            SetAllowed(settings.AuthorizationStatus switch
            {
                UNAuthorizationStatus.Authorized or UNAuthorizationStatus.Provisional or UNAuthorizationStatus.Ephemeral => true,
                UNAuthorizationStatus.NotDetermined => null,
                _ => false,
            }));

    /// <summary>
    /// iOS refuses to queue a notification of an app that may not show any: once alerts are allowed (now, or later in Settings) the reconciler,
    /// in its own queue, plans again what is not pending and applies the newest snapshot at once - an add refused before the grant is made again by the grant itself.
    /// </summary>
    private void SetAllowed(bool? allowed)
    {
        Allowed = allowed;
        if (allowed == true) _ = _reconciler.AlertsAllowedAsync();
    }

    private static BlindAccountEntry Entry(string accountId) =>
        accountId == BlindAccountEntry.LegacyId
            ? new BlindAccountEntry(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "")
            : new BlindAccountEntry(accountId, BlindAccountLayouts.Dir, "");

    /// <summary>The notification center of the phone, as tasks: each one ends when the system has answered. Nothing else is decided here.</summary>
    private sealed class NativeCenter(IosSilenceNotifier owner) : ISilenceNotificationCenter
    {
        public Task<IReadOnlyList<string>> PendingIdsAsync()
        {
            var done = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            UNUserNotificationCenter.Current.GetPendingNotificationRequests(requests => done.TrySetResult(requests.Select(r => r.Identifier).ToList()));
            return done.Task;
        }

        public async Task RemoveAsync(IReadOnlyList<string> requestIds)
        {
            UNUserNotificationCenter.Current.RemovePendingNotificationRequests([.. requestIds]);
            // The call has no completion of its own: a read that comes back after it is the barrier that says the system has dealt with it before the next call is made.
            await PendingIdsAsync().ConfigureAwait(false);
        }

        public Task<bool> AddAsync(SilenceAdd add, TimeSpan after)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var content = new UNMutableNotificationContent { Title = add.Notice.Title, Body = add.Notice.Body, Sound = UNNotificationSound.Default };
            var trigger = UNTimeIntervalNotificationTrigger.CreateTrigger(after.TotalSeconds, repeats: false);
            // The same identifier replaces the pending one.
            UNUserNotificationCenter.Current.AddNotificationRequest(UNNotificationRequest.FromIdentifier(add.RequestId, content, trigger), error => done.TrySetResult(error is null));
            return done.Task;
        }

        public void RequestPermission() =>
            UNUserNotificationCenter.Current.RequestAuthorization(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound, (granted, _) => owner.SetAllowed(granted));
    }
}
