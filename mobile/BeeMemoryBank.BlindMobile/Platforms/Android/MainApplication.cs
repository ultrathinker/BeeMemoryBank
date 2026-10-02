using Android.App;
using Android.Runtime;

namespace BeeMemoryBank.BlindMobile.Platforms.Android;

[Application(
    NetworkSecurityConfig = "@xml/network_security_config",
    Icon = "@mipmap/appicon",
    RoundIcon = "@mipmap/appicon_round")]
public class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override void OnCreate()
    {
        // The restart helper (ProcessRestart) is a few lines of Activity code in a process of its own; it needs no MAUI app, no
        // container and no database.
        if (ProcessName?.EndsWith(":restart", StringComparison.Ordinal) == true) return;
        base.OnCreate();
    }
}
