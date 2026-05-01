using Android.App;
using Android.Content;

namespace BeeMemoryBank.Mobile.Platforms.Android;

[BroadcastReceiver(Exported = true, DirectBootAware = false)]
[IntentFilter(new[] { Intent.ActionBootCompleted, "android.intent.action.QUICKBOOT_POWERON" })]
public class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null) return;
        var serviceIntent = new Intent(context, typeof(SyncForegroundService));
        context.StartForegroundService(serviceIntent);
    }
}
