using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>
/// The one line the iPhone screen adds on top of the shared <see cref="BlindHomeView"/> that is iOS's own: when the phone will warn about a silent node, or why it cannot. (The last contact and the
/// newest problem are the shared view's own, <c>BlindHomeView.Sync</c> and <c>BlindHomeView.Problem</c>: a screen binds them and builds no sentence of its own.) Tested without a phone.
/// </summary>
public static class IosHomeLines
{
    /// <summary>The line that says when the phone will warn about a silent node, or why it cannot.</summary>
    public static string? SilenceWarning(BlindAppStatus status, DateTimeOffset? nextWarning, bool? alertsAllowed)
    {
        if (!status.IsPaired) return null;
        if (alertsAllowed == false)
            return "Notifications are off for this app: it cannot warn you when the node has been silent for three days. Settings > Notifications turns them on.";
        return nextWarning is { } at
            ? $"If the node stays silent, this iPhone warns you on {at.ToLocalTime():dd.MM.yyyy HH:mm}."
            : null;
    }
}
