using UIKit;

namespace BeeMemoryBank.FullIos.Platforms.iOS;

/// <summary>
/// A plain cover over the app while it is not active, so the picture iOS keeps for the app switcher (taken as the app leaves) shows no
/// note: a native view laid over the window at once, not a page, because the picture is taken before a page could be drawn.
/// </summary>
internal static class PrivacyCover
{
    private static UIView? _cover;

    public static void Show()
    {
        if (_cover is not null) return;
        var window = KeyWindow();
        if (window is null) return;
        var cover = new UIView(window.Bounds)
        {
            AutoresizingMask = UIViewAutoresizing.FlexibleDimensions,
            BackgroundColor = UIColor.SystemBackground,
        };
        var label = new UILabel
        {
            Text = "Bee Memory Bank",
            TextColor = UIColor.SecondaryLabel,
            Font = UIFont.BoldSystemFontOfSize(17)!,
            TextAlignment = UITextAlignment.Center,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        cover.AddSubview(label);
        label.CenterXAnchor.ConstraintEqualTo(cover.CenterXAnchor).Active = true;
        label.CenterYAnchor.ConstraintEqualTo(cover.CenterYAnchor).Active = true;
        window.AddSubview(cover);
        _cover = cover;
    }

    public static void Hide()
    {
        _cover?.RemoveFromSuperview();
        _cover?.Dispose();
        _cover = null;
    }

    internal static UIWindow? KeyWindow() =>
        UIApplication.SharedApplication.ConnectedScenes.ToArray()
            .OfType<UIWindowScene>()
            .SelectMany(scene => scene.Windows)
            .FirstOrDefault(w => w.IsKeyWindow);
}
