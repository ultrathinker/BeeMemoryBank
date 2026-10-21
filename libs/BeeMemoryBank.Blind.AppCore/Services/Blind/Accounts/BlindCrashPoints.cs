namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// A named place between two writes of a multi-step change (adopting the 2.6 install, adding an account, removing one, the registry's own
/// write and rename). Production passes nothing and every place is a no-op; a test passes a recorder that throws at the Nth place, which is how
/// "the process died after every write" is tested: the same start-up code is then run again over the folder the dead run left behind.
/// </summary>
public interface IBlindCrashPoints
{
    /// <summary>Called when the step <paramref name="step"/> has finished (or, for steps named "before ...", is about to begin).</summary>
    void Reach(string step);
}

/// <summary>The place nobody listens to.</summary>
public sealed class NoCrashPoints : IBlindCrashPoints
{
    public static readonly NoCrashPoints Instance = new();

    public void Reach(string step) { }
}
