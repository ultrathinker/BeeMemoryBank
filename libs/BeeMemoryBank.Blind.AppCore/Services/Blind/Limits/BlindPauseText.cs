using System.Globalization;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// A pause in words for the screen, the log and the sentence a job returns: what is the matter, in the person's terms, and when it ends. One place, so that the card, the controller and
/// the scheduler never say it differently. Pure; the time zone is the device's (the caller passes <see cref="TimeProvider.LocalTimeZone"/>).
/// </summary>
public static class BlindPauseText
{
    /// <summary>The sentence of <paramref name="pause"/>, without the leading "Paused:" (the state line adds it where it wants it).</summary>
    /// <param name="zone">The device's time zone, for "Resumes at 00:00"; UTC when null.</param>
    /// <param name="now">When given, a resume time that is not today or tomorrow is told with its date.</param>
    public static string Say(BlindPause pause, TimeZoneInfo? zone = null, DateTimeOffset? now = null)
    {
        var resumes = Resumes(pause.Until, zone, now);
        return pause.Reason switch
        {
            BlindPauseReasons.Traffic when pause.Limit == 0 => "the daily traffic limit is set to 0.",
            BlindPauseReasons.Traffic => $"daily traffic limit reached ({BlindSizeFormat.Format(pause.Limit ?? 0)}).{resumes}",
            BlindPauseReasons.Requests when pause.Limit == 0 => "the daily request limit is set to 0.",
            BlindPauseReasons.Requests => $"daily request limit reached ({(pause.Limit ?? 0).ToString(CultureInfo.InvariantCulture)} requests).{resumes}",
            BlindPauseReasons.Disk when pause.Needed is { } needed =>
                $"not enough room: this copy needs about {BlindSizeFormat.Format(needed)} and the limit is {BlindSizeFormat.Format(pause.Limit ?? 0)}.",
            BlindPauseReasons.Disk when pause.Limit == 0 => "the storage limit is set to 0.",
            BlindPauseReasons.Disk => $"storage limit reached ({BlindSizeFormat.Format(pause.Limit ?? 0)}). Raise the limit to go on.",
            BlindPauseReasons.DeviceTraffic when pause.Limit == 0 => "the device's daily traffic limit is set to 0.",
            BlindPauseReasons.DeviceTraffic => $"the device's daily traffic limit is reached ({BlindSizeFormat.Format(pause.Limit ?? 0)}).{resumes}",
            BlindPauseReasons.DeviceDisk when pause.Needed is { } needed =>
                $"not enough room on this device: the copy needs about {BlindSizeFormat.Format(needed)} and the device limit is {BlindSizeFormat.Format(pause.Limit ?? 0)}.",
            BlindPauseReasons.DeviceDisk => $"the device's storage limit is reached ({BlindSizeFormat.Format(pause.Limit ?? 0)}).",
            BlindPauseReasons.MeteredNetwork when pause.ConditionUnknown => "waiting: this device does not say whether the network is metered.",
            BlindPauseReasons.MeteredNetwork => "waiting for an unmetered network (Wi-Fi).",
            BlindPauseReasons.PowerSaving when pause.ConditionUnknown => "waiting: this device does not say whether it is saving power.",
            BlindPauseReasons.PowerSaving => "waiting: the battery saver is on.",
            BlindPauseReasons.PausedByYou => "paused by you.",
            BlindPauseReasons.UsageUnavailable => "the usage counts of this account could not be read or saved, so its limits cannot be checked. Start new counts to go on: the damaged file is kept aside (the five newest kept copies stay).",
            BlindPauseReasons.DeviceUsageUnavailable => "the device's usage counts could not be read or saved, so its limits cannot be checked. Start new counts to go on: the damaged file is kept aside (the five newest kept copies stay).",
            BlindPauseReasons.StorageUnknown => "the stored size could not be measured (a folder cannot be read or is a link), so the storage limit cannot be checked.",
            _ => "paused.",
        };
    }

    /// <summary>
    /// "Paused: daily traffic limit reached (200 MB). Resumes at 00:00." (the sentence a job returns for a pause; it begins with a capital). The network and the battery are not a pause the
    /// person made but a wait: "Waiting for an unmetered network (Wi-Fi)."
    /// </summary>
    public static string Job(BlindPause pause, TimeZoneInfo? zone = null, DateTimeOffset? now = null)
    {
        var text = Say(pause, zone, now);
        return IsWaiting(pause) ? char.ToUpperInvariant(text[0]) + text[1..] : "Paused: " + text;
    }

    /// <summary>True for the reasons that are a fault of this device's own files (counts that cannot be read, a folder that cannot be measured), not a limit or a choice.</summary>
    public static bool IsFault(BlindPause pause) =>
        pause.Reason is BlindPauseReasons.UsageUnavailable or BlindPauseReasons.DeviceUsageUnavailable or BlindPauseReasons.StorageUnknown;

    /// <summary>True for the reasons that are a wait for the network or the battery.</summary>
    public static bool IsWaiting(BlindPause pause) => pause.Reason is BlindPauseReasons.MeteredNetwork or BlindPauseReasons.PowerSaving;

    /// <summary>" Resumes at 00:00." (today or tomorrow), " Resumes on 24 Oct at 00:00." (later); empty when the end is not known.</summary>
    private static string Resumes(DateTimeOffset? until, TimeZoneInfo? zone, DateTimeOffset? now)
    {
        if (until is not { } at) return "";
        var local = TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Utc);
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (now is { } n)
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(n, zone ?? TimeZoneInfo.Utc).DateTime);
            var day = DateOnly.FromDateTime(local.DateTime);
            if (day.DayNumber - today.DayNumber is > 1 or < 0)
                return $" Resumes on {local.ToString("d MMM", CultureInfo.InvariantCulture)} at {time}.";
        }
        return $" Resumes at {time}.";
    }
}
