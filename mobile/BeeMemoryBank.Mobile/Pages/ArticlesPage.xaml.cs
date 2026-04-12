using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

public record ArticleListItem(Guid Id, string Title, string TreePath, string TagsDisplay, DateTime UpdatedAt);

public partial class ArticlesPage : ContentPage
{
    private readonly IServiceProvider _services;
    private List<ArticleListItem> _allArticles = new();
    private CancellationTokenSource? _searchCts;

    public ArticlesPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadArticlesAsync();
        _services.GetRequiredService<Services.SyncNotificationService>().ClearPendingUpdates();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _services.GetRequiredService<Services.SyncStatusService>().CancelSync();
    }

    private async Task LoadArticlesAsync(string? query = null)
    {
        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;
        var contentSearch = ContentSearchSwitch.IsToggled;
        try
        {
            var items = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IArticleRepository>();
                List<BeeMemoryBank.Core.Models.Article> articles;
                if (contentSearch && !string.IsNullOrWhiteSpace(query))
                {
                    var searchSvc = scope.ServiceProvider.GetRequiredService<SearchService>();
                    var results = await searchSvc.SearchWithContentAsync(query);
                    articles = results.Articles;
                }
                else
                {
                    articles = string.IsNullOrWhiteSpace(query)
                        ? await repo.ListAsync()
                        : await repo.SearchAsync(query);
                }
                return articles
                    .OrderByDescending(a => a.UpdatedAt)
                    .Select(a => new ArticleListItem(
                        a.Id, a.Title, a.TreePath,
                        a.Tags.Count > 0 ? string.Join(", ", a.Tags) : "",
                        a.UpdatedAt))
                    .ToList();
            });
            _allArticles = items;
            ArticlesList.ItemsSource = _allArticles;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadArticles error: {ex.Message}");
        }
        finally
        {
            RefreshView.IsRefreshing = false;
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
        }
    }

    private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchCts?.Cancel();

        if (ContentSearchSwitch.IsToggled && !string.IsNullOrWhiteSpace(e.NewTextValue))
        {
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;
            try
            {
                await Task.Delay(1000, token);
                if (!token.IsCancellationRequested)
                    await LoadArticlesAsync(e.NewTextValue);
            }
            catch (TaskCanceledException) { }
        }
        else
        {
            await LoadArticlesAsync(e.NewTextValue);
        }
    }

    private async void OnContentSearchToggled(object? sender, ToggledEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SearchBar.Text))
            await LoadArticlesAsync(SearchBar.Text);
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        var syncNotify = _services.GetRequiredService<Services.SyncNotificationService>();
        if (!syncNotify.HasPendingUpdates)
        {
            var syncStatus = _services.GetRequiredService<Services.SyncStatusService>();
            await syncStatus.SyncNowAsync();
        }
        
        await LoadArticlesAsync(SearchBar.Text);
        syncNotify.ClearPendingUpdates();
    }

    private async void OnArticleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject bo) return;
        if (bo.BindingContext is not ArticleListItem item) return;
        await Shell.Current.GoToAsync($"articleDetail?id={item.Id}");
    }

    private async void OnNewArticleClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("articleEdit");
    }
}
