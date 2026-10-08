using Foundation;
using UIKit;

namespace BeeMemoryBank.FullIos;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    /// <summary>A bmb-join: link (the Camera scanned a computer's join QR code, or the code was tapped).</summary>
    public override bool OpenUrl(UIApplication application, NSUrl url, NSDictionary options)
    {
        if (App.TryHandleLink(url.AbsoluteString)) return true;
        return base.OpenUrl(application, url, options);
    }
}
