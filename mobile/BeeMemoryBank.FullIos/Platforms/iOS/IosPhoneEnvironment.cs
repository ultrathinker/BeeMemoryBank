using BeeMemoryBank.Vault.Sealed;

namespace BeeMemoryBank.FullIos.Platforms.iOS;

/// <summary>What the phone says about the app for the blockers of "Encrypt this iPhone's database" (docs/design/step-b-spec.md 10.2, 10.8).</summary>
internal sealed class IosPhoneEnvironment : IPhoneEnvironment
{
    public bool IsForeground => AppFlow.IsActive;

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
