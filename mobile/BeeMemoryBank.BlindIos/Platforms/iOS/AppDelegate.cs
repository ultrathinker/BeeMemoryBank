using BeeMemoryBank.BlindIos.Platforms.iOS;
using Foundation;
using UIKit;

namespace BeeMemoryBank.BlindIos;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        var started = base.FinishedLaunching(application, launchOptions);
        // The background task handlers must be registered before launching ends, also when iOS starts the app only to run one of them.
        IPlatformApplication.Current?.Services.GetService<IosBlindBackground>()?.Register();
        return started;
    }

    /// <summary>A bmb-blind-call: link (the Camera scanned the computer's QR code, or a pasted link was tapped).</summary>
    public override bool OpenUrl(UIApplication application, NSUrl url, NSDictionary options)
    {
        if (App.TryHandleLink(url.AbsoluteString)) return true;
        return base.OpenUrl(application, url, options);
    }
}
