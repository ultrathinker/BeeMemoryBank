using BeeMemoryBank.Mobile.Services;
using AppTheme = Microsoft.Maui.ApplicationModel.AppTheme;

namespace BeeMemoryBank.Mobile.Platforms.Android;

/// <summary>
/// The status bar and the navigation bar wear the app background of the appearance shown right now
/// (BmbBackground in the dark, BmbBackgroundLight in the light), not a fixed theme colour: a light
/// background with dark icons in the light appearance, a dark background with light icons in the dark
/// one. MainActivity calls <see cref="Apply"/> at start and whenever the appearance changes (the
/// Settings choice or the system theme while the choice is "System").
///
/// A modal page (PushModalAsync) lives in its OWN dialog window, which the activity's colours do not
/// reach: on API 30+ MAUI builds that window from the Maui.MainTheme.NoActionBar theme and only sets
/// its icon appearance from the theme's colorPrimary. <see cref="ApplyModal"/> is therefore applied to
/// each modal dialog window as it is shown (MainActivity's fragment-lifecycle hook), and the theme's
/// bar colours are overridden per night mode in Resources/values and values-night so the window is
/// already right when it is created.
/// </summary>
public static class SystemBarColors
{
    public static void Apply(global::Android.App.Activity? activity)
    {
        if (activity?.Window is { } window) ApplyWindow(window, isActivityWindow: true);
    }

    /// <summary>Repaint a modal dialog's window so it matches the appearance of the page behind it.</summary>
    public static void ApplyModal(global::Android.Views.Window? window)
    {
        if (window is not null) ApplyWindow(window, isActivityWindow: false);
    }

    private static void ApplyWindow(global::Android.Views.Window window, bool isActivityWindow)
    {
        var light = ThemeManager.Effective == AppTheme.Light;
        var background = ThemeManager.Token("Background");
        var color = global::Android.Graphics.Color.Argb(
            (int)(background.Alpha * 255), (int)(background.Red * 255),
            (int)(background.Green * 255), (int)(background.Blue * 255));

        if (OperatingSystem.IsAndroidVersionAtLeast(35))
        {
            // Android 35 enforces edge-to-edge: the bars are transparent and the colour setters are
            // obsolete, so the window background is what shows through them. Only the activity window
            // needs that here; a modal dialog's background is left to MAUI / the modal page itself.
            if (isActivityWindow)
                window.SetBackgroundDrawable(new global::Android.Graphics.Drawables.ColorDrawable(color));
        }
        else
        {
            window.SetStatusBarColor(color);
            window.SetNavigationBarColor(color);
        }

        // "AppearanceLight" means a light bar with dark icons; the dark appearance wants light icons.
        // This also overrides the icon appearance MAUI derives from colorPrimary for a modal window.
        var controller = new global::AndroidX.Core.View.WindowInsetsControllerCompat(window, window.DecorView);
        controller.AppearanceLightStatusBars = light;
        controller.AppearanceLightNavigationBars = light;
    }

    /// <summary>
    /// On API 35+ (edge-to-edge) the status bar is transparent, and what shows through it on a Shell
    /// page is the Shell's app bar background: MAUI's MauiAppBarLayout style paints that with
    /// colorPrimary (#2C3E50), which sits ON TOP of the window background <see cref="Apply"/> sets, so
    /// the window colour never reaches the status-bar area. Repaint every AppBarLayout in the Shell
    /// with the app background token instead. Called on navigation (a new page builds a new app bar)
    /// and on an appearance change. Below API 35 the bars are painted by <see cref="Apply"/> and this
    /// is a no-op, so that behaviour is unchanged.
    /// </summary>
    public static void ApplyShellAppBars(global::Android.App.Activity? activity)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(35)) return;
        if (activity?.Window?.DecorView is not global::Android.Views.ViewGroup root) return;
        var background = ThemeManager.Token("Background");
        var color = global::Android.Graphics.Color.Argb(
            (int)(background.Alpha * 255), (int)(background.Red * 255),
            (int)(background.Green * 255), (int)(background.Blue * 255));
        PaintAppBars(root, color);
    }

    private static void PaintAppBars(global::Android.Views.ViewGroup group, global::Android.Graphics.Color color)
    {
        for (int i = 0; i < group.ChildCount; i++)
        {
            var child = group.GetChildAt(i);
            if (child is global::Google.Android.Material.AppBar.AppBarLayout appBar)
                appBar.SetBackgroundColor(color);
            if (child is global::Android.Views.ViewGroup childGroup)
                PaintAppBars(childGroup, color);
        }
    }
}
