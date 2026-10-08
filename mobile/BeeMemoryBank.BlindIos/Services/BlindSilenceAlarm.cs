using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>A local notification the phone schedules for itself.</summary>
public sealed record BlindSilenceNotice(DateTimeOffset FireAt, string Title, string Body);

/// <summary>
/// "The node has been silent too long", without a push server: every time the copy hears from its node (a successful sync), the app
/// moves ONE pending local notification to that moment plus <see cref="SilentAfter"/>. While the node answers, the notification keeps
/// moving ahead and never shows; when the node goes silent - or iOS stops giving the app background time, which looks the same from the
/// phone - nothing moves it any more and iOS shows it on time, even if the app never runs again. The desktop apps raise the same alarm
/// for a blind copy that has not called for three days.
/// </summary>
public static class BlindSilenceAlarm
{
    public const string RequestId = "bmb-blind-silent";

    /// <summary>How long without contact before the phone says so (the desktop alarm's three days).</summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromDays(3);

    /// <summary>When the silence has already lasted that long, the next reminder is not sooner than this.</summary>
    public static readonly TimeSpan RemindAfter = TimeSpan.FromDays(1);

    /// <summary>
    /// The notification that should be pending now, or null when none should (not paired: there is no node to miss). A paired copy that has
    /// never synced counts from <paramref name="now"/>: the time it was last seen running.
    /// </summary>
    public static BlindSilenceNotice? Plan(BlindAppStatus status, DateTimeOffset now)
    {
        if (!status.IsPaired) return null;
        var heardAt = status.LastSyncAt ?? now;
        var fireAt = heardAt + SilentAfter;
        // Already silent that long (the app was opened, or woken, without reaching the node): say it again a day later, not at once and
        // not on every refresh.
        if (fireAt <= now) fireAt = now + RemindAfter;
        var since = status.LastSyncAt is { } last
            ? $"since {last.ToLocalTime():dd.MM.yyyy HH:mm}"
            : "since it was paired";
        return new BlindSilenceNotice(fireAt, "Blind copy: no contact with the node",
            $"This iPhone has not synced with its node {since}. Its copy may be out of date: open the app so it can sync, and check that the computer or server it calls is on.");
    }
}
