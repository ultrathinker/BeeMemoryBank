namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// "Lock the vault when this computer sleeps": the person's choice, stored in desktop-settings.json with the other per-app flags (keep
/// awake, update checks). Off by default; a settings file without the key reads as off. The shell asks <see cref="IsEnabled"/> at the
/// moment of each sleep event, never once at start-up, so changing it takes effect at once.
/// </summary>
public sealed class LockOnSleepSetting(DesktopSettingsStore store)
{
    public const string SettingKey = "lockOnSleep";

    /// <summary>The text of the setting in every UI, the same on Windows and macOS.</summary>
    public const string Label = "Lock the vault when this computer sleeps";

    public const string Hint = "After waking up you sign in again.";

    public bool IsEnabled
    {
        get => store.GetBool(SettingKey, defaultValue: false);
        set => store.SetBool(SettingKey, value);
    }
}
