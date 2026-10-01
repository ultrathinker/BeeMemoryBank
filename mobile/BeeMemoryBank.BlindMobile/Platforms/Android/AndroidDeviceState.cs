using Android.Content;
using Android.Net;
using Android.OS;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Platforms.Android;

/// <summary>Network, charger and battery for <see cref="BlindPhoneWork"/>, read fresh each time.</summary>
public sealed class AndroidDeviceState : IDeviceStateProvider
{
    public BlindPhoneDeviceState Current()
    {
        var context = global::Android.App.Application.Context;

        var cm = (ConnectivityManager?)context.GetSystemService(Context.ConnectivityService);
        var caps = cm?.GetNetworkCapabilities(cm.ActiveNetwork);
        var connected = caps?.HasCapability(NetCapability.Internet) == true;
        var unmetered = connected && caps!.HasCapability(NetCapability.NotMetered);

        // The sticky battery broadcast: no receiver is registered, the last value is returned.
        using var battery = context.RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
        var status = (BatteryStatus)(battery?.GetIntExtra(BatteryManager.ExtraStatus, -1) ?? -1);
        var charging = status is BatteryStatus.Charging or BatteryStatus.Full;
        var level = battery?.GetIntExtra(BatteryManager.ExtraLevel, -1) ?? -1;
        var scale = battery?.GetIntExtra(BatteryManager.ExtraScale, -1) ?? -1;
        var percent = level >= 0 && scale > 0 ? level * 100 / scale : 0;

        return new BlindPhoneDeviceState(connected, unmetered, charging, percent);
    }
}
