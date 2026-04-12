using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

public record TagArticleItem(Guid Id, string Title, string TreePath, DateTime UpdatedAt);

[QueryProperty(nameof(TagName), "tagName")]
public partial class TagArticlesPage : ContentPage
{
    private readonly IServiceProvider _services;

    private string _tagName = "";
    public string TagName
    {
        set
        {
            _tagName = Uri.UnescapeDataString(value ?? "");
            Title = _tagName;
            _ = LoadArticlesAsync(_tagName);
        }
    }

    public TagArticlesPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
    }

    private async Task LoadArticlesAsync(string tagName)
    {
        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;

        try
        {
            var items = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IArticleRepository>();
                var articles = await repo.ListAsync();
                return articles
                    .Where(a => a.Tags.Contains(tagName, StringComparer.OrdinalIgnoreCase))
                    .OrderByDescending(a => a.UpdatedAt)
                    .Select(a => new TagArticleItem(a.Id, a.Title, a.TreePath, a.UpdatedAt))
                    .ToList();
            });
            ArticlesList.ItemsSource = items;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadArticlesForTag error: {ex.Message}");
        }
        finally
        {
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
        }
    }

    private async void OnArticleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject bo) return;
        if (bo.BindingContext is not TagArticleItem item) return;
        await Shell.Current.GoToAsync($"articleDetail?id={item.Id}");
    }
}
