using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile;

public partial class AppShell : Shell
{
    private readonly (Tab Tab, string Icon)[] _tabs;

    // The page the header describes. Shell.CurrentPage becomes the topmost modal while a sheet or
    // dialog is open, so the header keeps the underlying page instead of going blank under the dim.
    private Page? _headerPage;

    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute("articleDetail", typeof(Pages.ArticleDetailPage));
        Routing.RegisterRoute("articleEdit", typeof(Pages.ArticleEditPage));
        Routing.RegisterRoute("folderPicker", typeof(Pages.FolderPickerPage));
        // Tags is pushed from Search (a relative route), so it keeps the back arrow and the tab bar;
        // as a root ShellContent it stranded the person with no way back.
        Routing.RegisterRoute("tags", typeof(Pages.TagsPage));
        Routing.RegisterRoute("tagArticles", typeof(Pages.TagArticlesPage));
        Routing.RegisterRoute("treeFolder", typeof(Pages.TreeFolderPage));
        Routing.RegisterRoute("recoveryKey", typeof(Pages.RecoveryKeyPage));
        Routing.RegisterRoute("trash", typeof(Pages.TrashPage));

        _tabs = [(TreeTab, "tree"), (SearchTab, "search"), (SyncTab, "sync"), (SettingsTab, "settings")];
        Navigated += (_, _) =>
        {
            UpdateTabIcons();
            // A modal page (sheet / dialog / popup) is the CurrentPage while it is open, but it must
            // not change the header: remember only the underlying, non-modal page.
            var page = CurrentPage;
            if (page is not null && !Navigation.ModalStack.Contains(page))
                _headerPage = page;
            OnPropertyChanged(nameof(HeaderTitle));
            OnPropertyChanged(nameof(HeaderPadding));
            // On API 35+ the Shell app bar shows through the transparent status bar; repaint it with
            // the appearance background (each page builds its own app bar). Dispatched so the new
            // page's app bar is attached first. No-op below API 35.
            Dispatcher.Dispatch(() => Platforms.Android.SystemBarColors.ApplyShellAppBars(
                Microsoft.Maui.ApplicationModel.Platform.CurrentActivity));
        };
        if (Application.Current is { } app) app.RequestedThemeChanged += (_, _) => UpdateTabIcons();
        UpdateTabIcons();
    }

    /// <summary>
    /// The title the header shows on the left: the current page's (localized) title. The tree and the
    /// article pages already carry their own breadcrumb rail or big title, so for them the header stays
    /// empty rather than saying it twice. CurrentItem.Title was the TabBar (always empty) before.
    /// </summary>
    public string HeaderTitle
    {
        get
        {
            var page = _headerPage;
            if (page is Pages.TreePage or Pages.TreeFolderPage or Pages.ArticleDetailPage or Pages.ArticleEditPage)
                return "";
            return page?.Title ?? "";
        }
    }

    /// <summary>
    /// The header's left padding. A tab page has no back arrow, so its title would sit at the very
    /// left edge (x = 0) while the cards start at 16dp; give it the page margin so they line up. A
    /// pushed page already has the back arrow to its left, so it keeps no extra padding.
    /// </summary>
    public Thickness HeaderPadding
        => new(IsTabRoot(_headerPage) ? 16 : 0, 0, 16, 0);

    private static bool IsTabRoot(Page? page)
        => page is Pages.TreePage or Pages.ArticlesPage or Pages.StatusPage or Pages.SecurityPage;

    /// <summary>
    /// The tab icons in the colours of the mock-up: the secondary text colour for an idle tab,
    /// the accent text colour for the selected one, each in the appearance shown right now.
    /// Android draws a tab icon as it is (no tint of its own), so the colour lives in the file.
    /// </summary>
    private void UpdateTabIcons()
    {
        var theme = ThemeManager.Effective == AppTheme.Light ? "light" : "dark";
        var selected = CurrentItem?.CurrentItem;
        foreach (var (tab, icon) in _tabs)
            tab.Icon = ImageSource.FromFile($"tab_{icon}_{theme}{(ReferenceEquals(tab, selected) ? "_active" : "")}.svg");
    }

    protected override void OnNavigating(ShellNavigatingEventArgs args)
    {
        base.OnNavigating(args);

        // The editor's unsaved-changes question also covers the toolbar up button: Shell's up
        // navigation does not go through the page's OnBackButtonPressed (the hardware back does).
        if (CurrentPage is Pages.ArticleEditPage editor && !editor.LeaveConfirmed &&
            (args.Target?.Location?.OriginalString ?? "") == "..")
        {
            args.Cancel();
            Dispatcher.Dispatch(async () =>
            {
                try
                {
                    if (await editor.RequestLeaveAsync())
                    {
                        editor.LeaveConfirmed = true;
                        await GoToAsync("..");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Editor leave failed: {ex.Message}");
                }
            });
            return;
        }

        // While initial sync is pending, the only allowed destinations are
        // the gate itself and the setup/unlock pages (cancel-and-wipe path).
        // Everything else is blocked — the user must wait for sync to finish.
        var target = args.Target?.Location?.OriginalString ?? "";

        if (target.StartsWith("//initialSync") || target.StartsWith("//setup") ||
            target.StartsWith("//unlock") || target.StartsWith("//welcome"))
            return;

        // Read the cached flag only — never touch SQLite on the UI thread.
        // The cache is seeded by PostUnlockRouter on every unlock path, and
        // cleared/updated by InitialSyncPage when sync completes. Before the
        // first unlock it's null, in which case we let navigation proceed
        // (the App.OnStart flow always routes to //unlock anyway).
        var router = IPlatformApplication.Current?.Services.GetService<PostUnlockRouter>();
        if (router?.CachedInitialSyncCompleted == false)
        {
            args.Cancel();
            Dispatcher.Dispatch(() => GoToAsync("//initialSync"));
        }
    }
}
