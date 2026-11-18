using Android.Graphics.Drawables;
using Microsoft.Maui.Platform;

namespace BeeMemoryBank.Mobile.Platforms.Android;

/// <summary>
/// The rounded field background of the mock-up (.input-shell, .textarea-input): a 16dp-corner
/// surface with the strong border, drawn natively because a MAUI Entry has no corner radius.
/// The fill is the theme-resolved BackgroundColor of the field's style; the stroke is the
/// BorderStrong token of the current appearance. A transparent field keeps no background at all:
/// its shell (Controls/PasswordBox) draws the rounded surface around it.
/// </summary>
internal static class PlatformFieldBackground
{
    public static void Apply(global::Android.Widget.EditText? editText, Color? fill, int padLeft, int padTop, int padRight, int padBottom)
    {
        if (editText is null) return;   // handler teardown

        if (fill is null || fill == Colors.Transparent)
        {
            editText.Background = null;
            return;
        }

        // The mapper runs after every property change (every keystroke included): repaint only
        // when the fill actually changed — a theme change moves it, typing does not.
        var platformFill = fill.ToPlatform();
        if (editText.Background is GradientDrawable current && current.Color is { } fillState && fillState.DefaultColor == platformFill)
            return;

        var density = editText.Context?.Resources?.DisplayMetrics?.Density ?? 1f;
        var drawable = new GradientDrawable();
        drawable.SetCornerRadius(16f * density);
        drawable.SetColor(platformFill);
        drawable.SetStroke((int)(1f * density), Services.ThemeManager.Token("BorderStrong").ToPlatform());
        editText.Background = drawable;
        editText.SetPadding(
            (int)(padLeft * density), (int)(padTop * density),
            (int)(padRight * density), (int)(padBottom * density));
    }
}
