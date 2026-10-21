namespace BeeMemoryBank.Sync.Blind;

/// <summary>Why a blind copy stops syncing for a while. Words of the contract (they travel to the source as <c>paused=&lt;reason&gt;</c>): never translated, never changed.</summary>
public static class BlindPauseReasons
{
    /// <summary>The account's traffic of the day is used up.</summary>
    public const string Traffic = "traffic";

    /// <summary>The account's requests of the day are used up.</summary>
    public const string Requests = "requests";

    /// <summary>The account holds as many bytes as its limit allows.</summary>
    public const string Disk = "disk";

    /// <summary>The whole device's traffic of the day is used up.</summary>
    public const string DeviceTraffic = "device-traffic";

    /// <summary>The device's stored bytes, over every account, reached the device limit.</summary>
    public const string DeviceDisk = "device-disk";

    /// <summary>The network is metered (or this device cannot say) and the account is set to use unmetered networks only.</summary>
    public const string MeteredNetwork = "metered-network";

    /// <summary>The device is saving power (or cannot say) and the account is set to pause then.</summary>
    public const string PowerSaving = "power-saving";

    /// <summary>The person paused the account (or set a limit of zero).</summary>
    public const string PausedByYou = "paused-by-you";

    /// <summary>The account's usage counts exist and cannot be read or trusted, so its traffic and request limits cannot be checked (a person starts new counts on purpose).</summary>
    public const string UsageUnavailable = "usage-unavailable";

    /// <summary>The device's usage counts exist and cannot be read or trusted, so its traffic limit cannot be checked (a person starts new counts on purpose).</summary>
    public const string DeviceUsageUnavailable = "device-usage-unavailable";

    /// <summary>The bytes the copy holds cannot be measured (a folder cannot be read, or is a link), so its storage limit cannot be checked.</summary>
    public const string StorageUnknown = "storage-unknown";

    public static readonly IReadOnlyList<string> All =
        [Traffic, Requests, Disk, DeviceTraffic, DeviceDisk, MeteredNetwork, PowerSaving, PausedByYou, UsageUnavailable, DeviceUsageUnavailable, StorageUnknown];

    /// <summary>
    /// True when the source is told about a pause for this reason. Not for the network and the battery: the person said not to use that network, or not
    /// on battery saver, and the source needs no word of it (nothing is wrong, and nothing a limit of the data plan is its business). Not for the three that are a fault of
    /// this device's own files (unreadable counts, an unmeasurable folder): the source can do nothing about them and must not read them as the person's choice.
    /// </summary>
    public static bool IsReportable(string reason) =>
        reason is not (MeteredNetwork or PowerSaving or UsageUnavailable or DeviceUsageUnavailable or StorageUnknown) && All.Contains(reason);
}

/// <summary>
/// A reason to hold a copy's work, as the limit guard decides it. A pause is not a failure: nothing is wrong, the person's own limit or switch says "not now".
/// </summary>
/// <param name="Reason">One of <see cref="BlindPauseReasons"/>.</param>
/// <param name="Until">When the reason is known to end (the next local midnight for a daily limit); null when it ends with a change of the setting or of the conditions.</param>
/// <param name="Limit">The number the person set that was reached (bytes, or a count of requests), when there is one.</param>
/// <param name="Needed">For a refusal before a download starts: the bytes the download would need (stored bytes plus its size).</param>
/// <param name="ConditionUnknown">For the network and the battery: true when the device could not say (and the copy waits because the switch is on), false when it said so.</param>
public sealed record BlindPause(string Reason, DateTimeOffset? Until = null, long? Limit = null, long? Needed = null, bool ConditionUnknown = false)
{
    /// <summary>The reason in a short English phrase without times or numbers, for a log line or an exception: the screens use the richer text of the view records.</summary>
    public string Describe() => Reason switch
    {
        BlindPauseReasons.Traffic => "the daily traffic limit is reached",
        BlindPauseReasons.Requests => "the daily request limit is reached",
        BlindPauseReasons.Disk => "the storage limit is reached",
        BlindPauseReasons.DeviceTraffic => "the device's daily traffic limit is reached",
        BlindPauseReasons.DeviceDisk => "the device's storage limit is reached",
        BlindPauseReasons.MeteredNetwork => "this network is not known to be unmetered",
        BlindPauseReasons.PowerSaving => "the device is saving power",
        BlindPauseReasons.PausedByYou => "paused by you",
        BlindPauseReasons.UsageUnavailable => "the usage counts cannot be read",
        BlindPauseReasons.DeviceUsageUnavailable => "the device's usage counts cannot be read",
        BlindPauseReasons.StorageUnknown => "the stored size cannot be measured",
        _ => "paused",
    };
}

/// <summary>
/// Thrown where a limit says "stop": before a request goes out, while a response is read, before a download starts. A type of its own, never a message to be
/// parsed: the pull round, the first load, the controller and the scheduler all catch THIS and treat it as a pause (nothing counts as a failure, nothing is
/// retried at once), and anything that catches broader than this must let it through. It is not an <see cref="IOException"/> on purpose, so that the HTTP
/// stack does not rewrap it.
/// </summary>
public sealed class BlindPausedException(BlindPause pause) : Exception("Paused: " + pause.Describe()), IBlindOwnSentence
{
    public BlindPause Pause { get; } = pause;
}
