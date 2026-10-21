using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>
/// The lines the iPhone screens add to the shared view records of AppCore (<see cref="BlindAccountsView"/>, <see cref="BlindAccountCard"/>): what iOS lets the copies do in the background,
/// and the pairing steps of an account. Worked out from plain values, so that they are tested without a phone. Every other sentence on the Accounts, Add and Account pages comes from
/// AppCore and is only assigned to a control.
/// </summary>
public static class IosAccountsLines
{
    /// <summary>
    /// What the person should know about background time: nothing when there is no account; the reason when iOS gives the app none (Background App Refresh off, Low Power Mode); otherwise
    /// that iOS decides when, that each account is synced less often the more there are, and that opening the app syncs them all.
    /// </summary>
    /// <param name="refreshAvailable">UIApplication.BackgroundRefreshStatus is Available.</param>
    /// <param name="refreshStatus">The status in words, for the sentence when it is not available.</param>
    public static string? BackgroundNote(int accounts, bool refreshAvailable, string refreshStatus, bool lowPowerMode)
    {
        if (accounts <= 0) return null;
        if (!refreshAvailable)
            return $"Background App Refresh is off for this app ({refreshStatus}): {(accounts == 1 ? "the copy syncs" : "the copies sync")} only while the app is open. Settings > General > Background App Refresh turns it on.";
        if (lowPowerMode)
            return $"Low Power Mode is on: iOS gives {(accounts == 1 ? "the copy" : "the copies")} no background time until it is off.";
        return accounts == 1
            ? "iOS decides when the copy may sync in the background (a few short turns a day at best). Open the app to sync it now."
            : $"iOS gives the app a few short turns a day in the background (about 30 seconds each) and decides when. With {accounts} accounts each one syncs less often that way, the one that waited longest first. Open the app to sync all of them.";
    }

    /// <summary>The line that says why no warning about a silent node can come: nothing before an account is paired; the reason when notifications are off for the app.</summary>
    public static string? AlertsNote(bool anyAccountPaired, bool? alertsAllowed) =>
        anyAccountPaired && alertsAllowed == false
            ? "Notifications are off for this app: it cannot warn you when a node has been silent for three days. Settings > Notifications turns them on."
            : null;
}
