namespace BeeMemoryBank.Mobile.Services;

/// <summary>
/// The appearance choice of the mock-up: dark, light, or the system's. The choice is saved on the
/// phone and applied at every start; while the app runs, <c>Application.UserAppTheme</c> switches
/// every <c>AppThemeBinding</c> resource at once (the activity keeps running: MainActivity handles
/// UiMode changes itself).
/// </summary>
public static class ThemeManager
{
    private const string PreferenceKey = "app_theme";

    public const string System = "system";
    public const string Dark = "dark";
    public const string Light = "light";

    /// <summary>The saved choice: "system" (the default), "dark" or "light".</summary>
    public static string Saved => Preferences.Get(PreferenceKey, System);

    /// <summary>The theme actually shown right now, the system's one when the choice is "system".</summary>
    public static AppTheme Effective => Application.Current?.RequestedTheme ?? AppTheme.Dark;

    /// <summary>
    /// A colour token of Resources/Styles/Colors.xaml in the appearance shown right now, for the
    /// views built in code (the sheets, the dialogs, the sync badge). "Text" reads BmbText in the
    /// dark and BmbTextLight in the light — the same pair the XAML's AppThemeBinding picks.
    /// A token with no light variant of its own (the accent yellow) is the same in both.
    /// </summary>
    public static Color Token(string key)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return Colors.Transparent;
        // TryGetValue, not the indexer: ResourceDictionary's indexer throws KeyNotFoundException,
        // and the accent has no "Light" variant (the light theme closed the app on 14.11).
        var name = "Bmb" + key + (Effective == AppTheme.Light ? "Light" : "");
        if (resources.TryGetValue(name, out var themed) && themed is Color color) return color;
        return resources.TryGetValue("Bmb" + key, out var plain) && plain is Color fallback ? fallback : Colors.Transparent;
    }

    /// <summary>
    /// A toolbar icon set from code: "icon_x" gives icon_x_light.svg in the light appearance
    /// (dark strokes) and icon_x.svg in the dark one. The XAML picks the same pair with AppThemeBinding.
    /// </summary>
    public static string Icon(string name) => name + (Effective == AppTheme.Light ? "_light" : "") + ".svg";

    /// <summary>Applies the saved choice; called once at start, before the first page is shown.</summary>
    public static void ApplySaved() => Apply(Saved);

    /// <summary>Saves the choice and applies it at once.</summary>
    public static void Set(string choice)
    {
        Preferences.Set(PreferenceKey, choice);
        Apply(choice);
    }

    private static void Apply(string choice)
    {
        if (Application.Current is not { } app) return;
        app.UserAppTheme = choice switch
        {
            Dark => AppTheme.Dark,
            Light => AppTheme.Light,
            _ => AppTheme.Unspecified,
        };
    }
}
