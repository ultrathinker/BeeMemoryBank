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

        // WorkManager backstop FIRST — it's the resilient path if the OEM blocks the foreground
        // service from auto-starting after boot (and it persists across reboots regardless).
        SyncWorkScheduler.Ensure(context);

        try
        {
            var serviceIntent = new Intent(context, typeof(SyncForegroundService));
            context.StartForegroundService(serviceIntent);
        }
        catch (System.Exception ex)
        {
            // Some OEMs disallow starting a foreground service from BOOT_COMPLETED — WorkManager covers it.
            global::Android.Util.Log.Error("BeeSync", $"BootReceiver StartForegroundService failed: {ex.Message}");
        }
    }
}
