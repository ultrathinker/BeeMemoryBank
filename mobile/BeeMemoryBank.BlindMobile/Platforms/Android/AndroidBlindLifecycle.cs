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

    public void RestartAfterWipe() => ProcessRestart.Now();
}
