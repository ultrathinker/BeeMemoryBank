using BeeMemoryBank.FullIos.Controls;
using BeeMemoryBank.FullIos.Localization;
using BeeMemoryBank.FullIos.Pages;

namespace BeeMemoryBank.FullIos;

/// <summary>
/// The open vault: the design's four bottom tabs - Tree, Search, Sync, Settings - and the header
/// with the sync state badge. Made new at every unlock and dropped at every lock (AppFlow), so its
/// pages never outlive the open vault. The routes stay the ones AppFlow and the end-to-end check
/// navigate by (notes / search / sync / settings); the tab names and icons are the design's, the
/// same on both phones. The header title lines up with the page margin; the note and the editor
/// carry their own big title, so for them the header stays empty rather than saying it twice.
/// </summary>
public sealed class AppShell : Shell
{
    private readonly (Tab Tab, string Icon)[] _tabs;
    private View? _titleView;
    private Label? _titleLabel;
    private SyncIndicatorView? _badge;
    private Page? _lastPage;

    public AppShell(IServiceProvider services)
    {
        FlyoutBehavior = FlyoutBehavior.Disabled;
        Shell.SetNavBarHasShadow(this, false);
        this.SetAppTheme(BackgroundColorProperty, Token("BackgroundLight"), Token("Background"));
        this.SetAppTheme(ForegroundColorProperty, Token("TextLight"), Token("Text"));
        this.SetAppTheme(TitleColorProperty, Token("TextLight"), Token("Text"));
        this.SetAppTheme(TabBarBackgroundColorProperty, Token("SurfaceLight"), Token("Surface"));
        this.SetAppTheme(TabBarForegroundColorProperty, Token("AccentTextLight"), Token("AccentText"));
        this.SetAppTheme(TabBarTitleColorProperty, Token("AccentTextLight"), Token("AccentText"));
        this.SetAppTheme(TabBarUnselectedColorProperty, Token("TextSecondaryLight"), Token("TextSecondary"));

        _titleView = BuildTitleView();
        Shell.SetTitleView(this, _titleView);

        var tabs = new TabBar();
        var tree = NewTab(AppStrings.Get("TabTree"), "notes", () => services.GetRequiredService<FolderPage>());
        var search = NewTab(AppStrings.Get("TabSearch"), "search", () => services.GetRequiredService<SearchPage>());
        var sync = NewTab(AppStrings.Get("TabSync"), "sync", () => services.GetRequiredService<SyncPage>());
        var settings = NewTab(AppStrings.Get("TabSettings"), "settings", () => services.GetRequiredService<SettingsPage>());
        tabs.Items.Add(tree);
        tabs.Items.Add(search);
        tabs.Items.Add(sync);
        tabs.Items.Add(settings);
        Items.Add(tabs);

        _tabs = [(tree, "tree"), (search, "search"), (sync, "sync"), (settings, "settings")];
        Navigated += (_, _) =>
        {
            // A modal over the page (a dialog, a sheet, the busy pop-up) also raises Navigated,
            // with itself as CurrentPage: the header must keep the page's title and width, not
            // the modal's empty ones (the design's rule: a sheet never blanks the page title).
            var page = CurrentPage;
            if (page is not null && page.AutomationId is not ("AppDialog" or "BottomSheet" or "InputPopup" or "BusyPage"))
                _lastPage = page;
            UpdateTabIcons();
            UpdateTitleViewWidth();
            // Before their handlers attach, the title and the badge measure too small and the
            // collapse decision would keep the full badge over a truncated title; re-decide once
            // the bar has laid out (the SegmentedControl lesson).
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), UpdateTitleViewWidth);
            OnPropertyChanged(nameof(HeaderTitle));
        };
        SizeChanged += (_, _) => UpdateTitleViewWidth();
        if (Application.Current is { } app) app.RequestedThemeChanged += (_, _) => UpdateTabIcons();
        UpdateTabIcons();
        UpdateTitleViewWidth();
    }

    /// <summary>
    /// iOS sizes the navigation bar's TitleView to its content and centres it, so the badge would
    /// sit in the middle of the header. Give the view the width between the leading edge (the back
    /// button when there is one) and the page's toolbar items, and the badge lands at the right of
    /// the header, before the actions, as in the design.
    /// </summary>
    private void UpdateTitleViewWidth()
    {
        if (_titleView is null || Window is not { } window || window.Width <= 0) return;
        var page = _lastPage ?? CurrentPage;
        var hasBack = (page?.Navigation?.NavigationStack.Count ?? 1) > 1;
        double trailing = 16;
        if (page is not null)
        {
            // iOS shows the Secondary items together as one overflow button, the Primary ones each.
            // A text item ("Edit", "Save") is its caption plus the glass pill's padding, and it grows
            // with the phone's text size (real iPhone 11 Pro, 15.11: "Izmenit" 125 instead of 110
            // wide). Under-estimating it let the TitleView slide under the toolbar and clip the
            // badge's right end; over-estimating hid the badge for no reason. Measure the caption
            // with the same scaled font the bar uses, plus the pill padding.
            var primary = page.ToolbarItems.Where(i => i.Order != ToolbarItemOrder.Secondary).ToList();
            foreach (var item in primary)
                trailing += item.IconImageSource is not null ? 44 : ToolbarTextWidth(item.Text ?? "");
            if (page.ToolbarItems.Any(i => i.Order == ToolbarItemOrder.Secondary)) trailing += 44;
            if (page.ToolbarItems.Count > 0) trailing += 12;
        }
        // A little slack under the real free space: an over-wide TitleView slides under the toolbar
        // and clips the badge's right end. No wide floor: on a narrow phone the free space really is
        // small, and the badge then collapses to its icon instead of being cut (real iPhone 11 Pro, 15.11).
        double leading = hasBack ? 60 : 16;
        double available = Math.Max(48, window.Width - leading - trailing - 12);
        // The badge never clips: when the title and the full badge do not fit (a large system
        // font), the badge collapses to its icon and the title gets the room back. The grid keeps
        // 16 of right padding, so the content has the width minus that padding.
        if (_titleLabel is not null && _badge is not null)
        {
            _badge.Collapsed = false;
            var need = _titleLabel.Measure(double.PositiveInfinity, double.PositiveInfinity).Width
                     + 12 + _badge.Measure(double.PositiveInfinity, double.PositiveInfinity).Width;
            if (need > available - 16) _badge.Collapsed = true;
        }
        _titleView.WidthRequest = available;
    }

    /// <summary>The width of a toolbar text item: its caption in the bar's scaled font plus the pill padding.</summary>
    private static double ToolbarTextWidth(string text)
    {
#if IOS
        try
        {
            var system = UIKit.UIFont.SystemFontOfSize((System.Runtime.InteropServices.NFloat)17, UIKit.UIFontWeight.Semibold);
            if (system is null) return 150;
            var font = UIKit.UIFontMetrics.DefaultMetrics.GetScaledFont(system);
            var attributes = new Foundation.NSDictionary(UIKit.UIStringAttributeKey.Font, font);
            var width = (double)new Foundation.NSAttributedString(text, attributes).Size.Width;
            return Math.Ceiling(width) + 36;
        }
        catch
        {
            return 150;
        }
#else
        return 150;
#endif
    }

    /// <summary>
    /// The title the header shows on the left: the current page's title. The note and the editor
    /// already carry their own big title, so for them the header stays empty rather than saying it
    /// twice. A sheet or dialog over a page is a modal: CurrentPage does not change, the title stays.
    /// </summary>
    public string HeaderTitle
    {
        get
        {
            var page = _lastPage ?? CurrentPage;
            // The note and the editor carry their own big title; a pushed folder page carries
            // its breadcrumb rail - the header stays empty rather than saying it twice (and
            // squeezing the badge). The picker and the tag's articles carry their own heading
            // too, so a long caption wraps in the page instead of cutting in the bar.
            if (page is NotePage or EditNotePage or FolderPickerPage or TagArticlesPage) return "";
            if (page is FolderPage && (page.Navigation?.NavigationStack.Count ?? 1) > 1) return "";
            return page?.Title ?? "";
        }
    }

    private static Tab NewTab(string title, string route, Func<Page> page)
    {
        var tab = new Tab { Title = title };
        tab.Items.Add(new ShellContent
        {
            Route = route,
            ContentTemplate = new DataTemplate(page),
        });
        return tab;
    }

    private View BuildTitleView()
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            ColumnSpacing = 12,
            Padding = new Thickness(0, 0, 16, 0),
            // Start, not Fill: the bar centres an over-wide TitleView and clips its left edge;
            // left-aligned, the title sits on the page margin whatever the width estimate does.
            HorizontalOptions = LayoutOptions.Start,
        };
        var label = new Label
        {
            Style = StyleOf("HeaderTitle"),
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        label.SetBinding(Label.TextProperty, new Binding(nameof(HeaderTitle), source: this));
        var badge = new SyncIndicatorView { VerticalOptions = LayoutOptions.Center };
        Grid.SetColumn(badge, 1);
        grid.Children.Add(label);
        grid.Children.Add(badge);
        _titleLabel = label;
        _badge = badge;
        return grid;
    }

    /// <summary>
    /// The tab icons in the colours of the design: the secondary text colour for an idle tab, the
    /// accent text colour for the selected one, each in the appearance shown right now. The colour
    /// lives in the file (four variants per tab), as on Android.
    /// </summary>
    private void UpdateTabIcons()
    {
        var theme = ThemeManager.Effective == AppTheme.Light ? "light" : "dark";
        var selected = CurrentItem?.CurrentItem;
        foreach (var (tab, icon) in _tabs)
        {
            var active = ReferenceEquals(tab, CurrentItem) || ReferenceEquals(tab.Items.FirstOrDefault(), selected);
            tab.Icon = ImageSource.FromFile($"tab_{icon}_{theme}{(active ? "_active" : "")}.png");
        }
    }

    private static Color Token(string key)
    {
        // TryGetValue, not the indexer: a missing key must never throw.
        var resources = Application.Current?.Resources;
        if (resources is not null)
        {
            if (resources.TryGetValue("Bmb" + key, out var plain) && plain is Color color) return color;
        }
        return Colors.Transparent;
    }

    private static Style? StyleOf(string key)
        => Application.Current?.Resources.TryGetValue(key, out var style) == true && style is Style s ? s : null;
}
