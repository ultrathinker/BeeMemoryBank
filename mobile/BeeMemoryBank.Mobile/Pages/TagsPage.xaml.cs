using System.Collections.ObjectModel;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

public record TagListItem(string Name, int Count);

public partial class TagsPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly ObservableCollection<TagListItem> _tagItems = new();
    private List<TagInfo> _allTags = new();

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
                var repo = scope.ServiceProvider.GetRequiredService<IArticleRepository>();
                return (await repo.GetAllTagsAsync())
                    .OrderByDescending(t => t.ArticleCount)
                    .ToList();
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
        var syncNotify = _services.GetRequiredService<Services.SyncNotificationService>();
        if (!syncNotify.HasPendingUpdates)
        {
            var syncStatus = _services.GetRequiredService<Services.SyncStatusService>();
            await syncStatus.SyncNowAsync();
        }
        
        await LoadTagsAsync();
        syncNotify.ClearPendingUpdates();
        RefreshView.IsRefreshing = false;
    }
    private void ApplyTags(List<TagInfo> tags)
    {
        _tagItems.Clear();
        foreach (var tag in tags)
            _tagItems.Add(new TagListItem(tag.Name, tag.ArticleCount));
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyTags(FilterTags(e.NewTextValue));
    }

    private List<TagInfo> FilterTags(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _allTags;

        return _allTags
            .Where(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async void OnTagTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject bo) return;
        if (bo.BindingContext is not TagListItem item) return;
        await Shell.Current.GoToAsync($"tagArticles?tagName={Uri.EscapeDataString(item.Name)}");
    }

    private async void OnTagOptionsClicked(object? sender, EventArgs e)
    {
        if (sender is not Button btn || btn.CommandParameter is not TagListItem item) return;

        var action = await DisplayActionSheet($"Tag: {item.Name}", "Cancel", null, "Rename", "Delete");
        if (action == "Rename")
            await RenameTagAsync(item.Name);
        else if (action == "Delete")
            await DeleteTagAsync(item.Name);
    }

    private async Task RenameTagAsync(string oldName)
    {
        var newName = await this.ShowInputPopupAsync("Rename Tag", "New tag name:", initialValue: oldName, maxLength: 100);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == oldName) return;
        newName = newName.Trim();

        try
        {
            await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IArticleRepository>();
                var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();
                var articles = (await repo.ListAsync()).Where(a => a.Tags.Contains(oldName)).ToList();
                foreach (var article in articles)
                {
                    var newTags = article.Tags.Select(t => t == oldName ? newName : t).ToList();
                    await articleSvc.UpdateAsync(article.Id, null, null, newTags, null);
                }
            });
            await LoadTagsAsync();
        }
        catch (Exception ex) { await DisplayAlert("Error", ex.Message, "OK"); }
    }

    private async Task DeleteTagAsync(string tagName)
    {
        bool confirmed = await DisplayAlert("Delete Tag", $"Remove tag \"{tagName}\" from all articles?", "Delete", "Cancel");
        if (!confirmed) return;

        try
        {
            await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IArticleRepository>();
                var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();
                var articles = (await repo.ListAsync()).Where(a => a.Tags.Contains(tagName)).ToList();
                foreach (var article in articles)
                {
                    var newTags = article.Tags.Where(t => t != tagName).ToList();
                    await articleSvc.UpdateAsync(article.Id, null, null, newTags, null);
                }
            });
            await LoadTagsAsync();
        }
        catch (Exception ex) { await DisplayAlert("Error", ex.Message, "OK"); }
    }
}
