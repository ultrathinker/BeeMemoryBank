using Foundation;
using UIKit;

namespace BeeMemoryBank.FullIos.Platforms.iOS;

/// <summary>
/// Copies a secret (the recovery key) so that it stays on this phone and leaves the clipboard by itself: "local only" keeps it out of
/// Universal Clipboard (the owner's other Apple devices), and iOS removes it after <paramref name="minutes"/>.
/// </summary>
internal static class IosClipboard
{
    public static void CopySecret(string text, int minutes = 2)
    {
        var item = new NSDictionary<NSString, NSObject>(new NSString("public.utf8-plain-text"), new NSString(text));
        var options = new UIPasteboardOptions
        {
            LocalOnly = true,
            ExpirationDate = NSDate.FromTimeIntervalSinceNow(minutes * 60),
        };
        UIPasteboard.General.SetItems([item], options);
    }
}
