using Android.Content.Res;
using Android.Graphics;
using Microsoft.Maui.Platform;

namespace BeeMemoryBank.Mobile.Platforms.Android;

/// <summary>
/// The mock-up's .switch-btn in both states: the checked track is the accent yellow with the
/// accent-foreground thumb (dark on yellow), the unchecked track is the surface-high with the
/// secondary-text thumb. A ColorStateList per part gives the per-state tint natively; the mapper
/// runs again whenever a mapped property changes, so a theme change repaints every switch.
/// </summary>
internal static class PlatformSwitchColors
{
    public static void Apply(AndroidX.AppCompat.Widget.SwitchCompat? switchCompat)
    {
        if (switchCompat is null) return;   // handler teardown

        var accent = Services.ThemeManager.Token("Accent").ToPlatform();
        var accentFg = Services.ThemeManager.Token("AccentForeground").ToPlatform();
        var surfaceHigh = Services.ThemeManager.Token("SurfaceHigh").ToPlatform();
        var secondary = Services.ThemeManager.Token("TextSecondary").ToPlatform();

        var checkedState = new int[] { global::Android.Resource.Attribute.StateChecked };
        var uncheckedState = new int[] { -global::Android.Resource.Attribute.StateChecked };

        switchCompat.ThumbTintList = new ColorStateList(
            [checkedState, uncheckedState],
            [accentFg, secondary]);
        switchCompat.TrackTintList = new ColorStateList(
            [checkedState, uncheckedState],
            [accent, surfaceHigh]);
    }
}
