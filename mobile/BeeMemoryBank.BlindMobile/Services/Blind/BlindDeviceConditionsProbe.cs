namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// What the device says about its network and its power, from two probes the platform supplies (Android: <c>ConnectivityManager</c> and <c>PowerManager</c>). A probe that cannot answer
/// (no active network, no capabilities, a system service that threw) is NULL, never a guess: with a switch on, the guard then waits (fail closed toward the data plan of the person who owns
/// the phone). The probes are asked each time, because the answer changes with the network the phone is on.
/// </summary>
/// <param name="notMetered">True when the active network is known to be unmetered, false when it is known to be metered, null when the device cannot tell.</param>
/// <param name="powerSaving">True when the device is in battery saver, false when it is not, null when it cannot tell.</param>
public sealed class ProbedBlindDeviceConditions(Func<bool?> notMetered, Func<bool?> powerSaving) : IBlindDeviceConditions
{
    public bool? IsMetered => Ask(notMetered) is { } unmetered ? !unmetered : null;

    public bool? IsPowerSaving => Ask(powerSaving);

    private static bool? Ask(Func<bool?> probe)
    {
        try { return probe(); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { return null; }
    }
}
