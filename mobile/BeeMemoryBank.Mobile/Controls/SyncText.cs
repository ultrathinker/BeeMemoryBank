using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Mobile.Services;
using BeeMemoryBank.Sync.Channels;

namespace BeeMemoryBank.Mobile.Controls;

/// <summary>
/// The sync screens' own words (en + ru in AppResources), never the raw English sentence from the sync
/// layer. A computer that is simply not reachable is said in yellow ("... is not reachable for now"),
/// a real failure in red; the badge and the cards read the same codes SyncStatusService classifies.
/// The same helper as the iPhone's Controls/SyncText.
/// </summary>
public static class SyncText
{
    /// <summary>The yellow "not reachable for now" line for one computer.</summary>
    public static string Unreachable(string name) => AppStrings.Format("SyncUnreachableFormat", name);

    /// <summary>The sentence for a peer's last failure code, in the app's words; null when there is none.</summary>
    public static string? Problem(string? code, string name) => code switch
    {
        null => null,
        SyncStatusService.ProblemUnreachable => Unreachable(name),
        SyncStatusService.ProblemWrongKey => AppStrings.Get("SyncProblemWrongKey"),
        SyncStatusService.ProblemRefused => AppStrings.Get("SyncProblemRefused"),
        SyncStatusService.ProblemDisconnected => AppStrings.Get("SyncProblemDisconnected"),
        SyncStatusService.ProblemBusy => AppStrings.Get("SyncProblemBusy"),
        SyncStatusService.ProblemSnapshot => AppStrings.Get("SyncProblemSnapshot"),
        _ => AppStrings.Get("SyncProblemOther"),
    };

    /// <summary>The reachability line for a peer card, in the app's words; null where the contact line says it.</summary>
    public static string? Reachability(string state, string name) => state switch
    {
        ReachabilityStates.DirectLan => AppStrings.Get("SyncReachDirectLan"),
        ReachabilityStates.DirectInternet => AppStrings.Get("SyncReachDirectInternet"),
        ReachabilityStates.Away => AppStrings.Get("SyncReachAway"),
        ReachabilityStates.OldVersion => AppStrings.Format("SyncReachOldVersion", name),
        ReachabilityStates.ViaRelay => AppStrings.Get("SyncReachViaRelay"),
        _ => null,
    };

    /// <summary>Whether a reachability state means "the computer is not reachable for now" (yellow, not an error).</summary>
    public static bool IsUnreachableState(string state) => state is ReachabilityStates.NotAnswering or ReachabilityStates.Silent;
}
