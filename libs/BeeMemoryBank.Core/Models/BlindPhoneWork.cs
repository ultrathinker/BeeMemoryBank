namespace BeeMemoryBank.Core.Models;

/// <summary>What an Android blind node does in the background.</summary>
public enum BlindPhoneJob
{
    /// <summary>Periodic sync with the listening node (WorkManager, at least every 15 minutes).</summary>
    Sync,
    /// <summary>The first download of the blind package — the whole network's data.</summary>
    InitialLoad,
    /// <summary>Writing a backup file.</summary>
    Backup,
}

/// <summary>The device state the conditions are judged on.</summary>
public sealed record BlindPhoneDeviceState(bool NetworkAvailable, bool Unmetered, bool Charging, int BatteryPercent);

/// <summary>
/// When an Android blind node may run each job (plan section 10). The phone is someone's phone first:
/// the first load and backups move the whole network's data, so they wait for Wi-Fi (an unmetered
/// network — a backup saved to a cloud folder is uploaded right after) and a charger, and a backup
/// never starts below 20 % battery. Sync is small and runs on any network. One rule for the scheduler,
/// the "Back up now" button and the screen that explains a wait.
/// </summary>
public static class BlindPhoneWork
{
    public const int MinBatteryPercentForBackup = 20;

    /// <summary>Null if <paramref name="job"/> may run now, otherwise why not — in words for the screen.</summary>
    public static string? WhyNot(BlindPhoneJob job, BlindPhoneDeviceState device)
    {
        if (!device.NetworkAvailable) return "No network connection.";
        if (job == BlindPhoneJob.Sync) return null;

        if (!device.Unmetered) return "Waiting for Wi-Fi.";
        if (!device.Charging) return "Waiting for the charger.";
        if (job == BlindPhoneJob.Backup && device.BatteryPercent < MinBatteryPercentForBackup)
            return $"Battery below {MinBatteryPercentForBackup} %.";
        return null;
    }
}
