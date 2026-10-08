using BackgroundTasks;
using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using Foundation;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindIos.Platforms.iOS;

/// <summary>
/// The blind copy's background time on iOS, through BGTaskScheduler (see <see cref="IosBackgroundRounds"/> for what each task does and
/// what iOS promises - which is little). Both requests are submitted again whenever the app goes to the background and at the start of
/// every round, so one is always pending; the handler ends the round when iOS says the time is up (the job pauses and resumes later).
/// </summary>
public sealed class IosBlindBackground(IServiceProvider services) : IIosBackgroundRequests
{
    /// <summary>The state key of the last background round (time, task and result), for the screen.</summary>
    public const string LastRoundKey = "bmb.ios.last_background_round";

    private int _registered;

    /// <summary>Registers both handlers; AppDelegate calls it before launching ends. Twice would throw in iOS, so the second call does nothing.</summary>
    public void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        BGTaskScheduler.Shared.Register(IosBackgroundRounds.SyncTaskId, null, task => Handle(task, work: false));
        BGTaskScheduler.Shared.Register(IosBackgroundRounds.WorkTaskId, null, task => Handle(task, work: true));
    }

    public void Submit()
    {
        try
        {
            var sync = new BGAppRefreshTaskRequest(IosBackgroundRounds.SyncTaskId)
            {
                EarliestBeginDate = (NSDate)DateTime.UtcNow.Add(IosBackgroundRounds.SyncNotBefore),
            };
            BGTaskScheduler.Shared.Submit(sync, out var syncError);

            // Network only: like the other copies, a long job never waits for the charger (iOS still prefers to run it while the phone
            // is idle and charging).
            var work = new BGProcessingTaskRequest(IosBackgroundRounds.WorkTaskId)
            {
                EarliestBeginDate = (NSDate)DateTime.UtcNow.Add(IosBackgroundRounds.WorkNotBefore),
                RequiresNetworkConnectivity = true,
                RequiresExternalPower = false,
            };
            BGTaskScheduler.Shared.Submit(work, out var workError);

            if ((syncError ?? workError) is { } error) Log($"iOS refused a background request: {error.LocalizedDescription}");
        }
        catch (Exception ex)
        {
            Log($"Asking iOS for background time failed ({ex.GetType().Name}): {ex.Message}");
        }
    }

    public void CancelAll() => BGTaskScheduler.Shared.CancelAll();

    private void Handle(BGTask task, bool work)
    {
        // The next requests first: a round that iOS ends early, or a failure below, must not leave none pending.
        Submit();
        var stop = new CancellationTokenSource();
        task.ExpirationHandler = () => stop.Cancel();
        var name = work ? "processing window" : "app refresh";
        _ = Task.Run(async () =>
        {
            var completed = false;
            IosBlindRuntime? runtime = null;
            try
            {
                runtime = services.GetRequiredService<IosBlindHost>().Current;
                var result = work
                    ? await IosBackgroundRounds.RunWorkAsync(runtime.App, stop.Token)
                    : await IosBackgroundRounds.RunSyncAsync(runtime.App, stop.Token);
                Remember(runtime, name, result);
                completed = true;
            }
            catch (OperationCanceledException)
            {
                if (runtime is not null) Remember(runtime, name, "ended by iOS before it finished; it continues later");
            }
            catch (Exception ex)
            {
                if (runtime is not null) BlindRunReport.RecordFailure(runtime.Services.GetRequiredService<BlindPhoneLog>(), "background", ex);
            }
            finally
            {
                try
                {
                    if (runtime is not null) services.GetService<IosSilenceNotifier>()?.Update(runtime.App.GetStatus());
                }
                catch (Exception) { /* the notification is a convenience */ }
                task.SetTaskCompleted(completed);
                stop.Dispose();
            }
        });
    }

    private static void Remember(IosBlindRuntime runtime, string task, string result)
    {
        try { runtime.Services.GetRequiredService<IBlindStateStore>().Set(LastRoundKey, IosLastRound.Format(DateTimeOffset.UtcNow, task, result)); }
        catch (Exception) { /* the screen's line only */ }
    }

    private void Log(string message)
    {
        try { BlindRunReport.Record(services.GetRequiredService<IosBlindHost>().Current.Services.GetRequiredService<BlindPhoneLog>(), "background", message); }
        catch (Exception) { /* nowhere left to tell it */ }
    }
}
