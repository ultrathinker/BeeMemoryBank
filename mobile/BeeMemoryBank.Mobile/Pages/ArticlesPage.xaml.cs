using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Controls;
using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

/// <summary>
/// One search result row: the title and the "path · date" meta as formatted strings, so the
/// query matches carry the mock-up's highlight (.search-hit) in both of them.
/// </summary>
public record SearchRow(Guid Id, string Title, string Path, DateTime UpdatedAt, bool Protected, FormattedString TitleText, FormattedString MetaText);

public partial class ArticlesPage : ContentPage
{
    private readonly IServiceProvider _services;
    private CancellationTokenSource? _searchCts;
    private string? _selectedTag;
    private readonly List<(string? Tag, Border Chip)> _chips = [];

    // #4 — compact (1-line) vs expanded (up to 3 lines) list items; shared preference with the tree.
    private bool _expanded = Preferences.Get(TreePage.ListExpandedKey, false);
    public LineBreakMode ItemsLineBreakMode => _expanded ? LineBreakMode.WordWrap : LineBreakMode.TailTruncation;
    public int ItemsMaxLines => _expanded ? 3 : 1;

    public ArticlesPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        UpdateExpandIcon();
    }

    // The chips are Border + Label pills (like the article's TagLine), so they measure their real
    // height at any system font — no HeightRequest hack needed. They are coloured from the theme
    // tokens; follow an appearance change while the page is shown.
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler != null && Application.Current is { } app)
            app.RequestedThemeChanged += OnThemeChanged;
        else if (Application.Current is { } gone)
            gone.RequestedThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, AppThemeChangedEventArgs e) => UpdateChipStates();

    // The accessible name follows the state (expand all / collapse all), like the Tree page.
    // Set the name first: a menu item re-reads its name when its icon changes.
    private void UpdateExpandIcon()
    {
        AutomationProperties.SetName(ExpandToggle, AppStrings.Get(_expanded ? "CollapseAll" : "ExpandAll"));
        ExpandToggle.IconImageSource = Services.ThemeManager.Icon(_expanded ? "icon_compact" : "icon_expand");
    }

    private void OnToggleExpandClicked(object? sender, EventArgs e)
    {
        _expanded = !_expanded;
        Preferences.Set(TreePage.ListExpandedKey, _expanded);
        UpdateExpandIcon();
        OnPropertyChanged(nameof(ItemsLineBreakMode));
        OnPropertyChanged(nameof(ItemsMaxLines));
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        SyncExpandedFromPrefs(); // pick up a toggle made on another (cached) tab
        NavBusyOverlay.IsVisible = false; // clear tap-loading when returning from the article
        _ = LoadTagChipsAsync();
        _ = RunSearchAsync();
        _services.GetRequiredService<Services.SyncNotificationService>().ClearPendingUpdates();
    }

    private void SyncExpandedFromPrefs()
    {
        var pref = Preferences.Get(TreePage.ListExpandedKey, false);
        if (pref == _expanded) return;
        _expanded = pref;
        UpdateExpandIcon();
        OnPropertyChanged(nameof(ItemsLineBreakMode));
        OnPropertyChanged(nameof(ItemsMaxLines));
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _searchCts?.Cancel();
        _services.GetRequiredService<Services.SyncStatusService>().CancelSync();
    }

    // ── Tag chips (.filter-rail) ─────────────────────────────────────────────

    private async Task LoadTagChipsAsync()
    {
        List<ConceptTagInfo> tags;
        try
        {
            tags = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                return await scope.ServiceProvider.GetRequiredService<ConceptTagService>().ListAsync(null);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadTags error: {ex.Message}");
            return;
        }

        TagChipsLayout.Children.Clear();
        _chips.Clear();
        AddChip(null, AppStrings.Get("AllTags"));
        foreach (var tag in tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            AddChip(tag.Name, "#" + tag.Name);
        UpdateChipStates();
    }

    private void AddChip(string? tag, string text)
    {
        // The same pill as the article's TagLine (Border + Label), not a Button: a MaterialButton keeps
        // its own min height and insets, so the strip measured shorter than it drew and cut the chips
        // at a large font. A pill measures its real height, so the strip shows whole chips at any font.
        var label = new Label
        {
            Text = text,
            FontSize = 12,
            FontFamily = ChipFont(),
            VerticalOptions = LayoutOptions.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        var chip = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(14) },
            Padding = new Thickness(10, 4),
            // Start, not Fill: inside the horizontal ScrollView a filled chip was stretched taller than
            // the strip draws (device check at font 1.15), which cut the chips at the bottom.
            VerticalOptions = LayoutOptions.Start,
            Content = label,
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            _selectedTag = tag;
            UpdateChipStates();
            _ = RunSearchAsync();
        };
        chip.GestureRecognizers.Add(tap);
        _chips.Add((tag, chip));
        TagChipsLayout.Children.Add(chip);
    }

    private void UpdateChipStates()
    {
        foreach (var (tag, chip) in _chips)
        {
            var selected = tag == _selectedTag;
            chip.BackgroundColor = ThemeManager.Token(selected ? "AccentSoft" : "SurfaceHigh");
            chip.Stroke = ThemeManager.Token(selected ? "AccentBorder" : "Border");
            if (chip.Content is Label label)
                label.TextColor = ThemeManager.Token(selected ? "AccentText" : "TextSecondary");
        }
    }

    // TryGetValue, not the indexer: a missing key must never throw.
    private static string ChipFont()
        => Application.Current?.Resources.TryGetValue("FontBodySemibold", out var v) == true && v is string s ? s : "";

    // ── Search ───────────────────────────────────────────────────────────────

    // busy-exempt: a search reads the articles on this phone, nothing is sent
    private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ClearSearchButton.IsVisible = (e.NewTextValue ?? "").Length > 0;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        try { await Task.Delay(350, token); }
        catch (TaskCanceledException) { return; }
        await RunSearchAsync();
    }

    // busy-exempt: a search reads the articles on this phone, nothing is sent
    private async void OnContentSearchToggled(object? sender, ToggledEventArgs e) => await RunSearchAsync();

    private void OnClearSearchClicked(object? sender, EventArgs e)
    {
        SearchBar.Text = "";
        SearchBar.Focus();
    }

    private void OnResetSearchClicked(object? sender, EventArgs e)
    {
        _selectedTag = null;
        SearchBar.Text = "";
        UpdateChipStates();
        _ = RunSearchAsync();
    }

    /// <summary>
    /// The mock-up's search: an empty query with no tag shows the recent articles; a query goes
    /// through the core SearchService (titles and tags, plus the texts when the switch is on —
    /// the same calls the iPhone SearchPage makes); a tag chip narrows the result to that tag.
    /// </summary>
    private async Task RunSearchAsync()
    {
        var query = (SearchBar.Text ?? "").Trim();
        var inText = ContentSearchSwitch.IsToggled;
        var tag = _selectedTag;

        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;
        try
        {
            var (articles, recent) = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var sp = scope.ServiceProvider;
                var repo = sp.GetRequiredService<IArticleRepository>();

                if (query.Length == 0 && tag is null)
                {
                    var all = await repo.ListAsync();
                    return (all.OrderByDescending(a => a.UpdatedAt).Take(20).ToList(), true);
                }

                HashSet<Guid>? tagIds = null;
                if (tag is not null)
                {
                    var tagged = await sp.GetRequiredService<ConceptTagService>().SearchByConceptAsync(tag);
                    tagIds = tagged.Select(t => t.Id).ToHashSet();
                }

                List<Article> found;
                if (query.Length > 0)
                {
                    var search = sp.GetRequiredService<SearchService>();
                    var results = inText
                        ? await search.SearchWithContentAsync(query)
                        : await search.SearchAsync(query);
                    found = results.Articles;
                }
                else
                {
                    found = await repo.ListAsync();
                }

                if (tagIds is not null)
                    found = found.Where(a => tagIds.Contains(a.Id)).ToList();
                if (query.Length == 0)
                    found = found.OrderByDescending(a => a.UpdatedAt).ToList();
                return (found, false);
            });

            var rows = articles.Select(a =>
            {
                var path = ContentFormat.NormalizePath(a.TreePath);
                var meta = $"{path} · {ContentFormat.ShortDate(a.UpdatedAt)}";
                return new SearchRow(a.Id, a.Title, path, a.UpdatedAt, a.Protected,
                    Highlight(a.Title, query, isTitle: true),
                    Highlight(meta, query, isTitle: false));
            }).ToList();

            ResultsLabel.Text = recent ? AppStrings.Get("Recent") : AppStrings.Format("FoundFormat", rows.Count);
            ArticlesList.ItemsSource = rows;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Search error: {ex.Message}");
            ArticlesList.ItemsSource = new List<SearchRow>();
            ResultsLabel.Text = AppStrings.Format("FoundFormat", 0);
        }
        finally
        {
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
        }
    }

    /// <summary>The mock-up's .search-hit: the query matches on the accent-soft background, in the accent text colour, bold.</summary>
    private static FormattedString Highlight(string text, string query, bool isTitle)
    {
        var resources = Application.Current?.Resources;
        var normal = new Span
        {
            TextColor = ThemeManager.Token(isTitle ? "Text" : "TextSecondary"),
            FontFamily = (string?)resources?[isTitle ? "FontBodySemibold" : "FontBody"],
            FontSize = isTitle ? 15 : 13,
        };
        var hit = new Span
        {
            TextColor = ThemeManager.Token("AccentText"),
            BackgroundColor = ThemeManager.Token("AccentSoft"),
            FontFamily = (string?)resources?["FontBodyBold"],
            FontSize = isTitle ? 15 : 13,
        };

        var result = new FormattedString();
        if (query.Length == 0)
        {
            result.Spans.Add(new Span { Text = text, TextColor = normal.TextColor, FontFamily = normal.FontFamily, FontSize = normal.FontSize });
            return result;
        }

        var lower = text.ToLowerInvariant();
        var q = query.ToLowerInvariant();
        var idx = 0;
        while (idx < text.Length)
        {
            var at = lower.IndexOf(q, idx, StringComparison.Ordinal);
            if (at < 0)
            {
                result.Spans.Add(new Span { Text = text[idx..], TextColor = normal.TextColor, FontFamily = normal.FontFamily, FontSize = normal.FontSize });
                break;
            }
            if (at > idx)
                result.Spans.Add(new Span { Text = text[idx..at], TextColor = normal.TextColor, FontFamily = normal.FontFamily, FontSize = normal.FontSize });
            result.Spans.Add(new Span { Text = text[at..(at + q.Length)], TextColor = hit.TextColor, BackgroundColor = hit.BackgroundColor, FontFamily = hit.FontFamily, FontSize = hit.FontSize });
            idx = at + q.Length;
        }
        return result;
    }

    // ── Rows ─────────────────────────────────────────────────────────────────

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        // Pulling the list down asks for a sync round: a request to the computer, not a read (BMB-311). The one busy helper refuses a second round while one runs;
        // the spinner of the pull is the pull's own, so no pop-up; a failure to even ask is said in full.
        try
        {
            await BusyAction.RunAsync(RefreshView, async () =>
            {
                var syncNotify = _services.GetRequiredService<Services.SyncNotificationService>();
                if (!syncNotify.HasPendingUpdates)
                {
                    var syncStatus = _services.GetRequiredService<Services.SyncStatusService>();
                    await syncStatus.SyncNowAsync();
                }

                await RunSearchAsync();
                syncNotify.ClearPendingUpdates();
            }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
        }
        finally
        {
            RefreshView.IsRefreshing = false;
        }
    }

    // busy-exempt: opens the article tapped (the screen has its own overlay while the page opens), nothing is sent
    private async void OnArticleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject bo) return;
        if (bo.BindingContext is not SearchRow row) return;
        NavBusyOverlay.IsVisible = true;
        try
        {
            await Task.Delay(16);
            await Shell.Current.GoToAsync($"articleDetail?id={row.Id}");
        }
        finally { NavBusyOverlay.IsVisible = false; }
    }

    // busy-exempt: opens the row action sheet; the rename/move/delete work runs behind BusyAction in TreeItemActions
    private async void OnArticleMenuClicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not SearchRow row) return;
        var item = new TreeListItem(row.Title, false, null, row.Id, row.UpdatedAt, row.Protected, "");
        await TreeItemActions.RunAsync(this, _services, item, RunSearchAsync);
    }

    // busy-exempt: opens the editor, nothing is sent and nothing waits
    private async void OnNewArticleClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("articleEdit");
    }

    // The tag list until the Search screen grows its tag management (the chips filter only):
    // the list itself stays reachable from here. Pushed relative so it keeps the back arrow and tab bar.
    // busy-exempt: opens the tag list, nothing is sent and nothing waits
    private async void OnTagsClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("tags");
    }
}
