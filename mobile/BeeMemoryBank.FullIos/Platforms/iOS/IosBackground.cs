using UIKit;

namespace BeeMemoryBank.FullIos.Platforms.iOS;

/// <summary>
/// Work that must finish after the app leaves the screen (the last sync round, the lock after it): run inside a background-task
/// assertion, the few seconds iOS grants a leaving app. When iOS takes the time back, the work is cancelled and the assertion ended.
/// </summary>
internal static class IosBackground
{
    public static void Run(string name, Func<CancellationToken, Task> work)
    {
        var app = UIApplication.SharedApplication;
        var cancel = new CancellationTokenSource();
        nint assertion = UIApplication.BackgroundTaskInvalid;
        var gate = new object();
        void End()
        {
            lock (gate)
            {
                if (assertion == UIApplication.BackgroundTaskInvalid) return;
                app.EndBackgroundTask(assertion);
                assertion = UIApplication.BackgroundTaskInvalid;
            }
        }
        assertion = app.BeginBackgroundTask(name, () =>
        {
            cancel.Cancel();
            End();
        });
        _ = Task.Run(async () =>
        {
            try { await work(cancel.Token); }
            finally
            {
                MainThread.BeginInvokeOnMainThread(End);
            }
        });
    }
}
