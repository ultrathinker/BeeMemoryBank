namespace BeeMemoryBank.Mobile.Services.Blind;

/// <summary>What this install of the app is (plan section 10): chosen once, at first start.</summary>
public enum DeviceMode
{
    /// <summary>An ordinary device: reads and writes notes, knows the master password.</summary>
    Full,
    /// <summary>A blind copy: keeps the network's encrypted data and backups, never the DEK or the password.</summary>
    Blind,
}

/// <summary>
/// Where the choice is kept. Null means not chosen yet — or an install from before the choice existed,
/// which the app treats as <see cref="DeviceMode.Full"/> once a node is initialized.
/// </summary>
public static class DeviceModeStore
{
    private const string Key = "bmb.device_mode";

    public static DeviceMode? Get() =>
        Enum.TryParse<DeviceMode>(Preferences.Default.Get<string?>(Key, null), out var mode) ? mode : null;

    public static void Set(DeviceMode mode) => Preferences.Default.Set(Key, mode.ToString());

    public static void Clear() => Preferences.Default.Remove(Key);

    public static bool IsBlind => Get() == DeviceMode.Blind;
}
