namespace BeeMemoryBank.FullIos.Services;

/// <summary>
/// When the open vault closes by itself: after the app has been away from the screen longer than <see cref="BackgroundGrace"/>, and
/// after <see cref="IdleTimeout"/> on the screen without a touch. iOS gives a suspended app no time to run a timer, so leaving is
/// judged when the app comes back (and the app locks at once if it is woken any other way); the idle time is checked by the app's own
/// timer while it is in front. The grace exists because opening the vault costs seconds of Argon2id on a phone; with zero it locks at
/// once, as the Android app does. Time comes from the caller, so the tests need no clock.
/// </summary>
public sealed class AutoLockPolicy
{
    public static readonly TimeSpan[] GraceChoices = [TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    public static readonly TimeSpan[] IdleChoices = [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    public TimeSpan BackgroundGrace { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    private DateTime? _leftAt;
    private DateTime _lastActivity = DateTime.MinValue;

    /// <summary>A touch, a key press, a page opened: the person is using the app.</summary>
    public void Activity(DateTime now) => _lastActivity = now;

    /// <summary>The app left the screen (home button, another app, the phone locked).</summary>
    public void Left(DateTime now) => _leftAt ??= now;

    /// <summary>The app is back on the screen: true when the vault must be locked before anything is shown.</summary>
    public bool Returned(DateTime now)
    {
        var leftAt = _leftAt;
        _leftAt = null;
        if (leftAt is null) return false;
        var away = now - leftAt.Value;
        // A clock that went backwards (time zone, manual change) proves nothing about how long the app was away: lock.
        if (away < TimeSpan.Zero || away > BackgroundGrace) return true;
        _lastActivity = now;
        return false;
    }

    /// <summary>True when the app has been on the screen without a touch for longer than <see cref="IdleTimeout"/>.</summary>
    public bool IsIdle(DateTime now) => _lastActivity != DateTime.MinValue && now - _lastActivity > IdleTimeout;

    /// <summary>Text for a choice on the settings page.</summary>
    public static string Describe(TimeSpan span) =>
        span == TimeSpan.Zero ? "Immediately" : span.TotalMinutes == 1 ? "After 1 minute" : $"After {span.TotalMinutes:0} minutes";
}
