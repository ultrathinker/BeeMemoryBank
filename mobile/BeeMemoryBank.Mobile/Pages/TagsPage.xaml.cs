using System.Collections.ObjectModel;
using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Controls;
using BeeMemoryBank.Mobile.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

public record TagListItem(string Name, int Count)
{
    public string CountText => Services.ContentFormat.ArticleCount(Count);
}

public partial class TagsPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly ObservableCollection<TagListItem> _tagItems = new();
    private List<ConceptTagInfo> _allTags = new();

    public TagsPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        TagsList.ItemsSource = _tagItems;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadTagsAsync();
        _services.GetRequiredService<Services.SyncNotificationService>().ClearPendingUpdates();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _services.GetRequiredService<Services.SyncStatusService>().CancelSync();
    }

    private async Task LoadTagsAsync()
    {
        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;

        try
        {
            var tags = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var conceptTagSvc = scope.ServiceProvider.GetRequiredService<ConceptTagService>();
                return await conceptTagSvc.ListAsync(null);
            });
            _allTags = tags;
            ApplyTags(_allTags);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadTags error: {ex.Message}");
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

                await LoadTagsAsync();
                syncNotify.ClearPendingUpdates();
            }, new BusyOptions { FailureTitle = AppStrings.Get("SyncingTitle") });
        }
        finally
        {
            RefreshView.IsRefreshing = false;
        }
    }

    private void ApplyTags(List<ConceptTagInfo> tags)
    {
        _tagItems.Clear();
        foreach (var tag in tags)
            _tagItems.Add(new TagListItem(tag.Name, tag.ArticleCount));
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ClearTagSearchButton.IsVisible = (e.NewTextValue ?? "").Length > 0;
        ApplyTags(FilterTags(e.NewTextValue));
    }

    private void OnClearTagSearchClicked(object? sender, EventArgs e)
    {
        TagsSearchBar.Text = "";
        TagsSearchBar.Focus();
    }

    private List<ConceptTagInfo> FilterTags(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _allTags;

        return _allTags
            .Where(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    // busy-exempt: opens the articles of the tag tapped, nothing is sent and nothing waits
    private async void OnTagTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject bo) return;
        if (bo.BindingContext is not TagListItem item) return;
        await Shell.Current.GoToAsync($"tagArticles?tagName={Uri.EscapeDataString(item.Name)}");
    }

    // The row's ⋮ opens the actions sheet; the rename / delete work runs behind BusyAction below.
    private async void OnTagOptionsClicked(object? sender, EventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not TagListItem item) return;

        var action = await ShowTagActionsAsync(item.Name);
        if (action == "rename")
            await RenameTagAsync(item.Name);
        else if (action == "delete")
            await DeleteTagAsync(item.Name);
    }

    /// <summary>The mock-up's row actions sheet (like ItemActionsSheet): rename, delete, and a quiet cancel.</summary>
    private async Task<string> ShowTagActionsAsync(string tagName)
    {
        var chosen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answered = false;
        var resources = Application.Current?.Resources;
        var stack = new VerticalStackLayout { Spacing = 8 };
        BottomSheet.BottomSheetPage? sheet = null;

        void Add(string key, string styleKey, string action)
        {
            var button = new Button { Text = AppStrings.Get(key), Style = (Style?)resources?[styleKey] };
            button.Clicked += async (_, _) =>
            {
                // Close the sheet FIRST, then answer: rename/delete push another modal right after.
                answered = true;
                if (sheet != null) await sheet.CloseAsync();
                chosen.TrySetResult(action);
            };
            stack.Children.Add(button);
        }

        Add("Rename", "SecondaryButton", "rename");
        Add("Delete", "DangerButton", "delete");
        var cancel = new Button { Text = AppStrings.Get("Cancel"), Style = (Style?)resources?["TextButton"], HorizontalOptions = LayoutOptions.Fill };
        cancel.Clicked += async (_, _) =>
        {
            answered = true;
            if (sheet != null) await sheet.CloseAsync();
            chosen.TrySetResult("none");
        };
        stack.Children.Add(cancel);

        sheet = new BottomSheet.BottomSheetPage(stack, tagName);
        sheet.Disappearing += (_, _) => { if (!answered) chosen.TrySetResult("none"); };
        await Navigation.PushModalAsync(sheet);
        return await chosen.Task;
    }

    private async Task RenameTagAsync(string oldName)
    {
        // The same input popup the folder rename uses.
        var newName = await this.ShowInputPopupAsync(AppStrings.Get("RenameTitle"), AppStrings.Get("NewNameLabel"),
            accept: AppStrings.Get("Save"), cancel: AppStrings.Get("Cancel"), initialValue: oldName, maxLength: 100);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == oldName) return;
        newName = newName.Trim();

        // The one busy helper (BMB-311): a second tap cannot start a second rename; a failure is said in full.
        await BusyAction.RunAsync(null, async () =>
        {
            await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var conceptTagSvc = scope.ServiceProvider.GetRequiredService<ConceptTagService>();
                await conceptTagSvc.RenameAsync(oldName, newName);
            });
            await LoadTagsAsync();
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }

    private async Task DeleteTagAsync(string tagName)
    {
        bool confirmed = await AppDialog.ConfirmAsync(this, AppStrings.Get("DeleteTagTitle"),
            AppStrings.Format("DeleteTagDescFormat", tagName),
            AppStrings.Get("Delete"), AppStrings.Get("Cancel"), danger: true);
        if (!confirmed) return;

        await BusyAction.RunAsync(null, async () =>
        {
            await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var conceptTagSvc = scope.ServiceProvider.GetRequiredService<ConceptTagService>();
                await conceptTagSvc.DeleteAsync(tagName);
            });
            await LoadTagsAsync();
        }, new BusyOptions { FailureTitle = AppStrings.Get("ErrorTitle") });
    }
}
