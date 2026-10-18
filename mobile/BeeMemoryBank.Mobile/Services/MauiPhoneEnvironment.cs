using BeeMemoryBank.Vault.Sealed;

namespace BeeMemoryBank.Mobile.Services;

/// <summary>
/// What the phone says about the app for the blockers of "Encrypt this phone's database" (docs/design/step-b-spec.md 10.2 <c>mobile_foreground</c>,
/// 10.8): the app's own foreground flag (set by <c>App</c>) and MAUI's battery.
/// </summary>
public sealed class MauiPhoneEnvironment : IPhoneEnvironment
{
    public static volatile bool Foreground = true;

    public bool IsForeground => Foreground;

    public int? BatteryPercent
    {
        get
        {
            var level = Battery.Default.ChargeLevel;
            return level < 0 ? null : (int)Math.Round(level * 100);
        }
    }

    public bool IsCharging => Battery.Default.State is BatteryState.Charging or BatteryState.Full;

    /// <summary>
    /// The free space of the file system that holds the app's data folder. The generic answer (the drive of the path root) is the read-only system
    /// partition on Android, which has none, so a phone with gigabytes free was told "not enough free space".
    /// </summary>
    public long? FreeBytes(string path)
    {
#if ANDROID
        try { return new Android.OS.StatFs(path).AvailableBytes; }
        catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException or IOException or UnauthorizedAccessException) { return null; }
#else
        return null;
#endif
    }
}
