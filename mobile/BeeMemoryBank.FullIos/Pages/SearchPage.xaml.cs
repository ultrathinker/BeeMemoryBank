using BeeMemoryBank.Busy;
using BeeMemoryBank.FullIos.Controls;
using BeeMemoryBank.FullIos.Localization;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>
/// Search as you type (after a short pause): titles, folders and ids through the full-text index, and
/// the notes' text when the switch is on. An empty query with no tag shows the recent notes; a tag chip
/// narrows the result. The rows' density (compact / expanded) is shared with the tree. Pull to refresh.
/// </summary>
public partial class SearchPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly NotesService _notes;
    private readonly FullSync _sync;
    private CancellationTokenSource? _typing;
    private string? _selectedTag;
    private readonly List<(string? Tag, Border Chip)> _chips = [];

    public SearchPage(IServiceProvider services, NotesService notes, FullSync sync)
    {
        InitializeComponent();
        Title = AppStrings.Get("TabSearch");
        EmptyDesc.Text = AppStrings.Get("SearchHintDesc");
        _services = services;
        _notes = notes;
        _sync = sync;
        UpdateExpandIcon();
    }

    // The shared density (compact one line / expanded up to three), bound by the row template.
    public LineBreakMode ItemsLineBreakMode => ListDensity.LineBreakMode;
    public int ItemsMaxLines => ListDensity.MaxLines;


    protected override void OnAppearing()
    {
        base.OnAppearing();
        UpdateExpandIcon();
        OnPropertyChanged(nameof(ItemsLineBreakMode));
        OnPropertyChanged(nameof(ItemsMaxLines));
        ListDensity.Changed += OnDensityChanged;
        if (string.IsNullOrEmpty(Query.Text)) Query.Focus();
        _ = LoadChipsAsync();
        _ = RunAsync(CancellationToken.None);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        ListDensity.Changed -= OnDensityChanged;
    }

    private void OnDensityChanged(object? sender, EventArgs e)
    {
        UpdateExpandIcon();
        OnPropertyChanged(nameof(ItemsLineBreakMode));
        OnPropertyChanged(nameof(ItemsMaxLines));
    }

    private void UpdateExpandIcon()
    {
        ExpandToggle.ImageSource = ImageSource.FromFile(ThemeManager.Icon(ListDensity.Expanded ? "icon_compact" : "icon_expand"));
        SemanticProperties.SetDescription(ExpandToggle, AppStrings.Get(ListDensity.Expanded ? "CollapseAll" : "ExpandAll"));
    }

    private void OnToggleExpand(object? sender, EventArgs e) => ListDensity.Toggle();

    /// <summary>Searches for <paramref name="query"/> as if it had been typed.</summary>
    public void Find(string query) => Query.Text = query;

    private async void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        _typing?.Cancel();
        _typing = new CancellationTokenSource();
        var token = _typing.Token;
        try { await Task.Delay(350, token); }
        catch (TaskCanceledException) { return; }
        await RunAsync(token);
    }

    // busy-exempt: a search reads the notes on this phone, nothing is sent
    private async void OnSearch(object? sender, EventArgs e) => await RunAsync(CancellationToken.None);

    // busy-exempt: a search reads the notes on this phone, nothing is sent
    private async void OnInTextToggled(object? sender, ToggledEventArgs e) => await RunAsync(CancellationToken.None);

    // busy-exempt: opens the tags page, nothing is sent and nothing waits
    private async void OnTags(object? sender, EventArgs e) =>
        await Navigation.PushAsync(_services.GetRequiredService<TagsPage>());

    private void OnReset(object? sender, EventArgs e)
    {
        _selectedTag = null;
        Query.Text = "";
        UpdateChipStates();
        _ = RunAsync(CancellationToken.None);
    }

    // ---- the tag filter chips ----

    private async Task LoadChipsAsync()
    {
        try
        {
            var tags = await Task.Run(() => _notes.AllTagsAsync(null));
            TagChips.Children.Clear();
            _chips.Clear();
            AddChip(null, AppStrings.Get("AllTags"));
            foreach (var t in tags.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                AddChip(t.Name, "#" + t.Name);
            UpdateChipStates();
        }
        catch (Exception) { /* the chips are a convenience; the search still works without them */ }
    }

    private void AddChip(string? tag, string text)
    {
        var chip = new Border
        {
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(14) },
            Padding = new Thickness(12, 6),
            Content = new Label { Text = text, FontSize = 13, VerticalOptions = LayoutOptions.Center },
        };
        var captured = tag;
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            _selectedTag = captured;
            UpdateChipStates();
            _ = RunAsync(CancellationToken.None);
        };
        chip.GestureRecognizers.Add(tap);
        _chips.Add((tag, chip));
        TagChips.Children.Add(chip);
    }

    private void UpdateChipStates()
    {
        foreach (var (tag, chip) in _chips)
        {
            var selected = tag == _selectedTag;
            chip.BackgroundColor = ThemeManager.Token(selected ? "AccentSoft" : "SurfaceHigh");
            chip.Stroke = ThemeManager.Token(selected ? "AccentBorder" : "Border");
            if (chip.Content is Label label) label.TextColor = ThemeManager.Token(selected ? "AccentText" : "TextSecondary");
        }
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        try
        {
            await BusyAction.RunAsync(Refresher, async () =>
            {
                await _sync.RoundAsync();
                await LoadChipsAsync();
                await RunAsync(CancellationToken.None);
            }, new BusyOptions { FailureTitle = AppStrings.Get("TabSync") });
        }
        finally
        {
            Refresher.IsRefreshing = false;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var query = Query.Text ?? "";
        var inText = InText.IsToggled;
        var tag = _selectedTag;
        try
        {
            var found = await Task.Run(() => _notes.SearchFilteredAsync(query, inText, tag), token);
            if (token.IsCancellationRequested) return;
            Results.ItemsSource = found;
            var recent = query.Trim().Length == 0 && tag is null;
            ResultsLabel.Text = recent ? AppStrings.Get("Recent") : AppStrings.Format("FoundFormat", found.Count);
            var empty = found.Count == 0 && !recent;
            EmptyTitle.IsVisible = empty;
            ResetButton.IsVisible = empty;
            if (empty) EmptyDesc.Text = AppStrings.Get("NothingFoundDesc");
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            Results.ItemsSource = null;
            EmptyTitle.IsVisible = true;
            EmptyTitle.Text = AppStrings.Get("ErrorTitle");
            EmptyDesc.Text = AppStrings.Get("SearchErrorDesc");
        }
    }

    // busy-exempt: opens the note tapped, nothing is sent and nothing waits
    private async void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not NoteListItem item) return;
        if (item.IsFolder)
            await Navigation.PushAsync(_services.GetRequiredService<FolderPage>().At(item.Path));
        else if (item.NoteId is { } id)
            await Navigation.PushAsync(_services.GetRequiredService<NotePage>().For(id));
    }
}
