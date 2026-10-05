using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindMobile.Platforms.Android;

/// <summary>Android's fresh-process restart after a successful blind-copy wipe.</summary>
public sealed class AndroidBlindLifecycle : IBlindLifecycle
{
    public void StopBackgroundWork()
    {
        BlindWorkScheduler.Cancel(Platform.AppContext);
    }

    public void StopBackupService()
    {
        Platform.AppContext.StopService(new global::Android.Content.Intent(Platform.AppContext, typeof(BlindBackupService)));
    }

    /// <summary>An aborted wipe: the periodic jobs that <see cref="StopBackgroundWork"/> cancelled are enqueued again.</summary>
    public void ResumeBackgroundWork() => BlindWorkScheduler.Ensure(Platform.AppContext);

    public void RestartAfterWipe() => ProcessRestart.Now();
}
