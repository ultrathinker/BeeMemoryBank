using BackgroundTasks;
using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using Foundation;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindIos.Platforms.iOS;

/// <summary>
/// The blind app's background time on iOS, through BGTaskScheduler (see <see cref="IosBackgroundRounds"/> for what each task does and
/// what iOS promises - which is little). The two task identifiers are fixed in Info.plist and are NOT per account: a wake serves the
/// accounts of the phone in turn, the one that synced longest ago first (<see cref="IosBackgroundRounds"/>). Both requests are submitted
/// again whenever the app goes to the background and at the start of every round, so one is always pending while an account needs it; the
/// handler ends the round when iOS says the time is up (the job pauses and resumes later).
/// </summary>
public sealed class IosBlindBackground(IServiceProvider services) : IIosBackgroundRequests
{
    /// <summary>The state key of the last background round of an account (time, task and result), for its screen.</summary>
    public const string LastRoundKey = IosLastRound.StateKey;

    /// <summary>How long a wake waits for the Network framework's first answer before it goes on (the metered check then says "cannot tell" and an account that wants an unmetered network waits).</summary>
    private static readonly TimeSpan FirstPathWait = TimeSpan.FromSeconds(2);

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

            if ((syncError ?? workError) is { } error) Log(IosBackgroundRequestText.Describe(new(error.Domain, (long)error.Code, error.LocalizedDescription)));
        }
        catch (Exception ex)
        {
            Log($"Asking iOS for background time failed ({IosFailureText.Say(ex)}).");
        }
    }

    public void CancelAll() => BGTaskScheduler.Shared.CancelAll();

    private void Handle(BGTask task, bool work)
    {
        // The next requests first: a round that iOS ends early, or a failure below, must not leave none pending. (Withdrawn again at the end when no account needs them.)
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
                runtime = services.GetRequiredService<IosBlindRuntime>();
                // The network path arrives a moment after the monitor starts; a wake that began from nothing waits briefly for it.
                await services.GetRequiredService<IIosNetworkPathSource>().WaitForFirstPathAsync(FirstPathWait, stop.Token);
                // ONE background round at a time for the whole device, whichever task iOS woke the app for: a round that begins beside another one (or while the app is open) does nothing and
                // is completed as "nothing to do" (IosBlindRuntime.RunBackgroundAsync).
                var outcome = await runtime.RunBackgroundAsync(work, stop.Token);
                IosBackgroundRecorder.Record(runtime, name, outcome);
                completed = outcome.Skipped is not null || !outcome.EndedByIos;
            }
            catch (OperationCanceledException)
            {
                // iOS ended the wake before it began anything.
            }
            catch (Exception ex)
            {
                if (runtime is not null) IosBackgroundRecorder.RecordFailure(runtime, name, ex);
            }
            finally
            {
                try
                {
                    if (runtime is not null)
                    {
                        services.GetService<IosSilenceNotifier>()?.Update(runtime.Accounts.GetSnapshot());
                        runtime.RefreshBackgroundRequests();
                    }
                }
                catch (Exception) { /* the notification and the next request are a convenience */ }
                task.SetTaskCompleted(completed);
                stop.Dispose();
            }
        });
    }

    /// <summary>The wake belongs to the device, not to one account: its sentence goes to the DEVICE's log, which holds no account's name or fate.</summary>
    private void Log(string message)
    {
        try { BlindRunReport.Record(services.GetRequiredService<IosBlindRuntime>().DeviceLog, "background", message); }
        catch (Exception) { /* nowhere left to tell it */ }
    }
}
