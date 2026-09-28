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

    /// <summary>
    /// Whether this build offers the blind copy at all. Release A ships blind NODES (the PC and the
    /// container): the phone's side of it is unfinished — the identity row, the v=2 signer and the
    /// sync protocol behind them are still the Pending* stand-ins in MauiProgram — so the first-start
    /// choice is not shown, and the route to it is not taken. The code stays in place, behind this
    /// one switch, for the release that finishes it.
    /// </summary>
    public const bool BlindModeOffered = false;

    /// <summary>The stored choice, as written — no interpretation. <see cref="Effective"/> is what the
    /// app runs as.</summary>
    public static DeviceMode? Get() =>
        Enum.TryParse<DeviceMode>(Preferences.Default.Get<string?>(Key, null), out var mode) ? mode : null;

    /// <summary>
    /// The mode this install runs as. An install that chose the blind copy in a build that offered
    /// it — a dev build, never a release — is brought back to <see cref="DeviceMode.Full"/> here,
    /// once: leaving the stamp behind would land that install in a mode this build cannot run, and
    /// would drop it back into that mode silently if a later release offered the choice again.
    /// </summary>
    public static DeviceMode? Effective()
    {
        var mode = Get();
        if (mode == DeviceMode.Blind && !BlindModeOffered)
        {
            Clear();
            return null;
        }
        return mode;
    }

    public static void Set(DeviceMode mode) => Preferences.Default.Set(Key, mode.ToString());

    public static void Clear() => Preferences.Default.Remove(Key);

    /// <summary>No short-circuit on the switch: asking whether this install is a blind copy also
    /// brings a stale stamp back to Full (see <see cref="Effective"/>).</summary>
    public static bool IsBlind => Effective() == DeviceMode.Blind;
}
