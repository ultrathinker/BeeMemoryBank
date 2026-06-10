namespace BeeMemoryBank.Mobile.Services;

/// <summary>
/// Persistent "pulse" of the background sync loop, written to Preferences so it survives the app
/// being backgrounded/killed and is readable when the user next opens the app — even off-charger or
/// after a reboot (where logcat is unavailable and Release builds emit no app logs). Used to find out
/// WHETHER and WHEN background sync actually runs on a given device.
/// </summary>
public static class SyncHeartbeat
{
    private const string KeyUtc = "bg_sync_last_utc";
    private const string KeyStatus = "bg_sync_last_status";
    private const string KeyCount = "bg_sync_count";
    private const string KeyServiceStartUtc = "bg_sync_service_start_utc";

    /// <summary>
    /// Record one completed sync cycle (success or failure). <paramref name="source"/> tags which
    /// mechanism ran it: "fg" = foreground service, "wm" = WorkManager backstop — so we can see which
    /// one keeps the device synced when the OEM kills the foreground service.
    /// </summary>
    public static void RecordCycle(bool ok, int applied, string? error, string source = "")
    {
        var tag = string.IsNullOrEmpty(source) ? "" : $" [{source}]";
        Preferences.Set(KeyUtc, DateTime.UtcNow.ToString("o"));
        Preferences.Set(KeyStatus, (ok ? $"ok (applied {applied})" : $"error: {Trim(error)}") + tag);
        Preferences.Set(KeyCount, Preferences.Get(KeyCount, 0) + 1);
    }

    /// <summary>Record that the foreground service (re)started — distinguishes restarts from steady cycles.</summary>
    public static void RecordServiceStart() =>
        Preferences.Set(KeyServiceStartUtc, DateTime.UtcNow.ToString("o"));

    public static (DateTime? lastCycleUtc, string status, int count, DateTime? serviceStartUtc) Read()
    {
        return (Parse(Preferences.Get(KeyUtc, "")),
                Preferences.Get(KeyStatus, "never"),
                Preferences.Get(KeyCount, 0),
                Parse(Preferences.Get(KeyServiceStartUtc, "")));
    }

    private static DateTime? Parse(string s) =>
        DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

    private static string Trim(string? s) => string.IsNullOrEmpty(s) ? "?" : (s.Length > 120 ? s[..120] : s);
}
