using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Controls;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

[QueryProperty(nameof(FolderName), "folderName")]
[QueryProperty(nameof(FolderPath), "path")]
public partial class TreeFolderPage : ContentPage
{
    private readonly IServiceProvider _services;
    private string _currentPath = "/";

    public TreeFolderPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
    }

    public string FolderName
    {
        set { if (value != null) Title = Uri.UnescapeDataString(value); }
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

    private async Task LoadItemsAsync(string currentPath)
    {
        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;

        try
        {
            var items = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var folderRepo = scope.ServiceProvider.GetRequiredService<IFolderRepository>();
                var articleRepo = scope.ServiceProvider.GetRequiredService<IArticleRepository>();
                return await BuildTreeItems(folderRepo, articleRepo, currentPath);
            });

            FolderList.ItemsSource = items;
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

    private static async Task<List<TreeListItem>> BuildTreeItems(
        IFolderRepository folderRepo, IArticleRepository articleRepo, string currentPath)
    {
        var folders = await folderRepo.GetChildrenAsync(currentPath == "/" ? null : currentPath);
        var articles = await articleRepo.ListAsync(currentPath);
        var directArticles = articles
            .Where(a => NormalizePath(a.TreePath) == currentPath)
            .OrderBy(a => a.Title, BeeMemoryBank.Core.UnderscoreFirstComparer.Instance)
            .ToList();

        var result = new List<TreeListItem>();
        result.AddRange(folders.OrderBy(f => f.Name, BeeMemoryBank.Core.UnderscoreFirstComparer.Instance)
            .Select(f => new TreeListItem(f.Name, true, f.Path, null, null)));
        result.AddRange(directArticles
            .Select(a => new TreeListItem(a.Title, false, null, a.Id, a.UpdatedAt)));
        return result;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        return "/" + path.Trim('/');
    }

    private async void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject bo) return;
        if (bo.BindingContext is not TreeListItem item) return;

        if (item.IsFolder && item.FolderPath is not null)
        {
            await Shell.Current.GoToAsync(
                $"treeFolder?path={Uri.EscapeDataString(item.FolderPath)}&folderName={Uri.EscapeDataString(item.Name)}");
        }
        else if (!item.IsFolder && item.ArticleId.HasValue)
        {
            await Shell.Current.GoToAsync($"articleDetail?id={item.ArticleId.Value}");
        }
    }

    private async void OnNewArticleClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync($"articleEdit?path={Uri.EscapeDataString(_currentPath)}");
    }

    private async void OnItemLongPressed(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject bo) return;
        if (bo.BindingContext is not TreeListItem item) return;
        if (!item.IsFolder || item.FolderPath is null) return;

        var action = await DisplayActionSheet($"Folder: {item.Name}", "Cancel", null, "Rename", "Delete");
        if (action == "Rename")
            await RenameFolderAsync(item);
        else if (action == "Delete")
            await DeleteFolderAsync(item);
    }

    private async Task RenameFolderAsync(TreeListItem item)
    {
        var newName = await this.ShowInputPopupAsync("Rename Folder", "New folder name:",
            accept: "Rename", cancel: "Cancel", initialValue: item.Name, maxLength: 100);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == item.Name) return;

        try
        {
            using var scope = _services.CreateScope();
            var folderRepo = scope.ServiceProvider.GetRequiredService<IFolderRepository>();
            var folderSvc = scope.ServiceProvider.GetRequiredService<FolderService>();
            var folder = await folderRepo.GetByPathAsync(item.FolderPath!);
            if (folder == null) return;
            await folderSvc.RenameAsync(folder.Id, newName.Trim());
            await LoadItemsAsync(_currentPath);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    private async Task DeleteFolderAsync(TreeListItem item)
    {
        bool confirmed = await DisplayAlert("Delete Folder",
            $"Delete folder \"{item.Name}\" and all its contents?",
            "Delete", "Cancel");
        if (!confirmed) return;

        try
        {
            using var scope = _services.CreateScope();
            var folderRepo = scope.ServiceProvider.GetRequiredService<IFolderRepository>();
            var folderSvc = scope.ServiceProvider.GetRequiredService<FolderService>();
            var folder = await folderRepo.GetByPathAsync(item.FolderPath!);
            if (folder == null) return;
            await folderSvc.DeleteAsync(folder.Id);
            await LoadItemsAsync(_currentPath);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    private async void OnNewFolderClicked(object? sender, EventArgs e)
    {
        var name = await this.ShowInputPopupAsync("New Folder", "Folder name:",
            accept: "Create", cancel: "Cancel", maxLength: 100);
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            using var scope = _services.CreateScope();
            var folderSvc = scope.ServiceProvider.GetRequiredService<FolderService>();
            var newPath = _currentPath.TrimEnd('/') + "/" + name.Trim().Trim('/');
            await folderSvc.CreateAsync(newPath);
            await LoadItemsAsync(_currentPath);
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }
}
