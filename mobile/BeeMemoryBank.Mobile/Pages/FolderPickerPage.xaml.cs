using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Mobile.Controls;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BeeMemoryBank.Mobile.Pages;

public partial class FolderPickerPage : ContentPage
{
    public static Action<string>? FolderSelected;

    private readonly IServiceProvider _services;
    private List<FolderNode> _allNodes = new();
    private ObservableCollection<FolderNode> _visibleNodes = new();
    private string? _selectedPath;

    private class FolderNode : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public int Depth { get; set; }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(Arrow)); }
        }

        private bool _isVisible = true;
        public bool IsVisible
        {
            get => _isVisible;
            set { _isVisible = value; OnPropertyChanged(); }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public Thickness DepthMargin => new(Depth * 20, 2, 0, 2);
        public string Arrow => IsExpanded ? "▼" : "▶";
        public string Icon => "📁";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public FolderPickerPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        FolderList.ItemsSource = _visibleNodes;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadFoldersAsync();
    }

    private async Task LoadFoldersAsync()
    {
        try
        {
            var nodes = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IArticleRepository>();
                var articles = await repo.ListAsync();
                return BuildFolderTree(articles);
            });
            _allNodes = nodes;
            RebuildVisibleList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadFolders error: {ex.Message}");
        }
    }

    /// <summary>
    /// Depth-first ordered flat list of folders only.
    /// Root folders start visible+collapsed.
    /// </summary>
    private static List<FolderNode> BuildFolderTree(List<Article> articles)
    {
        var folderPaths = new HashSet<string>();
        foreach (var article in articles)
        {
            var current = article.TreePath?.Trim('/') ?? "";
            while (current.Length > 0)
            {
                folderPaths.Add("/" + current);
                var lastSlash = current.LastIndexOf('/');
                current = lastSlash > 0 ? current[..lastSlash] : "";
            }
        }

        var result = new List<FolderNode>();

        void AddFolder(string folderPath)
        {
            var depth = CountSegments(folderPath) - 1; // 0-based
            var isRoot = depth == 0;
            result.Add(new FolderNode
            {
                Name = GetLastSegment(folderPath),
                FullPath = folderPath,
                Depth = depth,
                IsExpanded = false,
                IsVisible = isRoot
            });

            foreach (var sub in folderPaths.Where(p => IsDirectChild(folderPath, p)).OrderBy(p => p))
                AddFolder(sub);
        }

        foreach (var root in folderPaths.Where(p => CountSegments(p) == 1).OrderBy(p => p))
            AddFolder(root);

        return result;
    }

    private void RebuildVisibleList(string? searchQuery = null)
    {
        _visibleNodes.Clear();

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            // In search mode: show all matching nodes regardless of expand state
            foreach (var node in _allNodes)
            {
                if (node.Name.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) ||
                    node.FullPath.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
                    _visibleNodes.Add(node);
            }
        }
        else
        {
            foreach (var node in _allNodes.Where(n => n.IsVisible))
                _visibleNodes.Add(node);
        }
    }

    private void OnFolderTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not BindableObject bo) return;
        if (bo.BindingContext is not FolderNode node) return;

        // Select this folder
        foreach (var n in _allNodes) n.IsSelected = false;
        node.IsSelected = true;
        _selectedPath = node.FullPath;
        SelectedPathLabel.Text = _selectedPath;
        SelectButton.IsEnabled = true;

        // Toggle expand/collapse
        node.IsExpanded = !node.IsExpanded;

        if (node.IsExpanded)
        {
            foreach (var child in _allNodes)
            {
                if (GetParentPath(child.FullPath) == node.FullPath)
                    child.IsVisible = true;
            }
        }
        else
        {
            foreach (var descendant in _allNodes)
            {
                if (descendant != node && IsDescendantOf(descendant.FullPath, node.FullPath))
                {
                    descendant.IsVisible = false;
                    descendant.IsExpanded = false;
                }
            }
        }

        RebuildVisibleList(SearchBar.Text);
    }

    private async void OnNewFolderClicked(object? sender, EventArgs e)
    {
        var name = await this.ShowInputPopupAsync("New Folder", "Folder name:",
            placeholder: "e.g. MyFolder");
        if (string.IsNullOrWhiteSpace(name)) return;

        name = name.Trim().Trim('/');
        var parentPath = _selectedPath ?? "/";
        var newPath = parentPath == "/" ? $"/{name}" : $"{parentPath}/{name}";

        // Don't add duplicates
        if (_allNodes.Any(n => n.FullPath == newPath)) return;

        var depth = CountSegments(newPath) - 1;
        var newNode = new FolderNode
        {
            Name = name,
            FullPath = newPath,
            Depth = depth,
            IsExpanded = false,
            IsVisible = true
        };

        // Insert in alphabetical order
        var insertIndex = _allNodes.FindIndex(n => string.Compare(n.FullPath, newPath, StringComparison.Ordinal) > 0);
        if (insertIndex < 0) insertIndex = _allNodes.Count;
        _allNodes.Insert(insertIndex, newNode);

        // Ensure parent is expanded
        EnsureParentExpanded(newPath);

        foreach (var n in _allNodes) n.IsSelected = false;
        newNode.IsSelected = true;
        _selectedPath = newPath;
        SelectedPathLabel.Text = _selectedPath;
        SelectButton.IsEnabled = true;

        RebuildVisibleList(SearchBar.Text);
    }

    private void EnsureParentExpanded(string childPath)
    {
        var parentPath = GetParentPath(childPath);
        if (parentPath == null) return;

        var parentNode = _allNodes.FirstOrDefault(n => n.FullPath == parentPath);
        if (parentNode != null && !parentNode.IsExpanded)
        {
            parentNode.IsExpanded = true;
            foreach (var child in _allNodes)
            {
                if (GetParentPath(child.FullPath) == parentNode.FullPath)
                    child.IsVisible = true;
            }
            EnsureParentExpanded(parentPath);
        }
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        RebuildVisibleList(e.NewTextValue);
    }

    private async void OnSelectClicked(object? sender, EventArgs e)
    {
        if (_selectedPath != null)
        {
            FolderSelected?.Invoke(_selectedPath);
            await Shell.Current.GoToAsync("..");
        }
    }

    private static bool IsDescendantOf(string path, string ancestorPath)
    {
        var prefix = ancestorPath.TrimEnd('/') + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool IsDirectChild(string parentPath, string candidatePath)
    {
        if (candidatePath == parentPath) return false;
        var prefix = parentPath == "/" ? "/" : parentPath + "/";
        if (!candidatePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        return !candidatePath[prefix.Length..].Contains('/');
    }

    private static string GetLastSegment(string path)
    {
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        return lastSlash < 0 ? trimmed : trimmed[(lastSlash + 1)..];
    }

    private static int CountSegments(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return 0;
        return path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static string? GetParentPath(string path)
    {
        if (path == "/") return null;
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash <= 0) return "/";
        return path[..lastSlash];
    }
}
