using Android.Content;
using AndroidX.Work;
using Java.Util.Concurrent;

namespace BeeMemoryBank.Mobile.Platforms.Android;

/// <summary>
/// Enqueues the periodic <see cref="SyncWorker"/>. Idempotent (ExistingPeriodicWorkPolicy.Keep) so it
/// can be called on every unlock and on boot without resetting the schedule. WorkManager persists the
/// schedule across reboots and process death by itself.
/// </summary>
public static class SyncWorkScheduler
{
    private const string WorkName = "bmb_periodic_sync";

    public static void Ensure(Context context)
    {
        try
        {
            var constraints = new Constraints.Builder()
                .SetRequiredNetworkType(NetworkType.Connected!)
                .Build();

            // 15 minutes is the Android minimum period for periodic work.
            var request = new PeriodicWorkRequest.Builder(
                    Java.Lang.Class.FromType(typeof(SyncWorker)), 15, TimeUnit.Minutes!)
                .SetConstraints(constraints)
                // On a transient sync failure the worker returns Retry — back off exponentially from
                // 30s instead of waiting the full 15-minute period.
                .SetBackoffCriteria(BackoffPolicy.Exponential!, 30, TimeUnit.Seconds!)
                .Build();

            WorkManager.GetInstance(context)
                .EnqueueUniquePeriodicWork(WorkName, ExistingPeriodicWorkPolicy.Keep!, request);
        }
        catch (System.Exception ex)
        {
            global::Android.Util.Log.Error("BeeSync", $"SyncWorkScheduler.Ensure failed: {ex.Message}");
        }
    }
}
