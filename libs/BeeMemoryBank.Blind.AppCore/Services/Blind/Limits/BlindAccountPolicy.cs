namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>What the person set for one account: its limits and its switches, as the limit guard reads them (<see cref="BlindAccountEntry"/> holds the same numbers in <c>accounts.json</c>).</summary>
public sealed record BlindAccountPolicy(BlindAccountLimits Limits, bool UnmeteredOnly = false, bool PauseOnPowerSaving = false, bool PausedByUser = false)
{
    /// <summary>No limit, any network, not paused: what a copy that was never given a policy has always been.</summary>
    public static readonly BlindAccountPolicy Unlimited = new(BlindAccountLimits.None);

    public static BlindAccountPolicy Of(BlindAccountEntry entry) =>
        new(entry.EffectiveLimits, entry.UnmeteredOnly, entry.PauseOnPowerSaving, entry.PausedByUser);
}

/// <summary>Where the guard reads the account's current policy: read at every check, so that a changed setting takes effect at once.</summary>
public interface IBlindAccountPolicySource
{
    BlindAccountPolicy Current { get; }
}

/// <summary>The policy of a composition that has no account registry: none.</summary>
public sealed class NoLimitsPolicySource : IBlindAccountPolicySource
{
    public static readonly NoLimitsPolicySource Instance = new();

    public BlindAccountPolicy Current => BlindAccountPolicy.Unlimited;
}

/// <summary>The policy of one account of an <see cref="BlindAccountHost"/>: whatever its entry in the registry says now.</summary>
internal sealed class RegistryPolicySource(BlindAccountRegistry registry, string accountId) : IBlindAccountPolicySource
{
    public BlindAccountPolicy Current =>
        registry.Current.Find(accountId) is { } entry ? BlindAccountPolicy.Of(entry) : BlindAccountPolicy.Unlimited;
}
