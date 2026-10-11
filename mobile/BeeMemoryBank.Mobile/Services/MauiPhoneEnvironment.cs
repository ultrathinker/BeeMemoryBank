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
}
