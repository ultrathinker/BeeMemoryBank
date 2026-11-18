using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Controls;
using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

[QueryProperty(nameof(FolderName), "folderName")]
[QueryProperty(nameof(FolderPath), "path")]
public partial class TreeFolderPage : ContentPage
{
    private readonly IServiceProvider _services;
    private string _currentPath = "/";

    // #4 — compact vs expanded list items; shared preference with the rest of the lists.
    private bool _expanded = Preferences.Get(TreePage.ListExpandedKey, false);
    public LineBreakMode ItemsLineBreakMode => _expanded ? LineBreakMode.WordWrap : LineBreakMode.TailTruncation;
    public int ItemsMaxLines => _expanded ? 3 : 1;

    public TreeFolderPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        UpdateExpandIcon();
    }

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
        NavBusyOverlay.IsVisible = false; // clear tap-loading when returning

        // The first appearance is loaded by the FolderPath query setter. Coming back (from an
        // article just created, edited or deleted here) must reload too, or the list is stale
        // until the folder is opened again, like the Tree and Search tabs already do.
        if (_appearedBefore)
            _ = LoadItemsAsync(_currentPath);
        _appearedBefore = true;
    }

    private bool _appearedBefore;

    public string FolderName
    {
        // Folder name is reflected in the breadcrumb; the title shows the nesting level instead.
        set { /* no-op: see FolderPath for title/breadcrumb */ }
    }

    public string FolderPath
    {
        set
        {
            if (value == null) return;
            _currentPath = Uri.UnescapeDataString(value);
            _ = LoadItemsAsync(_currentPath);
        }
    }

    /// <summary>
    /// The mock-up's breadcrumb rail: the root and every folder on the way down. A crumb taps
    /// back to its level — the root returns to the Tree tab, an ancestor pops the pages above it.
    /// </summary>
    private void BuildBreadcrumbs(string currentPath)
    {
        BreadcrumbsLayout.Children.Clear();

        var segments = currentPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        BreadcrumbsLayout.Children.Add(TreeCrumb.Build(AppStrings.Get("TreeRoot"),
            isCurrent: segments.Length == 0,
            onTap: segments.Length == 0 ? null : () => _ = Shell.Current.GoToAsync("//tree"),
            isRoot: true));

        var path = "";
        for (var i = 0; i < segments.Length; i++)
        {
            path += "/" + segments[i];
            var isCurrent = i == segments.Length - 1;
            var pops = segments.Length - 1 - i;
            var target = path;
            BreadcrumbsLayout.Children.Add(TreeCrumb.Build(segments[i], isCurrent,
                isCurrent ? null : () => _ = GoBackAsync(pops)));
        }
    }

    // busy-exempt: pops the pushed folder pages back to the crumb tapped, nothing is sent
    private async Task GoBackAsync(int pops)
    {
        // One ".." per level: a multi-segment relative route is not something every Shell
        // version agrees on, single pops always are.
        for (var i = 0; i < pops; i++)
            await Shell.Current.GoToAsync("..");
    }

    private async Task LoadItemsAsync(string currentPath)
    {
        // #5 — orient the user: title shows the depth as "Tree [N]", the breadcrumb shows the way.
        var depth = currentPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        Title = depth > 0 ? AppStrings.Format("TreeDepthFormat", depth) : AppStrings.Get("TabTree");
        BuildBreadcrumbs(currentPath);

        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;

        try
        {
            var groups = await Task.Run(() => TreeContent.BuildGroupsAsync(_services, currentPath));
            FolderList.ItemsSource = groups;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadTreeFolder error: {ex.Message}");
        }
        finally
        {
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
        }
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        // Pulling the list down asks for a sync round: a request to the computer, not a read (BMB-311).
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

                await LoadItemsAsync(_currentPath);
                syncNotify.ClearPendingUpdates();
            }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
        }
        finally
        {
            RefreshView.IsRefreshing = false;
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
        await TreeItemActions.RunAsync(this, _services, item, () => LoadItemsAsync(_currentPath));
    }

    // busy-exempt: opens the row action sheet; the rename/move/delete work runs behind BusyAction in TreeItemActions
    private async void OnItemMenuClicked(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not TreeListItem item) return;
        await TreeItemActions.RunAsync(this, _services, item, () => LoadItemsAsync(_currentPath));
    }

    // busy-exempt: opens the editor, nothing is sent and nothing waits
    private async void OnNewArticleClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync($"articleEdit?path={Uri.EscapeDataString(_currentPath)}");
    }

    private async void OnNewFolderClicked(object? sender, EventArgs e)
    {
        var parentLabel = _currentPath == "/"
            ? AppStrings.Get("NewFolderParentRoot")
            : AppStrings.Format("NewFolderParentFormat", ContentFormat.FolderName(_currentPath));
        var name = await this.ShowInputPopupAsync(AppStrings.Get("NewFolder"), parentLabel,
            accept: AppStrings.Get("Create"), cancel: AppStrings.Get("Cancel"),
            placeholder: AppStrings.Get("FolderNamePlaceholder"), maxLength: 100);
        if (string.IsNullOrWhiteSpace(name)) return;

        await BusyAction.RunAsync(sender as BindableObject, async () =>
        {
            using var scope = _services.CreateScope();
            var folderSvc = scope.ServiceProvider.GetRequiredService<FolderService>();
            var newPath = _currentPath.TrimEnd('/') + "/" + name.Trim().Trim('/');
            await folderSvc.CreateAsync(newPath);
            await LoadItemsAsync(_currentPath);
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }
}
