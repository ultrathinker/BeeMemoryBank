using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Windows;

/// <summary>The cost class of the connection, as <c>NetworkCostType</c> names it.</summary>
public enum WindowsCostType { Unknown, Unrestricted, Fixed, Variable }

/// <summary>What <c>ConnectionCost</c> of the internet connection profile says.</summary>
public sealed record WindowsConnectionCost(WindowsCostType Type, bool Roaming, bool OverDataLimit, bool ApproachingDataLimit);

/// <summary>The state of Windows' Energy Saver (battery saver), as <c>EnergySaverStatus</c> names it.</summary>
public enum WindowsEnergySaver { Uninitialized, Disabled, Off, On }

/// <summary>Where <see cref="WindowsDeviceConditions"/> reads Windows from; a test answers by hand.</summary>
public interface IWindowsConditionsSource
{
    /// <summary>The cost of the internet connection, or null when there is no internet connection profile (offline, or a host that has none).</summary>
    WindowsConnectionCost? ReadConnectionCost();

    WindowsEnergySaver ReadEnergySaver();
}

/// <summary>
/// The two answers the account switches ask of Windows (<see cref="IBlindDeviceConditions"/>): is the network in use METERED ("Only on Wi-Fi or other unmetered networks"), and is
/// the computer SAVING POWER ("Pause on battery saver").
///
/// <para><b>Metered</b> is read from <c>NetworkInformation.GetInternetConnectionProfile().GetConnectionCost()</c>: a connection that is not <c>Unrestricted</c> (fixed or variable
/// cost), or that is roaming, over its data limit or close to it, is metered; an unrestricted one is not. A cost that Windows does not know, or no profile at all, is NOT
/// guessed either way: the answer is "cannot tell" (null), and an account with the switch on then waits (<see cref="IBlindDeviceConditions"/>: unknown is never read as free).
/// Ethernet is not metered by default, so it is allowed; a Wi-Fi network the person marked "metered connection" is not.</para>
///
/// <para><b>Power saving</b> is <c>PowerManager.EnergySaverStatus == On</c>. <c>Off</c> and <c>Disabled</c> (a machine with no battery, or Energy Saver turned off) are not saving
/// power; <c>Uninitialized</c> is "cannot tell". A machine without a battery (a desktop, a server) therefore answers "not saving power" and the switch never holds it.</para>
///
/// <para>Windows is asked at most every 2 seconds: the guard asks before every request, and a download is many requests. A source that throws answers "cannot tell" - it never
/// breaks the sync loop - except for the exceptions that must not be swallowed.</para>
/// </summary>
public sealed class WindowsDeviceConditions : IBlindDeviceConditions
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(2);

    private readonly IWindowsConditionsSource _source;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private (DateTimeOffset At, bool? Metered, bool? PowerSaving)? _last;

    public WindowsDeviceConditions() : this(new WinRtConditionsSource())
    {
    }

    public WindowsDeviceConditions(IWindowsConditionsSource source, TimeProvider? time = null)
    {
        _source = source;
        _time = time ?? TimeProvider.System;
    }

    public bool? IsMetered => Read().Metered;

    public bool? IsPowerSaving => Read().PowerSaving;

    /// <summary>The mapping of a connection cost to "metered": null when it cannot be told.</summary>
    public static bool? Metered(WindowsConnectionCost? cost)
    {
        if (cost is null) return null;
        if (cost.Roaming || cost.OverDataLimit || cost.ApproachingDataLimit) return true;
        return cost.Type switch
        {
            WindowsCostType.Unrestricted => false,
            WindowsCostType.Fixed or WindowsCostType.Variable => true,
            _ => null,
        };
    }

    /// <summary>The mapping of the Energy Saver state to "saving power": null when it cannot be told.</summary>
    public static bool? SavingPower(WindowsEnergySaver status) => status switch
    {
        WindowsEnergySaver.On => true,
        WindowsEnergySaver.Off or WindowsEnergySaver.Disabled => false,
        _ => null,
    };

    private (bool? Metered, bool? PowerSaving) Read()
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_last is { } last && now - last.At < CacheFor && now >= last.At) return (last.Metered, last.PowerSaving);
            var metered = Safely(() => Metered(_source.ReadConnectionCost()));
            var power = Safely(() => SavingPower(_source.ReadEnergySaver()));
            _last = (now, metered, power);
            return (metered, power);
        }
    }

    private static bool? Safely(Func<bool?> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException or ThreadAbortException)) { return null; }
    }
}

/// <summary>The real source: the WinRT network and power APIs (the head targets net10.0-windows10.0.19041.0, which has them).</summary>
internal sealed class WinRtConditionsSource : IWindowsConditionsSource
{
    public WindowsConnectionCost? ReadConnectionCost()
    {
        var profile = global::Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
        var cost = profile?.GetConnectionCost();
        if (cost is null) return null;
        return new WindowsConnectionCost(
            cost.NetworkCostType switch
            {
                global::Windows.Networking.Connectivity.NetworkCostType.Unrestricted => WindowsCostType.Unrestricted,
                global::Windows.Networking.Connectivity.NetworkCostType.Fixed => WindowsCostType.Fixed,
                global::Windows.Networking.Connectivity.NetworkCostType.Variable => WindowsCostType.Variable,
                _ => WindowsCostType.Unknown,
            },
            cost.Roaming, cost.OverDataLimit, cost.ApproachingDataLimit);
    }

    public WindowsEnergySaver ReadEnergySaver() => global::Windows.System.Power.PowerManager.EnergySaverStatus switch
    {
        global::Windows.System.Power.EnergySaverStatus.On => WindowsEnergySaver.On,
        global::Windows.System.Power.EnergySaverStatus.Off => WindowsEnergySaver.Off,
        global::Windows.System.Power.EnergySaverStatus.Disabled => WindowsEnergySaver.Disabled,
        _ => WindowsEnergySaver.Uninitialized,
    };
}
