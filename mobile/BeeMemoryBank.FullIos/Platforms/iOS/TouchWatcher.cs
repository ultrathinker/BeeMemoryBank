using Foundation;
using UIKit;

namespace BeeMemoryBank.FullIos.Platforms.iOS;

/// <summary>
/// Notices every touch on the app's window (for the idle lock) without taking part in it: a gesture recognizer that reports the touch and
/// fails at once, so every view gets the touch exactly as before. Typing is not a touch on this window (the keyboard is a window of its
/// own): the editor reports its text changes itself.
/// </summary>
internal sealed class TouchWatcher : UIGestureRecognizer
{
    private readonly Action _touched;

    private TouchWatcher(Action touched)
    {
        _touched = touched;
        CancelsTouchesInView = false;
        DelaysTouchesBegan = false;
        DelaysTouchesEnded = false;
        ShouldRecognizeSimultaneously = (_, _) => true;
    }

    public static void Attach(Action touched)
    {
        var window = PrivacyCover.KeyWindow();
        if (window is null || window.GestureRecognizers?.OfType<TouchWatcher>().Any() == true) return;
        window.AddGestureRecognizer(new TouchWatcher(touched));
    }

    public override void TouchesBegan(NSSet touches, UIEvent evt)
    {
        base.TouchesBegan(touches, evt);
        _touched();
        State = UIGestureRecognizerState.Failed;
    }
}
