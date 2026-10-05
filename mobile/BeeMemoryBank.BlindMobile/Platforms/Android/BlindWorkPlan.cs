namespace BeeMemoryBank.BlindMobile.Platforms.Android;

/// <summary>Platform-independent policy for the blind app's Android work requests.</summary>
public static class BlindWorkPlan
{
    public const bool RequiresConnectedNetwork = true;
    public const bool RequiresCharging = false;
    public const bool RequiresBatteryNotLow = false;
    public const bool UpdateExistingPeriodicWork = true;
    public const bool KeepExistingOneTimeWork = true;
}
