using Android.Content;
using Android.Net;
using Android.OS;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindMobile.Platforms.Android;

/// <summary>
/// The phone's answers for the two switches of an account (plan 3-D): the active network is metered when its capabilities lack <c>NOT_METERED</c> (<c>ConnectivityManager.GetNetworkCapabilities</c>), and the
/// phone is saving power when <c>PowerManager.IsPowerSaveMode</c> is on. No active network, no capabilities or a system service that does not answer is NULL ("cannot tell"): an account with the switch on then waits.
/// </summary>
public static class AndroidDeviceConditions
{
    public static IBlindDeviceConditions Create(Context context) => new ProbedBlindDeviceConditions(
        notMetered: () =>
        {
            if (context.GetSystemService(Context.ConnectivityService) is not ConnectivityManager manager) return null;
            if (manager.ActiveNetwork is not { } network) return null;
            return manager.GetNetworkCapabilities(network)?.HasCapability(NetCapability.NotMetered);
        },
        powerSaving: () => (context.GetSystemService(Context.PowerService) as PowerManager)?.IsPowerSaveMode);
}

/// <summary>
/// What the account host asks of the platform when an account's work must stop (a removal): the periodic WorkManager jobs are the DEVICE's (one set for every account), so they are never cancelled for one account's
/// sake - the host refuses new rounds of an account that is being removed, and the scheduler skips it. Only a backup that is running for that very account is stopped.
/// </summary>
public sealed class AndroidAccountLifecycle : IBlindAccountLifecycle
{
    public void StopBackupService(BlindAccountEntry account) => BlindBackupService.StopFor(Platform.AppContext, account.Id);
}
