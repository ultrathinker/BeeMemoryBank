using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Controls;
using BeeMemoryBank.Mobile.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

/// <summary>One row of the tree: a folder or an article, with the meta line the mock-up shows.</summary>
public record TreeListItem(
    string Name,
    bool IsFolder,
    string? FolderPath,
    Guid? ArticleId,
    DateTime? UpdatedAt,
    bool Protected,
    string Meta)
{
    // Lock badge shown only for protected articles (folders are never protected).
    public bool ShowLock => Protected && !IsFolder;
}

/// <summary>A named section of the tree list ("Folders (2)", "Articles (5)").</summary>
public class TreeGroup(string name, List<TreeListItem> items) : List<TreeListItem>(items)
{
    public string Name { get; } = name;
}

public partial class TreePage : ContentPage
{
    private readonly IServiceProvider _services;

    // #4 — compact (1-line, truncated) vs expanded (up to 3 lines) list items, toggled from the
    // toolbar and remembered across launches. List item labels bind LineBreakMode/MaxLines to these.
    private bool _expanded = Preferences.Get(ListExpandedKey, false);
    public const string ListExpandedKey = "list_items_expanded";
    public LineBreakMode ItemsLineBreakMode => _expanded ? LineBreakMode.WordWrap : LineBreakMode.TailTruncation;
    public int ItemsMaxLines => _expanded ? 3 : 1;

    public TreePage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        UpdateExpandIcon();
        BreadcrumbsLayout.Children.Add(TreeCrumb.Build(AppStrings.Get("TreeRoot"), isCurrent: true, onTap: null, isRoot: true));
    }

    // Swap the toolbar icon so it reflects the current state (arrows-out = tap to expand,
    // arrows-in = tap to collapse). The accessible name follows the state too. Set the name
    // first: a menu item re-reads its name when its icon changes, so the icon swap applies it.
    private void UpdateExpandIcon()
    {
        AutomationProperties.SetName(ExpandToggle, AppStrings.Get(_expanded ? "CollapseAll" : "ExpandAll"));
        ExpandToggle.IconImageSource = Services.ThemeManager.Icon(_expanded ? "icon_compact" : "icon_expand");
    }

    private void OnToggleExpandClicked(object? sender, EventArgs e)
    {
        _expanded = !_expanded;
        Preferences.Set(ListExpandedKey, _expanded);
        UpdateExpandIcon();
        OnPropertyChanged(nameof(ItemsLineBreakMode));
        OnPropertyChanged(nameof(ItemsMaxLines));
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        SyncExpandedFromPrefs(); // pick up a toggle made on another (cached) tab
        NavBusyOverlay.IsVisible = false; // clear tap-loading when returning
        _ = LoadItemsAsync();
        _services.GetRequiredService<Services.SyncNotificationService>().ClearPendingUpdates();
    }

    // Tabs are cached, so a toggle on the Search tab must be reflected here on re-appear.
    private void SyncExpandedFromPrefs()
    {
        var pref = Preferences.Get(ListExpandedKey, false);
        if (pref == _expanded) return;
        _expanded = pref;
        UpdateExpandIcon();
        OnPropertyChanged(nameof(ItemsLineBreakMode));
        OnPropertyChanged(nameof(ItemsMaxLines));
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _services.GetRequiredService<Services.SyncStatusService>().CancelSync();
    }

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

                await LoadItemsAsync();
                syncNotify.ClearPendingUpdates();
            }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
        }
        finally
        {
            RefreshView.IsRefreshing = false;
        }
    }

    private async Task LoadItemsAsync()
    {
        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;

        try
        {
            var groups = await Task.Run(() => TreeContent.BuildGroupsAsync(_services, "/"));
            TreeList.ItemsSource = groups;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadTree error: {ex.Message}");
        }
        finally
        {
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
        }
    }

    // busy-exempt: opens the folder or article tapped (the screen has its own overlay while the page opens), nothing is sent
    private async void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject bo) return;
        if (bo.BindingContext is not TreeListItem item) return;

        string? route = null;
        if (item.IsFolder && item.FolderPath is not null)
            route = $"treeFolder?path={Uri.EscapeDataString(item.FolderPath)}&folderName={Uri.EscapeDataString(item.Name)}";
        else if (!item.IsFolder && item.ArticleId.HasValue)
            route = $"articleDetail?id={item.ArticleId.Value}";
        if (route is null) return;

        NavBusyOverlay.IsVisible = true;
        try
        {
            await Task.Delay(16);
            await Shell.Current.GoToAsync(route);
        }
        finally { NavBusyOverlay.IsVisible = false; }
    }

    // The mock-up's long press; MAUI has no long-press gesture, the double tap stands in for it.
    // busy-exempt: opens the row action sheet; the rename/move/delete work runs behind BusyAction in TreeItemActions
    private async void OnItemLongPressed(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject bo) return;
        if (bo.BindingContext is not TreeListItem item) return;
        await TreeItemActions.RunAsync(this, _services, item, LoadItemsAsync);
    }

    // busy-exempt: opens the row action sheet; the rename/move/delete work runs behind BusyAction in TreeItemActions
    private async void OnItemMenuClicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not TreeListItem item) return;
        await TreeItemActions.RunAsync(this, _services, item, LoadItemsAsync);
    }

    // busy-exempt: opens the editor, nothing is sent and nothing waits
    private async void OnNewArticleClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("articleEdit?path=%2F");
    }

    private async void OnNewFolderClicked(object? sender, EventArgs e)
    {
        var name = await this.ShowInputPopupAsync(AppStrings.Get("NewFolder"), AppStrings.Get("NewFolderParentRoot"),
            accept: AppStrings.Get("Create"), cancel: AppStrings.Get("Cancel"),
            placeholder: AppStrings.Get("FolderNamePlaceholder"), maxLength: 100);
        if (string.IsNullOrWhiteSpace(name)) return;

        await BusyAction.RunAsync(sender as BindableObject, async () =>
        {
            using var scope = _services.CreateScope();
            var folderSvc = scope.ServiceProvider.GetRequiredService<FolderService>();
            await folderSvc.CreateAsync("/" + name.Trim().Trim('/'));
            await LoadItemsAsync();
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }
}
