namespace BeeMemoryBank.Crypto;

/// <summary>
/// How many password derivations one restore may spend on recovery boxes (BMB-43, plan 6.6). Every
/// box in a recovery set comes from a peer or from a file anyone could have replaced; each one the
/// password is tried on costs a full Argon2id run — up to 1 GiB for a strong box. Without a total,
/// several peers (one active box each) or a crafted set multiply that work without bound.
///
/// <para>The defaults: a mesh has one active strong box per PC and one device box per device.
/// <see cref="DefaultHeavy"/> strong attempts is about half a minute of 1 GiB derivations on a PC;
/// <see cref="DefaultLight"/> device attempts about ten seconds of 64 MiB ones. A restore that runs out
/// stops trying and says so — with the password it had, nothing more would have opened.</para>
///
/// <para><see cref="MaxPerKey"/> caps the attempts spent on boxes that claim the same key fingerprint:
/// a set padded with junk boxes for one (fake) key costs that many attempts, not the whole budget. Four
/// covers the legitimate case — several devices holding the same key under different passwords.</para>
/// </summary>
public sealed class RecoveryAttemptBudget(
    int maxHeavy = RecoveryAttemptBudget.DefaultHeavy,
    int maxLight = RecoveryAttemptBudget.DefaultLight,
    int maxPerKey = RecoveryAttemptBudget.DefaultPerKey)
{
    public const int DefaultHeavy = 6;
    public const int DefaultLight = 48;
    public const int DefaultPerKey = 4;

    public int MaxPerKey => maxPerKey;

    /// <summary>
    /// Every box, however many: only on the user's explicit "try the remaining boxes", which can be
    /// cancelled like any restore.
    /// </summary>
    public static RecoveryAttemptBudget Unlimited() => new(int.MaxValue, int.MaxValue, int.MaxValue);

    public int HeavyUsed { get; private set; }
    public int LightUsed { get; private set; }

    /// <summary>
    /// A class ran into its limit: its last attempt was taken, or an attempt was refused. Even when no box
    /// is left over, the search stood at its bound, so it proves nothing about what lies past it; a
    /// class never touched (limit 0, no boxes of it) has not run out.
    /// </summary>
    public bool HeavyLimitReached { get; private set; }
    public bool LightLimitReached { get; private set; }
    public bool AnyLimitReached => HeavyLimitReached || LightLimitReached;

    /// <summary>Takes one attempt for a box of <paramref name="preset"/>; false when none is left.</summary>
    public bool TryTake(RecoveryBoxPreset preset)
    {
        if (preset.IsHeavy)
        {
            if (HeavyUsed >= maxHeavy) { HeavyLimitReached = true; return false; }
            HeavyUsed++;
            if (HeavyUsed >= maxHeavy) HeavyLimitReached = true;
            return true;
        }
        if (LightUsed >= maxLight) { LightLimitReached = true; return false; }
        LightUsed++;
        if (LightUsed >= maxLight) LightLimitReached = true;
        return true;
    }
}
