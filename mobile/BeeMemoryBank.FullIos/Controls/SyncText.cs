using BeeMemoryBank.FullIos.Localization;
using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Sync.Channels;

namespace BeeMemoryBank.FullIos.Controls;

/// <summary>
/// The sync screens' own words (en + ru in AppResources), never the raw English sentence from the sync
/// layer. A computer that is simply not reachable is said in yellow ("... is not reachable for now"),
/// a real failure in red; the badge and the cards read the same codes FullSync classifies.
/// </summary>
public static class SyncText
{
    /// <summary>The yellow "not reachable for now" line for one computer.</summary>
    public static string Unreachable(string name) => AppStrings.Format("SyncUnreachableFormat", name);

    /// <summary>The sentence for a peer's last failure code, in the app's words; null when there is none.</summary>
    public static string? Problem(string? code, string name) => code switch
    {
        null => null,
        FullSync.ProblemUnreachable => Unreachable(name),
        FullSync.ProblemWrongKey => AppStrings.Get("SyncProblemWrongKey"),
        FullSync.ProblemRefused => AppStrings.Get("SyncProblemRefused"),
        FullSync.ProblemDisconnected => AppStrings.Get("SyncProblemDisconnected"),
        FullSync.ProblemBusy => AppStrings.Get("SyncProblemBusy"),
        FullSync.ProblemSnapshot => AppStrings.Get("SyncProblemSnapshot"),
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

    /// <summary>The last-contact line of a peer card, in the app's words (the service's Summary is English, for the log and the tests).</summary>
    public static string Contact(PeerStatus peer)
    {
        var line = peer.LastSuccess is { } ok
            ? AppStrings.Format("SyncContactFormat", ok.ToLocalTime().ToString("HH:mm:ss"))
            : AppStrings.Get("SyncNoContact");
        return peer.Pinned ? line + " · " + AppStrings.Get("SyncKeyPinned") : line;
    }

    /// <summary>One channel row of the peer page, in the app's words: where the address came from and how it worked last.</summary>
    public static string ChannelLine(PeerChannelItem item)
    {
        var source = item.SourceCode switch
        {
            ChannelSources.Manual => AppStrings.Get("PeerSourceManual"),
            ChannelSources.Primary => AppStrings.Get("PeerSourcePrimary"),
            ChannelSources.Announced => AppStrings.Get("PeerSourceAnnounced"),
            ChannelSources.Mdns => AppStrings.Get("PeerSourceMdns"),
            _ => item.SourceCode,
        };
        var worked = item.LastOkAt is { } ok ? AppStrings.Format("PeerWorkedFormat", Ago(ok)) : AppStrings.Get("PeerNeverWorked");
        var detail = item.UsedFirst ? AppStrings.Format("PeerUsedFirstFormat", worked)
            : item.Dialled ? worked
            : item.LanAway ? worked + ", " + AppStrings.Get("PeerNotTriedLan")
            : worked + ", " + AppStrings.Get("PeerNotTriedWrong");
        return source + " · " + detail;
    }

    /// <summary>The outcome of "Add an address", in the app's words; never the service's English sentence.</summary>
    public static string AddAddressText(AddAddressResult result, bool blind) => result.Outcome switch
    {
        AddAddressOutcome.Added => AppStrings.Get(result.Answered ? "PeerAddSavedAnswered" : "PeerAddSavedSilent"),
        AddAddressOutcome.NotFound => AppStrings.Get("PeerAddNotFound"),
        AddAddressOutcome.Refused => AppStrings.Get(blind ? "PeerAddBlind" : "PeerAddRefused"),
        AddAddressOutcome.WrongPeer => AppStrings.Get("PeerAddWrongPeer"),
        AddAddressOutcome.NotSyncServer => AppStrings.Get("PeerAddNotSyncServer"),
        AddAddressOutcome.NoAnswer => AppStrings.Get("PeerAddNoAnswer"),
        AddAddressOutcome.TooMany => AppStrings.Format("PeerAddTooManyFormat", PeerChannelStore.MaxManualPerPeer),
        _ => result.Text,
    };

    private static string Ago(DateTime at)
    {
        var span = DateTime.UtcNow - at;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span < TimeSpan.FromMinutes(1)) return AppStrings.Get("TimeJustNow");
        if (span < TimeSpan.FromHours(1)) return AppStrings.Format("TimeMinutesAgoFormat", (int)span.TotalMinutes);
        if (span < TimeSpan.FromDays(1)) return AppStrings.Format("TimeHoursAgoFormat", (int)span.TotalHours);
        return AppStrings.Format("TimeDaysAgoFormat", (int)span.TotalDays);
    }
}

/// <summary>A channel row as the peer page shows it: the address and the app's own words for it.</summary>
public sealed record PeerChannelDisplay(string Address, string Line, bool ThisIPhoneOnly);
