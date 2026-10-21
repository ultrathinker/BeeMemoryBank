using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindMobile.Platforms.Android;

/// <summary>What the periodic WorkManager requests ask the system to wait for. Both false is the 2.6 rule: any connected network, any battery.</summary>
/// <param name="UnmeteredNetwork">Every account that syncs is "only on unmetered networks": the system wakes the phone only on Wi-Fi or another unmetered network.</param>
/// <param name="BatteryNotLow">Every account that syncs pauses on battery saver: the system does not wake the phone while the battery is low.</param>
public readonly record struct BlindWorkRequirements(bool UnmeteredNetwork, bool BatteryNotLow)
{
    public static readonly BlindWorkRequirements Default = new(false, false);
}

/// <summary>Platform-independent policy for the blind app's Android work requests.</summary>
public static class BlindWorkPlan
{
    public const bool RequiresConnectedNetwork = true;

    /// <summary>The phone never waits for the charger.</summary>
    public const bool RequiresCharging = false;

    public const bool UpdateExistingPeriodicWork = true;
    public const bool KeepExistingOneTimeWork = true;

    /// <summary>
    /// What the two periodic requests wait for, from the accounts: only when EVERY account that uses the network says so (an account that is paused by the person, being removed, refused, expired or
    /// removed by its owner uses none and is not counted). Fewer wake-ups for a phone whose accounts all wait for Wi-Fi; it is a saving, not a rule: the check at run time
    /// (<see cref="IBlindDeviceConditions"/>, asked by the guard before every round and every request) stays the authority, and a request the person makes on the screen waits for nothing but a network.
    /// </summary>
    public static BlindWorkRequirements For(IEnumerable<BlindAccountEntry> accounts)
    {
        var active = accounts.Where(UsesTheNetwork).ToList();
        if (active.Count == 0) return BlindWorkRequirements.Default;
        return new BlindWorkRequirements(
            UnmeteredNetwork: active.All(a => a.UnmeteredOnly),
            BatteryNotLow: active.All(a => a.PauseOnPowerSaving));
    }

    private static bool UsesTheNetwork(BlindAccountEntry account) =>
        !account.PausedByUser && account.Phase is BlindAccountPhases.Active or BlindAccountPhases.Pending or BlindAccountPhases.AwaitingConfirmation;
}
