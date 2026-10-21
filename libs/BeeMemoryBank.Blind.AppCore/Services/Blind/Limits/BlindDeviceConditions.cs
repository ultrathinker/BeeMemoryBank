namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// What the device says about itself that an account's two switches ask: whether the network in use is metered ("Only on Wi-Fi or other unmetered networks") and whether the device is saving power
/// ("Pause on battery saver"). Each head supplies the real adapter (Android ConnectivityManager and PowerManager, iOS NWPath and Low Power Mode, Windows connection cost and Energy Saver,
/// macOS Network.framework and low power); AppCore only asks. NULL MEANS THE DEVICE CANNOT SAY: with the switch on, an account then waits (fail closed toward the data plan of the
/// person who owns the device), with the switch off the answer is never asked for.
/// </summary>
public interface IBlindDeviceConditions
{
    /// <summary>True when the active network is metered, false when it is known not to be, null when this device cannot tell.</summary>
    bool? IsMetered { get; }

    /// <summary>True when the device is in a battery-saving mode, false when it is known not to be, null when it cannot tell.</summary>
    bool? IsPowerSaving { get; }
}

/// <summary>The conditions of a host that has no adapter: it cannot tell.</summary>
public sealed class UnknownBlindDeviceConditions : IBlindDeviceConditions
{
    public static readonly UnknownBlindDeviceConditions Instance = new();

    public bool? IsMetered => null;

    public bool? IsPowerSaving => null;
}

/// <summary>Conditions a test sets by hand (and a head's tests can use behind its adapter's seam).</summary>
public sealed class FakeBlindDeviceConditions : IBlindDeviceConditions
{
    public bool? IsMetered { get; set; }

    public bool? IsPowerSaving { get; set; }
}
