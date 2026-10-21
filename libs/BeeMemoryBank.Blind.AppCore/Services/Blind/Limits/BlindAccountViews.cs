using System.Globalization;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>How a card should look at a glance: the colour or icon a head chooses, never the words (those are in <see cref="BlindAccountCard.State"/>).</summary>
public enum BlindCardKind
{
    /// <summary>Synced, nothing to do.</summary>
    Ok,

    /// <summary>Something is running or about to (a round, a first load, starting up).</summary>
    Working,

    /// <summary>Waiting for the person or the computer, or for the network or the battery.</summary>
    Waiting,

    /// <summary>Held by a limit or by the person: calm, nothing is wrong.</summary>
    Paused,

    /// <summary>Something is wrong and the sentence says what to do.</summary>
    Problem,

    /// <summary>Being removed.</summary>
    Removing,
}

/// <summary>
/// One short card per account, worked out from one <see cref="BlindAccountSnapshot"/> and nothing else: the screens only assign these strings to their controls. ONE state line says what the
/// account is doing or why it is not; the numbers are rounded and say "about" where they are estimates (the bodies of requests are exact, headers and TLS are guessed). No charts.
/// </summary>
/// <param name="Name">What the person calls it.</param>
/// <param name="SourceHost">The host of the computer it calls ("mem.example.org"), or empty before it is paired.</param>
/// <param name="State">The one state line: "Synced 4 min ago", "Paused: daily traffic limit reached (200 MB). Resumes at 00:00." ...</param>
/// <param name="Stored">"1.2 GB of 2 GB" (or "1.2 GB" with no limit).</param>
/// <param name="Today">"312 requests, about 38 MB of 200 MB".</param>
/// <param name="Average">"about 40 MB a day over the last 7 days".</param>
/// <param name="LastSync">"Last sync: 4 min ago" or "Last sync: never".</param>
public sealed record BlindAccountCard(
    string Id, string Name, string SourceHost, string State, BlindCardKind Kind,
    string Stored, string Today, string Average, string LastSync,
    bool IsPaused, bool CanSyncNow, bool CanEdit, bool CanRepairUsage = false)
{
    /// <summary>The card of <paramref name="account"/>; the clock and the time zone are the device's.</summary>
    public static BlindAccountCard Build(BlindAccountSnapshot account, TimeZoneInfo zone, DateTimeOffset now)
    {
        var entry = account.Entry;
        var status = account.Status;
        var usage = account.Usage;
        var limits = entry.EffectiveLimits;
        var (state, kind) = StateLine(account, zone, now);

        return new BlindAccountCard(
            entry.Id, entry.Name, HostOf(status?.Endpoint) ?? HostOf(entry.SourceOrigin) ?? "",
            state, kind,
            Stored: StoredText(usage, limits.MaxStoredBytes),
            Today: TodayText(usage, limits),
            Average: usage?.AverageTrafficPerDay is { } average
                ? $"about {BlindSizeFormat.Format(average)} a day over the last 7 days"
                : "No traffic counted yet",
            LastSync: $"Last sync: {(status?.LastSyncAt is { } at ? Ago(now - at) : "never")}",
            IsPaused: account.Pause is not null || entry.PausedByUser,
            CanSyncNow: status is { IsPaired: true, InitialLoadDone: true, ActiveJob: null } && account.Pause is null && entry.Phase == BlindAccountPhases.Active,
            CanEdit: entry.Phase != BlindAccountPhases.Deleting,
            CanRepairUsage: usage?.Problem is not null);
    }

    private static (string State, BlindCardKind Kind) StateLine(BlindAccountSnapshot account, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (account.Problem is { } problem) return (problem, BlindCardKind.Problem);

        switch (account.Entry.Phase)
        {
            case BlindAccountPhases.Deleting: return ("Removing this account...", BlindCardKind.Removing);
            // An account added from an invitation (it carries the invitation's id) names the source; one paired the old way says "the owner".
            case BlindAccountPhases.Pending:
                return (account.Note ?? BlindInvitationWords.Connecting(HostOf(account.Entry.SourceOrigin) ?? "the owner's computer"), BlindCardKind.Waiting);
            case BlindAccountPhases.AwaitingConfirmation:
                return (account.Note ?? BlindInvitationWords.WaitingFor(OwnerName(account.Entry), account.Entry.MatchCode), BlindCardKind.Waiting);
            case BlindAccountPhases.Refused: return (BlindInvitationWords.Refused(Capitalised(OwnerName(account.Entry))), BlindCardKind.Problem);
            case BlindAccountPhases.Expired: return (BlindInvitationWords.ExpiredCard, BlindCardKind.Problem);
            case BlindAccountPhases.RemovedByOwner: return (BlindInvitationWords.RemovedByOwner(Capitalised(OwnerName(account.Entry))), BlindCardKind.Problem);
        }

        if (account.Status is not { } status) return ("Starting...", BlindCardKind.Working);
        if (status.StartError is { } error) return ($"Could not start: {BlindFailureText.ForDisplay(error)}", BlindCardKind.Problem);
        if (status.KeyStoreUnavailable is not null) return ("The key storage of this device did not answer. Nothing was changed; try again in a moment.", BlindCardKind.Problem);
        if (status.BackupKeyLost) return ("Keys missing - remove this account and add it again.", BlindCardKind.Problem);

        if (account.Pause is { } pause)
        {
            return (BlindPauseText.Job(pause, zone, now),
                BlindPauseText.IsWaiting(pause) ? BlindCardKind.Waiting : BlindPauseText.IsFault(pause) ? BlindCardKind.Problem : BlindCardKind.Paused);
        }

        if (status.ActiveJob is { } job)
            return (status.JobProgress is { } progress ? $"{job}: {(int)Math.Round(Math.Clamp(progress, 0, 1) * 100)}%" : $"{job}...", BlindCardKind.Working);
        // The source said no once and the copy asks again: neutral, because a proxy or a firewall in front of a healthy source can say no for a moment. Not when the first load has failed since
        // (the network is down, the source answers 5xx): only a success forgets the refusal, and a card that kept saying "asking again" would hide the newer, real failure below.
        var loadFailedSince = !status.InitialLoadDone && status.LastFailure is { Title: "First load" } failed && status.SourceRefusedAt is { } refused && failed.At > refused;
        if (!loadFailedSince && BlindSourceRemoval.RefusalIsRecent(status.SourceRefusedAt, now)) return (BlindSourceRemoval.FirstRefusalSentence, BlindCardKind.Waiting);
        if (!status.IsPaired)
            return (status.AwaitingAnswer ? "Waiting for the computer's answer: give it the code this account shows." : "Not paired yet.", BlindCardKind.Waiting);
        if (!status.InitialLoadDone)
            return (status.LastFailure is { Title: "First load" } failure
                ? $"First load failed: {BlindFailureText.WithoutFullStop(BlindFailureText.ForDisplay(failure.Message))}. It tries again soon."
                : "First load: waiting to start.", BlindCardKind.Working);
        return status.LastSyncAt is { } at ? ($"Synced {Ago(now - at)}", BlindCardKind.Ok) : ("Not synced yet.", BlindCardKind.Waiting);
    }

    private static string StoredText(BlindAccountUsageView? usage, long? limit) => usage is null ? "" : usage.StoredBytes is null ? "unknown" : StoredText(usage.StoredBytes, limit);

    private static string StoredText(long? stored, long? limit) => (stored, limit) switch
    {
        (null, _) => "",
        ({ } held, { } max) => $"{BlindSizeFormat.Format(held)} of {BlindSizeFormat.Format(max)}",
        ({ } held, null) => BlindSizeFormat.Format(held),
    };

    private static string TodayText(BlindAccountUsageView? usage, BlindAccountLimits limits)
    {
        if (usage is null) return "";
        if (usage.Problem is not null) return "usage counts unavailable";
        var requests = limits.MaxRequestsPerDay is { } maxRequests
            ? $"{usage.RequestsToday} of {maxRequests} requests"
            : $"{usage.RequestsToday} requests";
        var traffic = limits.MaxTrafficBytesPerDay is { } maxTraffic
            ? $"about {BlindSizeFormat.Format(usage.TrafficToday)} of {BlindSizeFormat.Format(maxTraffic)}"
            : $"about {BlindSizeFormat.Format(usage.TrafficToday)}";
        return $"{requests}, {traffic}";
    }

    /// <summary>"just now", "4 min ago", "3 h ago", "2 d ago".</summary>
    internal static string Ago(TimeSpan age) =>
        age < TimeSpan.FromMinutes(1) ? "just now"
        : age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes} min ago"
        : age < TimeSpan.FromDays(1) ? $"{(int)age.TotalHours} h ago"
        : $"{(int)age.TotalDays} d ago";

    private static string? HostOf(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : null;

    /// <summary>What the person calls the source (the account's name, which starts as the invitation's) when the account came from an invitation, otherwise "the owner".</summary>
    private static string OwnerName(BlindAccountEntry entry) => entry.InviteId is not null ? entry.Name : "the owner";

    private static string Capitalised(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>
/// The accounts screen: one card per account, the device line, and whether "Add account" is on. From one <see cref="BlindAccountsSnapshot"/>. When the registry is damaged there are no cards and
/// <see cref="Problem"/> says what happened (the app opens on that sentence and touches no account).
/// </summary>
/// <param name="DeviceLine">"3 accounts - 3.1 GB stored - today 41 MB".</param>
/// <param name="AddHint">Why "Add account" is off, or empty.</param>
/// <param name="Repairs">What the list of accounts and the disk disagree on, each with the sentence for the screen (the repair state); empty when they agree.</param>
/// <param name="DeviceUsageProblem">Set when the device's own usage counts cannot be read or trusted (the sentence for the screen); the repair is <see cref="IBlindAccountsController.StartNewDeviceUsageLedger"/>.</param>
public sealed record BlindAccountsView(IReadOnlyList<BlindAccountCard> Cards, string DeviceLine, bool CanAdd, string? Problem, string AddHint, IReadOnlyList<BlindRepairIssue>? Repairs = null,
    string? DeviceUsageProblem = null)
{
    public bool CanRepairDeviceUsage => DeviceUsageProblem is not null;

    public static BlindAccountsView Build(BlindAccountsSnapshot snapshot, TimeZoneInfo zone, DateTimeOffset now)
    {
        var cards = snapshot.Accounts.Select(a => BlindAccountCard.Build(a, zone, now)).ToList();
        var count = cards.Count;
        var parts = new List<string> { count switch { 0 => "No accounts", 1 => "1 account", _ => $"{count} accounts" } };

        // A size that is unknown stays unknown: nothing is summed over it as if it were zero.
        long? stored = snapshot.DeviceUsage is { } deviceUsage ? deviceUsage.StoredBytes
            : snapshot.Accounts.Any(a => a.Usage is { StoredBytes: null }) ? null
            : snapshot.Accounts.Sum(a => a.Usage?.StoredBytes ?? 0);
        var traffic = snapshot.DeviceUsage?.TrafficToday ?? snapshot.Accounts.Sum(a => a.Usage?.TrafficToday ?? 0);
        if (count > 0)
        {
            parts.Add(stored is not { } known ? "stored size unknown"
                : snapshot.Device.MaxStoredBytes is { } maxStored
                    ? $"{BlindSizeFormat.Format(known)} of {BlindSizeFormat.Format(maxStored)} stored"
                    : $"{BlindSizeFormat.Format(known)} stored");
            parts.Add(snapshot.Device.MaxTrafficBytesPerDay is { } maxTraffic
                ? $"today {BlindSizeFormat.Format(traffic)} of {BlindSizeFormat.Format(maxTraffic)}"
                : $"today {BlindSizeFormat.Format(traffic)}");
        }

        var hint = snapshot.RegistryProblem is not null ? ""
            : snapshot.CanAdd ? ""
            : count >= snapshot.MaxAccounts ? $"This device holds at most {snapshot.MaxAccounts} accounts. Remove one to add another."
            : "The app is still starting.";
        return new BlindAccountsView(cards, string.Join(" - ", parts), snapshot.CanAdd, snapshot.RegistryProblem, hint, snapshot.RepairIssues, snapshot.DeviceUsage?.Problem);
    }
}

/// <summary>What the confirmation of "Remove this account" says: what goes, and who is told.</summary>
public sealed record BlindRemoveConfirmation(string Title, string Message, string ConfirmText, string CancelText)
{
    public static BlindRemoveConfirmation Build(BlindAccountSnapshot account)
    {
        var name = account.Entry.Name;
        var size = account.Usage?.StoredBytes is { } held ? BlindSizeFormat.Format(held) : "Its data";
        var told = account.Status?.IsPaired == true
            ? $"{name} is told if this device is online."
            : "It never connected, so nobody is told.";
        return new BlindRemoveConfirmation(
            $"Remove {name}?",
            $"Remove the copy of {name}? {size} and its keys are deleted from this device. {told}",
            "Remove", "Keep it");
    }
}

/// <summary>The limits and switches a person may start an account with, or reach after the upgrade (see <see cref="BlindAccountDefaults"/>).</summary>
public sealed record BlindAccountSettings(BlindAccountLimits Limits, bool UnmeteredOnly, bool PauseOnPowerSaving);

/// <summary>
/// The starting settings. An invited account is protected by default (the friend's data plan and storage): 2 GB stored, 200 MB a day, no request limit, and on a phone only on unmetered
/// networks and paused on battery saver. Account 1 after the upgrade has no limit and any network: nothing changes for an install that was there before.
/// </summary>
public static class BlindAccountDefaults
{
    public const long InvitedStoredBytes = 2L << 30;
    public const long InvitedTrafficPerDay = 200L << 20;

    public static BlindAccountSettings ForInvited(bool isPhone) =>
        new(new BlindAccountLimits(InvitedStoredBytes, InvitedTrafficPerDay), UnmeteredOnly: isPhone, PauseOnPowerSaving: isPhone);

    public static BlindAccountSettings ForUpgradedAccountOne => new(BlindAccountLimits.None, UnmeteredOnly: false, PauseOnPowerSaving: false);
}

/// <summary>
/// What a person is shown before an invitation is accepted (<see cref="IBlindAccountsController.PreviewInvitation"/>): whose copy it is, from which computer, what this device will allow it and
/// how long the invitation works - or why it is not taken (<see cref="Refusal"/>, one of <see cref="BlindInvitationRefusals"/> as <see cref="RefusalKind"/>). The text is honest about what an invitation
/// is (a one-time invitation, not an encrypted one) and about what this device can read (nothing).
/// </summary>
/// <param name="ExpiresAt">When the invitation stops working (the hub's clock); null when the text was not read.</param>
/// <param name="Refusal">The sentence that says why the invitation is not taken; null when it is.</param>
/// <param name="RefusalKind">One of <see cref="BlindInvitationRefusals"/>; null when the invitation is taken.</param>
public sealed record BlindInvitationPreview(string SourceName, string SourceHost, BlindAccountSettings Settings,
    DateTimeOffset? ExpiresAt = null, string? Refusal = null, string? RefusalKind = null)
{
    /// <summary>True when the invitation can be added now.</summary>
    public bool CanAdd => Refusal is null;

    /// <summary>
    /// What the invitation is, in two plain halves: the COPY it gives is kept encrypted and cannot be read on this device; the INVITATION itself is a one-time code and is not encrypted
    /// (it works once, for a short time, and is useless without the owner's confirmation). A screen shows this sentence as it is.
    /// </summary>
    public string Description =>
        $"A copy of {SourceName}'s Bee Memory Bank, kept encrypted: this device cannot read it. It comes from {SourceHost}. This invitation is a one-time code, not an encrypted one.";

    /// <summary>How long it still works, in words (empty when the text was not read).</summary>
    public string Validity(DateTimeOffset now) => ExpiresAt is { } at ? BlindInvitationWords.ValidFor(at, now) : "";

    public string Limits
    {
        get
        {
            var parts = new List<string>();
            if (Settings.Limits.MaxStoredBytes is { } stored) parts.Add($"at most {BlindSizeFormat.Format(stored)} of storage");
            if (Settings.Limits.MaxTrafficBytesPerDay is { } traffic) parts.Add($"{BlindSizeFormat.Format(traffic)} a day");
            if (Settings.Limits.MaxRequestsPerDay is { } requests) parts.Add($"{requests} requests a day");
            if (Settings.UnmeteredOnly) parts.Add("only on Wi-Fi or other unmetered networks");
            if (Settings.PauseOnPowerSaving) parts.Add("paused on battery saver");
            return parts.Count == 0 ? "No limits are set." : "It will use " + string.Join(", ", parts) + ".";
        }
    }
}

/// <summary>What <see cref="BlindLimitsForm.Validate"/> found: the limits to store, or the sentences that say what to change.</summary>
public sealed record BlindLimitsResult(BlindAccountLimits? Limits, IReadOnlyList<string> Problems)
{
    public bool Ok => Problems.Count == 0 && Limits is not null;
}

/// <summary>
/// The limits form of one account: what a person types ("2 GB", "200 MB", "5000"), parsed and checked in one place. An empty box is no limit; <b>0 is a pause</b>, never "unlimited". A sync round
/// runs every 15 minutes (about 500 requests a day, a few MB), so a request limit below 200 a day or a traffic limit below 10 MB a day would stop the copy from keeping up: the form refuses
/// them and says why. A device limit is the same form without the request box (<see cref="BlindDeviceLimitsForm"/>).
/// </summary>
public sealed record BlindLimitsForm(string Stored, string TrafficPerDay, string RequestsPerDay, bool UnmeteredOnly, bool PauseOnPowerSaving)
{
    public const int MinRequestsPerDay = 200;
    public const long MinTrafficPerDay = 10L << 20;

    /// <summary>What the limits form tells the person, under its boxes: what empty and 0 mean, how a size is written, and why too little is refused. A screen shows it as it is.</summary>
    public static string Explanation =>
        $"Leave a box empty for no limit; 0 pauses the account. Sizes are written like 2 GB or 500 MB. A sync round runs every 15 minutes (about 500 requests and a few MB a day), so fewer than {MinRequestsPerDay} requests or {BlindSizeFormat.Format(MinTrafficPerDay)} a day are refused: the copy could not keep up.";

    /// <summary>The form as it should be shown for an account's current limits.</summary>
    public static BlindLimitsForm From(BlindAccountEntry entry)
    {
        var limits = entry.EffectiveLimits;
        return new BlindLimitsForm(
            limits.MaxStoredBytes is { } s ? BlindSizeFormat.Format(s) : "",
            limits.MaxTrafficBytesPerDay is { } t ? BlindSizeFormat.Format(t) : "",
            limits.MaxRequestsPerDay is { } r ? r.ToString(CultureInfo.InvariantCulture) : "",
            entry.UnmeteredOnly, entry.PauseOnPowerSaving);
    }

    public BlindLimitsResult Validate()
    {
        var problems = new List<string>();
        var stored = ParseSize(Stored, "Storage", problems);
        var traffic = ParseSize(TrafficPerDay, "Daily traffic", problems);
        if (traffic is > 0 and < MinTrafficPerDay)
            problems.Add($"Daily traffic: less than {BlindSizeFormat.Format(MinTrafficPerDay)} a day would stop the copy keeping up (it syncs every 15 minutes). Write at least {BlindSizeFormat.Format(MinTrafficPerDay)}, or 0 to pause the account.");

        int? requests = null;
        var requestText = (RequestsPerDay ?? "").Trim();
        if (requestText.Length > 0)
        {
            if (!int.TryParse(requestText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                problems.Add("Daily requests: write a whole number, for example 5000, or 0 to pause the account.");
            else if (parsed is > 0 and < MinRequestsPerDay)
                problems.Add($"Daily requests: a sync round runs every 15 minutes, about 500 requests a day. Fewer than {MinRequestsPerDay} a day would stop the copy keeping up. Write at least {MinRequestsPerDay}, or 0 to pause the account.");
            else
                requests = parsed;
        }

        return problems.Count > 0
            ? new BlindLimitsResult(null, problems)
            : new BlindLimitsResult(new BlindAccountLimits(stored, traffic, requests), problems);
    }

    internal static long? ParseSize(string? text, string what, List<string> problems)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return null;
        if (BlindSizeFormat.TryParse(trimmed, out var bytes, out var problem)) return bytes;
        problems.Add($"{what}: {problem}");
        return null;
    }
}

/// <summary>The device's limits form: stored bytes and traffic a day, over every account.</summary>
public sealed record BlindDeviceLimitsForm(string Stored, string TrafficPerDay)
{
    /// <summary>What the device's limits form tells the person, under its boxes. A screen shows it as it is.</summary>
    public static string Explanation =>
        $"Leave a box empty for no limit; 0 pauses the whole device. Sizes are written like 10 GB or 500 MB. Fewer than {BlindSizeFormat.Format(BlindLimitsForm.MinTrafficPerDay)} a day is refused: no copy could keep up. An account has limits of its own too; the smaller one holds.";

    public static BlindDeviceLimitsForm From(BlindDeviceLimits limits) => new(
        limits.MaxStoredBytes is { } s ? BlindSizeFormat.Format(s) : "",
        limits.MaxTrafficBytesPerDay is { } t ? BlindSizeFormat.Format(t) : "");

    /// <summary>The limits to store, or the sentences that say what to change.</summary>
    public (BlindDeviceLimits? Limits, IReadOnlyList<string> Problems) Validate()
    {
        var problems = new List<string>();
        var stored = BlindLimitsForm.ParseSize(Stored, "Device storage", problems);
        var traffic = BlindLimitsForm.ParseSize(TrafficPerDay, "Device daily traffic", problems);
        if (traffic is > 0 and < BlindLimitsForm.MinTrafficPerDay)
            problems.Add($"Device daily traffic: less than {BlindSizeFormat.Format(BlindLimitsForm.MinTrafficPerDay)} a day would stop every copy keeping up. Write at least {BlindSizeFormat.Format(BlindLimitsForm.MinTrafficPerDay)}, or 0 to pause the whole device.");
        return problems.Count > 0 ? (null, problems) : (new BlindDeviceLimits(stored, traffic), problems);
    }
}
