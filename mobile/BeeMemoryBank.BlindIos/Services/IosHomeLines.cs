using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>
/// The lines the iPhone screen shows on top of the shared <see cref="BlindHomeView"/>: when the copy last heard from its node, and the
/// newest problem since then. Worked out from the controller's status alone, so they are tested without a phone.
/// </summary>
public static class IosHomeLines
{
    /// <summary>The log kinds of a failed contact with the node: a sync round, a refused pin, a refused certificate.</summary>
    private static readonly string[] ContactProblems = ["sync", "pin", "certificate"];

    /// <summary>How far apart a refused key and the failed sync it caused can be logged.</summary>
    private static readonly TimeSpan SameRound = TimeSpan.FromSeconds(10);

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

    public static string LastContact(BlindAppStatus status, DateTimeOffset now)
    {
        if (status.LastSyncAt is not { } at)
            return status.IsPaired ? "Last contact with the node: not yet." : "Last contact with the node: never (not paired).";
        return $"Last contact with the node: {at.ToLocalTime():dd.MM.yyyy HH:mm} ({Ago(now - at)}).";
    }

    /// <summary>
    /// The newest problem the copy has now, or null: a failed first load or backup (the controller's <see cref="BlindAppStatus.LastFailure"/>),
    /// or a failed contact with the node that is newer than the last successful sync. The full story is in the log below it.
    /// </summary>
    public static string? LastProblem(BlindAppStatus status)
    {
        var problems = status.RecentLog
            .Where(e => ContactProblems.Contains(e.Kind) && (status.LastSyncAt is not { } ok || e.At > ok))
            .OrderByDescending(e => e.At)
            .ToList();
        var contact = problems.FirstOrDefault();
        // A refused pin or certificate is logged a moment before the sync failure it causes, and says more: show it instead.
        if (contact is { Kind: "sync" } failed &&
            problems.FirstOrDefault(e => e.Kind != "sync" && failed.At - e.At <= SameRound) is { } cause)
            contact = cause;
        var heavy = status.LastFailure;
        if (contact is null && heavy is null) return null;
        if (heavy is not null && (contact is null || heavy.At >= contact.At))
            return $"Problem ({heavy.At.ToLocalTime():dd.MM HH:mm}): {heavy.Title} failed - {OneLine(heavy.Message)}" +
                   (heavy.Attempts > 1 ? $" ({heavy.Attempts} tries in a row)." : "");
        return $"Problem ({contact!.At.ToLocalTime():dd.MM HH:mm}): {OneLine(contact.Message)}";
    }

    private static string Ago(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalMinutes: < 2 } => "1 minute ago",
        { TotalHours: < 1 } => $"{(int)span.TotalMinutes} minutes ago",
        { TotalHours: < 2 } => "1 hour ago",
        { TotalDays: < 1 } => $"{(int)span.TotalHours} hours ago",
        { TotalDays: < 2 } => "1 day ago",
        _ => $"{(int)span.TotalDays} days ago",
    };

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();
}
