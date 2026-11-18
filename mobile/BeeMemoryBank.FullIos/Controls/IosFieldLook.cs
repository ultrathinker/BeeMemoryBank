using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;

namespace BeeMemoryBank.FullIos.Controls;

/// <summary>
/// The design's fields on iOS, the way the Android app gives them its mapper: a filled field is
/// the rounded input shell (16 corners, the strong border, room for the text), and a transparent
/// field - the Entry inside PasswordBox, whose shell draws the surface - is flat, with no native
/// border of its own. Display only: it changes how a field looks, never what it does.
/// </summary>
public static class IosFieldLook
{
    public static void Apply()
    {
        EntryHandler.Mapper.AppendToMapping("BmbInputShell", (handler, entry) =>
        {
            var field = handler.PlatformView;
            var background = (entry as Microsoft.Maui.Controls.Entry)?.BackgroundColor;
            var filled = background is { Alpha: > 0 };
            field.BorderStyle = UITextBorderStyle.None;
            field.BackgroundColor = filled ? background!.ToPlatform() : UIColor.Clear;
            field.Layer.CornerRadius = 16;
            field.Layer.BorderWidth = filled ? 1 : 0;
            field.Layer.BorderColor = ThemeManager.Token("BorderStrong").ToCGColor();
            // A filled field carries the shell's text room; the transparent one sits in a shell
            // that already has its own padding.
            if (filled && field.LeftView is null)
            {
                field.LeftView = new UIView(new CoreGraphics.CGRect(0, 0, 14, 1));
                field.LeftViewMode = UITextFieldViewMode.Always;
                field.RightView = new UIView(new CoreGraphics.CGRect(0, 0, 14, 1));
                field.RightViewMode = UITextFieldViewMode.Always;
            }
            else if (!filled)
            {
                field.LeftView = null;
                field.RightView = null;
            }
        });

        // The join code's multi-line field gets the same shell as the one-line fields.
        EditorHandler.Mapper.AppendToMapping("BmbInputShell", (handler, editor) =>
        {
            var view = handler.PlatformView;
            var background = (editor as Microsoft.Maui.Controls.Editor)?.BackgroundColor;
            var filled = background is { Alpha: > 0 };
            view.BackgroundColor = filled ? background!.ToPlatform() : UIColor.Clear;
            view.Layer.CornerRadius = 16;
            view.Layer.BorderWidth = filled ? 1 : 0;
            view.Layer.BorderColor = ThemeManager.Token("BorderStrong").ToCGColor();
            if (filled)
                view.TextContainerInset = new UIKit.UIEdgeInsets(10, 12, 10, 12);
        });

        // The join code's multi-line field gets the same rounded shell as the one-line
        // fields: radius, border, background and text room, in both appearances.
        EditorHandler.Mapper.AppendToMapping("BmbInputShell", (handler, editor) =>
        {
            var view = handler.PlatformView;
            var background = (editor as Microsoft.Maui.Controls.Editor)?.BackgroundColor;
            var filled = background is { Alpha: > 0 };
            view.BackgroundColor = filled ? background!.ToPlatform() : UIColor.Clear;
            view.Layer.CornerRadius = 16;
            view.Layer.BorderWidth = filled ? 1 : 0;
            view.Layer.BorderColor = ThemeManager.Token("BorderStrong").ToCGColor();
            if (filled)
                view.TextContainerInset = new UIKit.UIEdgeInsets(10, 12, 10, 12);
        });

        // The note body WebView is sized to its content and scrolls with the page, not inside
        // itself (the note page is one ScrollView with the tags above and the comments below).
        WebViewHandler.Mapper.AppendToMapping("BmbNoteBody", (handler, view) =>
        {
            handler.PlatformView.ScrollView.ScrollEnabled = false;
        });

        // A full-width button may wrap to two lines; every line of the caption is centred,
        // not left-aligned (UIButton's title label defaults to natural alignment).
        ButtonHandler.Mapper.AppendToMapping("BmbButtonCaption", (handler, button) =>
        {
            handler.PlatformView.TitleLabel.TextAlignment = UITextAlignment.Center;
        });

        // The search strip: the platform band (inset rectangle, square corners, top/bottom
        // hairlines) goes away; the field itself is the design's rounded shell, full width.
        SearchBarHandler.Mapper.AppendToMapping("BmbSearchStrip", (handler, bar) =>
        {
            var search = handler.PlatformView;
            search.BackgroundImage = new UIImage();
            search.BackgroundColor = UIColor.Clear;
            // One clear control beside the field (the empty state also carries "Reset", as on
            // Android); the small in-field clear is turned off so there is not a second one.
            search.ShowsCancelButton = true;
            var field = search.SearchTextField;
            field.ClearButtonMode = UITextFieldViewMode.Never;
            field.BackgroundColor = ThemeManager.Token("SurfaceHigh").ToPlatform();
            field.Layer.CornerRadius = 16;
            field.Layer.BorderWidth = 1;
            field.Layer.BorderColor = ThemeManager.Token("BorderStrong").ToCGColor();
            if (field.LeftView is null)
            {
                field.LeftView = new UIView(new CoreGraphics.CGRect(0, 0, 10, 1));
                field.LeftViewMode = UITextFieldViewMode.Always;
            }
        });
        // MAUI re-runs its own text mapping on every change, and the in-field clear came back
        // next to the cancel button once a query was typed (two clear controls): turn it off
        // again after each text update.
        SearchBarHandler.Mapper.AppendToMapping(nameof(ISearchBar.Text), (handler, bar) =>
            handler.PlatformView.SearchTextField.ClearButtonMode = UITextFieldViewMode.Never);
    }
}
