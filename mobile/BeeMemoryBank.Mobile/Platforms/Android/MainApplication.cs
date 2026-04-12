using Android.App;
using Android.Runtime;

namespace BeeMemoryBank.Mobile.Platforms.Android;

[Application(
    NetworkSecurityConfig = "@xml/network_security_config",
    Icon = "@mipmap/appicon",
    RoundIcon = "@mipmap/appicon_round")]
public class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
