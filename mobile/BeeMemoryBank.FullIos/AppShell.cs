using BeeMemoryBank.FullIos.Pages;

namespace BeeMemoryBank.FullIos;

/// <summary>
/// The open vault: four tabs at the bottom, where a thumb reaches them - Notes (the folder tree), Search, Sync, Settings. Made new at every
/// unlock and dropped at every lock (AppFlow), so its pages never outlive the open vault.
/// </summary>
public sealed class AppShell : Shell
{
    public AppShell(IServiceProvider services)
    {
        FlyoutBehavior = FlyoutBehavior.Disabled;
        this.SetAppTheme(Shell.BackgroundColorProperty, Colour("LightBackground"), Colour("DarkBackground"));
        this.SetAppTheme(Shell.ForegroundColorProperty, Colour("LightAccent"), Colour("DarkAccent"));
        this.SetAppTheme(Shell.TitleColorProperty, Colour("LightText"), Colour("DarkText"));
        this.SetAppTheme(Shell.TabBarBackgroundColorProperty, Colour("LightSurface"), Colour("DarkSurface"));
        this.SetAppTheme(Shell.TabBarForegroundColorProperty, Colour("LightAccent"), Colour("DarkAccent"));
        this.SetAppTheme(Shell.TabBarTitleColorProperty, Colour("LightAccent"), Colour("DarkAccent"));
        this.SetAppTheme(Shell.TabBarUnselectedColorProperty, Colour("LightMuted"), Colour("DarkMuted"));

        var tabs = new TabBar();
        tabs.Items.Add(Tab("Notes", "tab_notes.png", "notes", () => services.GetRequiredService<FolderPage>()));
        tabs.Items.Add(Tab("Search", "tab_search.png", "search", () => services.GetRequiredService<SearchPage>()));
        tabs.Items.Add(Tab("Sync", "tab_sync.png", "sync", () => services.GetRequiredService<SyncPage>()));
        tabs.Items.Add(Tab("Settings", "tab_settings.png", "settings", () => services.GetRequiredService<SettingsPage>()));
        Items.Add(tabs);
    }

    private static ShellContent Tab(string title, string icon, string route, Func<Page> page) => new()
    {
        Title = title,
        Icon = icon,
        Route = route,
        ContentTemplate = new DataTemplate(page),
    };

    private static Color Colour(string key) => (Color)Application.Current!.Resources[key];
}
