using BeeMemoryBank.Busy;
using BeeMemoryBank.FullIos.Controls;
using BeeMemoryBank.FullIos.Localization;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>
/// A folder of the tree in the design's shape: the breadcrumb rail back to the root, the card
/// rows (icon box, name, human date) with the ⋮ sheet of the actions this app has, the empty
/// state with the two actions. Tap opens, swipe a note left moves it to the trash.
/// </summary>
public partial class FolderPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly NotesService _notes;
    private readonly FullSync _sync;
    private string _path = "/";

    public FolderPage(IServiceProvider services, NotesService notes, FullSync sync)
    {
        InitializeComponent();
        _services = services;
        _notes = notes;
        _sync = sync;
        UpdateToolbarIcons();
        UpdateExpandIcon();
    }

    /// <summary>Which folder this page shows (the top one unless set).</summary>
    public FolderPage At(string path)
    {
        _path = NotesService.NormalizePath(path);
        return this;
    }

    internal string FolderPath => _path;

    // The shared density (compact one line / expanded up to three), bound by the row template.
    public LineBreakMode ItemsLineBreakMode => ListDensity.LineBreakMode;
    public int ItemsMaxLines => ListDensity.MaxLines;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Title = _path == "/" ? AppStrings.Get("TabTree") : NotesService.FolderTitle(_path);
        BuildCrumbs();
        UpdateToolbarIcons();
        UpdateExpandIcon();
        OnPropertyChanged(nameof(ItemsLineBreakMode));
        OnPropertyChanged(nameof(ItemsMaxLines));
        ListDensity.Changed += OnDensityChanged;
        if (Application.Current is { } app) app.RequestedThemeChanged += OnThemeChanged;
        _sync.Changed += OnSyncChanged;
        _ = LoadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        ListDensity.Changed -= OnDensityChanged;
        if (Application.Current is { } app) app.RequestedThemeChanged -= OnThemeChanged;
        _sync.Changed -= OnSyncChanged;
    }

    private void OnDensityChanged(object? sender, EventArgs e)
    {
        UpdateExpandIcon();
        OnPropertyChanged(nameof(ItemsLineBreakMode));
        OnPropertyChanged(nameof(ItemsMaxLines));
    }

    private void UpdateExpandIcon()
    {
        // An overflow-menu item: the menu shows its text (the icon goes next to it).
        ExpandToggle.Text = AppStrings.Get(ListDensity.Expanded ? "CollapseAll" : "ExpandAll");
        ExpandToggle.IconImageSource = ImageSource.FromFile(ThemeManager.Icon(ListDensity.Expanded ? "icon_compact" : "icon_expand"));
        SemanticProperties.SetDescription(ExpandToggle, ExpandToggle.Text);
    }

    private void OnToggleExpand(object? sender, EventArgs e) => ListDensity.Toggle();

    private void OnThemeChanged(object? sender, AppThemeChangedEventArgs e) => UpdateToolbarIcons();

    // The toolbar icons are set from code, like the shell's tab icons: the XAML AppThemeBinding
    // on a ToolbarItem did not pick the dark file on iOS (the light strokes sat invisible on the
    // dark bar). The colour lives in the file, one pair per appearance.
    private void UpdateToolbarIcons()
    {
        NewArticleItem.IconImageSource = ImageSource.FromFile(ThemeManager.Icon("icon_new_article"));
        NewFolderItem.IconImageSource = ImageSource.FromFile(ThemeManager.Icon("icon_new_folder"));
    }

    // The rail: the root with the home icon, then every segment; the current one in the accent,
    // the others tappable back to their folder.
    private void BuildCrumbs()
    {
        Crumbs.Children.Clear();
        var segments = _path == "/" ? Array.Empty<string>() : _path.Trim('/').Split('/');
        Crumbs.Children.Add(TreeCrumb.Build(AppStrings.Get("TreeRoot"), isCurrent: segments.Length == 0,
            onTap: segments.Length == 0 ? null : () => GoToFolder("/"), isRoot: true));
        var walked = "";
        for (var i = 0; i < segments.Length; i++)
        {
            walked += "/" + segments[i];
            var target = walked;
            var current = i == segments.Length - 1;
            Crumbs.Children.Add(TreeCrumb.Build(segments[i], current, current ? null : () => GoToFolder(target)));
        }
    }

    // A crumb goes back to the folder's page when it is already in the stack, else opens it.
    private async void GoToFolder(string path)
    {
        var existing = Navigation.NavigationStack.OfType<FolderPage>().LastOrDefault(p => p.FolderPath == path);
        if (existing is not null && existing != this)
        {
            while (Navigation.NavigationStack.Count > 1 && Navigation.NavigationStack[^1] != existing)
                await Navigation.PopAsync();
        }
        else if (existing is null)
        {
            await Navigation.PushAsync(_services.GetRequiredService<FolderPage>().At(path));
        }
    }

    // A round that brought something in refreshes the list (the note written on the computer appears without a pull).
    private void OnSyncChanged()
    {
        if (!_sync.IsRunning && _sync.LastRound is { Applied: > 0 }) MainThread.BeginInvokeOnMainThread(() => _ = LoadAsync());
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        // Pulling the list down asks for a sync round: a request to the computer, not a read. The one busy helper (BMB-311) refuses a second round while one
        // runs (a second pull, the round of the loop is the sync's own business); the spinner of the pull is the pull's own, so no pop-up, and a failure to
        // even ask is said in full.
        try
        {
            await BusyAction.RunAsync(Refresher, async () =>
            {
                await _sync.RoundAsync();
                await LoadAsync();
            }, new BusyOptions { FailureTitle = AppStrings.Get("TabSync") });
        }
        finally
        {
            Refresher.IsRefreshing = false;
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            List.ItemsSource = await Task.Run(() => _notes.ListFolderAsync(_path));
        }
        catch (Exception ex)
        {
            EmptyDesc.Text = AppStrings.Format("FolderReadError", ex.Message);
            List.ItemsSource = null;
        }
    }

    // busy-exempt: opens the folder or note tapped, nothing is sent and nothing waits
    private async void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not NoteListItem item) return;
        if (item.IsFolder)
            await Navigation.PushAsync(_services.GetRequiredService<FolderPage>().At(item.Path));
        else if (item.NoteId is { } id)
            await Navigation.PushAsync(_services.GetRequiredService<NotePage>().For(id));
    }

    // busy-exempt: opens the editor, nothing is sent and nothing waits
    private async void OnNewNote(object? sender, EventArgs e) =>
        await Navigation.PushAsync(_services.GetRequiredService<EditNotePage>().New(_path));

    // busy-exempt: opens the app's input popup, nothing is sent and nothing waits
    private async void OnNewFolder(object? sender, EventArgs e)
    {
        var message = _path == "/"
            ? AppStrings.Get("NewFolderParentRoot")
            : AppStrings.Format("NewFolderParentFormat", NotesService.FolderTitle(_path));
        var name = await this.ShowInputPopupAsync(AppStrings.Get("NewFolder"), message,
            AppStrings.Get("Create"), AppStrings.Get("Cancel"), AppStrings.Get("FolderNamePlaceholder"), maxLength: 100);
        if (string.IsNullOrWhiteSpace(name)) return;
        // The one busy helper (BMB-311): the entry is off until the folder is made; a failure is said in full.
        await BusyAction.RunAsync(sender as BindableObject, async () =>
        {
            await _notes.CreateFolderAsync(_path, name);
            await LoadAsync();
        }, new BusyOptions { FailureTitle = AppStrings.Get("NewFolder") });
    }

    private async void OnSwipeDelete(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not NoteListItem { NoteId: { } id } item) return;
        await BusyAction.RunAsync(sender as BindableObject, async () =>
        {
            await _notes.DeleteAsync(id);
            await LoadAsync();
        }, new BusyOptions { FailureTitle = item.Name });
    }

    // busy-exempt: opens the row's sheet of actions, nothing is sent and nothing waits
    private async void OnRowMenu(object? sender, EventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not NoteListItem item) return;
        await ShowActionsAsync(item);
    }

    /// <summary>The ⋮ sheet: the actions this app has for the row (the design's item-actions sheet).</summary>
    internal async Task ShowActionsAsync(NoteListItem item)
    {
        var resources = Application.Current?.Resources;
        var stack = new VerticalStackLayout { Spacing = 8 };
        var sheet = new BottomSheet.BottomSheetPage(stack, item.Name);

        void Add(string text, string styleKey, Func<Task> act)
        {
            var button = new Button { Text = text, HorizontalOptions = LayoutOptions.Fill };
            if (resources is not null && resources.TryGetValue(styleKey, out var s) && s is Style style) button.Style = style;
            button.Clicked += async (_, _) =>
            {
                // Close the sheet FIRST, then act: the action may push a page or another modal.
                await sheet.CloseAsync();
                await act();
            };
            stack.Children.Add(button);
        }

        if (item.IsFolder)
        {
            Add(AppStrings.Get("NewArticle"), "PrimaryButton", () =>
                Navigation.PushAsync(_services.GetRequiredService<EditNotePage>().New(item.Path)));
            Add(AppStrings.Get("NewFolder"), "SecondaryButton", async () =>
            {
                var name = await this.ShowInputPopupAsync(AppStrings.Get("NewFolder"),
                    AppStrings.Format("NewFolderParentFormat", item.Name),
                    AppStrings.Get("Create"), AppStrings.Get("Cancel"), AppStrings.Get("FolderNamePlaceholder"), maxLength: 100);
                if (string.IsNullOrWhiteSpace(name)) return;
                await BusyAction.RunAsync(null, async () =>
                {
                    await _notes.CreateFolderAsync(item.Path, name);
                    await LoadAsync();
                }, new BusyOptions { FailureTitle = AppStrings.Get("NewFolder") });
            });
            Add(AppStrings.Get("Rename"), "SecondaryButton", () => RenameAsync(item));
            Add(AppStrings.Get("Move"), "SecondaryButton", () => MoveAsync(item));
            Add(AppStrings.Get("Delete"), "DangerButton", async () =>
            {
                // What the dialog says is what the core does: subfolders go with the folder,
                // the articles inside move to the root.
                if (!await AppDialog.ConfirmAsync(this, AppStrings.Get("DeleteFolderTitle"), AppStrings.Get("DeleteFolderDesc"),
                        AppStrings.Get("Delete"), danger: true)) return;
                await BusyAction.RunAsync(null, async () =>
                {
                    await _notes.DeleteFolderAsync(item.Path);
                    await LoadAsync();
                }, new BusyOptions { FailureTitle = item.Name });
            });
        }
        else if (item.NoteId is { } id)
        {
            Add(AppStrings.Get("Edit"), "SecondaryButton", async () =>
            {
                var note = await Task.Run(() => _notes.GetAsync(id));
                if (note is null) return;
                // A protected note opens in the read view first: its passphrase is asked there.
                if (note.IsProtected)
                    await Navigation.PushAsync(_services.GetRequiredService<NotePage>().For(id));
                else
                    await Navigation.PushAsync(_services.GetRequiredService<EditNotePage>().Existing(note, note.Body, null));
            });
            Add(AppStrings.Get("Rename"), "SecondaryButton", () => RenameAsync(item));
            Add(AppStrings.Get("Move"), "SecondaryButton", () => MoveAsync(item));
            Add(AppStrings.Get("Delete"), "DangerButton", async () =>
            {
                if (!await AppDialog.ConfirmAsync(this, AppStrings.Get("DeleteArticleTitle"), AppStrings.Get("DeleteArticleDesc"),
                        AppStrings.Get("Delete"), danger: true)) return;
                await BusyAction.RunAsync(null, async () =>
                {
                    await _notes.DeleteAsync(id);
                    await LoadAsync();
                }, new BusyOptions { FailureTitle = item.Name });
            });
        }

        var cancel = new Button { Text = AppStrings.Get("Cancel"), HorizontalOptions = LayoutOptions.Fill };
        if (resources is not null && resources.TryGetValue("TextButton", out var ts) && ts is Style textStyle) cancel.Style = textStyle;
        cancel.Clicked += async (_, _) => await sheet.CloseAsync();
        stack.Children.Add(cancel);

        await Navigation.PushModalAsync(sheet);
    }

    /// <summary>Rename through the core (FolderService / ArticleService), the popup prefilled with the name.</summary>
    private async Task RenameAsync(NoteListItem item)
    {
        var newName = await this.ShowInputPopupAsync(AppStrings.Get("RenameTitle"), AppStrings.Get("NewNameLabel"),
            AppStrings.Get("Save"), AppStrings.Get("Cancel"), initialValue: item.Name, maxLength: 200);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == item.Name) return;
        await BusyAction.RunAsync(null, async () =>
        {
            if (item.IsFolder) await _notes.RenameFolderAsync(item.Path, newName);
            else if (item.NoteId is { } id) await _notes.RenameArticleAsync(id, newName);
            await LoadAsync();
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }

    /// <summary>Move through the folder picker; a folder cannot move into itself or its children.</summary>
    private async Task MoveAsync(NoteListItem item)
    {
        var picker = _services.GetRequiredService<FolderPickerPage>().ForMove(item.IsFolder ? item.Path : null);
        await Navigation.PushAsync(picker);
        var destination = await picker.ResultTask;
        if (destination is null) return;
        await BusyAction.RunAsync(null, async () =>
        {
            await _notes.EnsureFolderAsync(destination);
            if (item.IsFolder) await _notes.MoveFolderAsync(item.Path, destination);
            else if (item.NoteId is { } id) await _notes.MoveArticleAsync(id, destination);
            await LoadAsync();
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }

    // busy-exempt: opens the trash page, nothing is sent and nothing waits
    private async void OnTrash(object? sender, EventArgs e) => await Navigation.PushAsync(_services.GetRequiredService<TrashPage>());

#if DEBUG || BMB_E2E
    /// <summary>The end-to-end check photographs the row sheet of the first row.</summary>
    internal async Task ShowFirstRowActionsAsync()
    {
        if (List.ItemsSource is System.Collections.IEnumerable rows && rows.Cast<object>().FirstOrDefault() is NoteListItem item)
            await ShowActionsAsync(item);
    }
#endif
}
