using BeeMemoryBank.BlindDesktop.Scheduling;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Services;

/// <summary>
/// The desktop lifecycle around "Disconnect and wipe": before the wipe the scheduler is stopped and a running job paused (so that no
/// job holds the database or the keys while they are removed); after a successful wipe the host returns to its first-run state.
/// There is no second backup service on a desktop, and no new process is needed: the first-run state is a new composition in the
/// same process (<paramref name="returnToFirstRun"/>, which must not block).
/// </summary>
public sealed class HostLifecycle(BlindTimerScheduler scheduler, Action returnToFirstRun) : IBlindLifecycle
{
    public void StopBackgroundWork() => scheduler.Cancel();

    /// <summary>There is no separate backup service on a desktop; the scheduler runs the backups, and it is already stopped.</summary>
    public void StopBackupService() => scheduler.Cancel();

    /// <summary>
    /// A refused wipe (the running work did not pause in time; nothing was deleted) gives the schedule back: stopping it ended the loop,
    /// and nothing else starts it again until the app is restarted. A scheduler that already runs is left alone.
    /// </summary>
    public void ResumeBackgroundWork() => scheduler.EnsureScheduled();

    public void RestartAfterWipe() => returnToFirstRun();
}
