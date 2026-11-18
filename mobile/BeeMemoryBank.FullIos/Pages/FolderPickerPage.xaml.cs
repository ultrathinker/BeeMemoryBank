using BeeMemoryBank.Busy;
using BeeMemoryBank.FullIos.Controls;
using BeeMemoryBank.FullIos.Localization;
using BeeMemoryBank.FullIos.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>
/// The folder picker (the Android FolderPickerPage's behaviour, the iPhone design): search over
/// the whole tree, new folder, expand/collapse, one selected destination, Select hands the path
/// back. For Move the folder itself and its descendants are not destinations, and a new folder
/// is created at once (the move needs a real folder); for the editor a new folder is only added
/// to this list, the core creates it when the note is saved, as on Android.
/// </summary>
public partial class FolderPickerPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly NotesService _notes;
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private List<FolderNode> _allNodes = [];
    private readonly ObservableCollection<FolderNode> _visibleNodes = [];
    private string? _selectedPath;
    private string? _excludePath;
    private bool _createFolders;
    private bool _answered;

    /// <summary>The chosen path, or null when the person went back.</summary>
    public Task<string?> ResultTask => _result.Task;

    private sealed class FolderNode : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public int Depth { get; set; }
        public bool IsRoot { get; set; }

        private bool _isExpanded;
        public bool IsExpanded { get => _isExpanded; set { _isExpanded = value; OnPropertyChanged(); } }

        private bool _isVisible = true;
        public bool IsVisible { get => _isVisible; set { _isVisible = value; OnPropertyChanged(); } }

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }

        public Thickness DepthMargin => new(Depth * 20, 4, 0, 4);

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public FolderPickerPage(IServiceProvider services, NotesService notes)
    {
        InitializeComponent();
        _services = services;
        _notes = notes;
        Title = AppStrings.Get("ChooseFolder");
        HeadingLabel.Text = Title;
        FolderList.ItemsSource = _visibleNodes;
        SelectedPathLabel.Text = AppStrings.Format("SelectedFolderFormat", ContentFormat.DisplayPath("/"));
    }

    /// <summary>Picker for Move: the folder and its descendants are not destinations; new folders are created at once.</summary>
    public FolderPickerPage ForMove(string? excludePath)
    {
        _excludePath = excludePath is null ? null : NotesService.NormalizePath(excludePath);
        _createFolders = true;
        return this;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadFoldersAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (!_answered) _result.TrySetResult(null);
    }

    private async Task LoadFoldersAsync()
    {
        try
        {
            var paths = await Task.Run(() => _notes.FolderPathsAsync());
            if (_excludePath is not null)
            {
                var prefix = _excludePath.TrimEnd('/') + "/";
                paths = paths.Where(p => p != _excludePath && !p.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            }
            _allNodes = BuildFolderTree(paths);
            // The root is a real destination (deleting a folder sends its articles there), so it
            // is a selectable row of its own. Moving a folder that already sits at the root has
            // no use for it, so that one case hides it.
            if (_excludePath is null || GetParentPath(_excludePath) != "/")
            {
                _allNodes.Insert(0, new FolderNode
                {
                    Name = AppStrings.Get("TreeRoot"),
                    FullPath = "/",
                    Depth = 0,
                    IsRoot = true,
                    IsExpanded = true,
                    IsVisible = true,
                });
            }
            RebuildVisibleList(SearchEntry.Text);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Folder picker load failed: " + ex);
            await AppDialog.AlertAsync(this, AppStrings.Get("ErrorTitle"), AppStrings.Get("ReadListError"));
        }
    }

    /// <summary>Depth-first flat list of folders; roots start visible and collapsed.</summary>
    private static List<FolderNode> BuildFolderTree(List<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.Ordinal);
        var result = new List<FolderNode>();

        void AddFolder(string folderPath)
        {
            var depth = CountSegments(folderPath) - 1;
            result.Add(new FolderNode
            {
                Name = GetLastSegment(folderPath),
                FullPath = folderPath,
                Depth = depth,
                IsExpanded = false,
                IsVisible = depth == 0,
            });
            foreach (var sub in set.Where(p => IsDirectChild(folderPath, p)).OrderBy(p => p, StringComparer.Ordinal))
                AddFolder(sub);
        }

        foreach (var root in set.Where(p => CountSegments(p) == 1).OrderBy(p => p, StringComparer.Ordinal))
            AddFolder(root);
        return result;
    }

    private void RebuildVisibleList(string? searchQuery)
    {
        _visibleNodes.Clear();
        // The root stays the first row in both modes: it is always a valid destination.
        var root = _allNodes.FirstOrDefault(n => n.IsRoot);
        if (root is not null) _visibleNodes.Add(root);
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            foreach (var node in _allNodes)
                if (!node.IsRoot &&
                    (node.Name.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) ||
                     node.FullPath.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)))
                    _visibleNodes.Add(node);
        }
        else
        {
            foreach (var node in _allNodes.Where(n => !n.IsRoot && n.IsVisible))
                _visibleNodes.Add(node);
        }
    }

    private void OnFolderTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is not FolderNode node) return;

        SetSelected(node);

        // The root has no children of its own to fold: the top-level folders are always shown.
        if (node.IsRoot) return;

        node.IsExpanded = !node.IsExpanded;
        if (node.IsExpanded)
        {
            foreach (var child in _allNodes)
                if (GetParentPath(child.FullPath) == node.FullPath)
                    child.IsVisible = true;
        }
        else
        {
            foreach (var descendant in _allNodes)
                if (descendant != node && IsDescendantOf(descendant.FullPath, node.FullPath))
                {
                    descendant.IsVisible = false;
                    descendant.IsExpanded = false;
                }
        }
        RebuildVisibleList(SearchEntry.Text);
    }

    /// <summary>Marks the one selected row and says its path the way a person reads it (the root by name).</summary>
    private void SetSelected(FolderNode node)
    {
        foreach (var n in _allNodes) n.IsSelected = false;
        node.IsSelected = true;
        _selectedPath = node.FullPath;
        SelectedPathLabel.Text = AppStrings.Format("SelectedFolderFormat", ContentFormat.DisplayPath(_selectedPath));
        SelectButton.IsEnabled = true;
    }

    private async void OnNewFolderClicked(object? sender, EventArgs e)
    {
        var name = await this.ShowInputPopupAsync(AppStrings.Get("NewFolder"), AppStrings.Get("FolderNameLabel"),
            AppStrings.Get("Create"), AppStrings.Get("Cancel"), AppStrings.Get("NewFolderExample"), maxLength: 100);
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim().Trim('/');
        var parentPath = _selectedPath ?? "/";
        var newPath = parentPath == "/" ? $"/{name}" : $"{parentPath}/{name}";
        if (_allNodes.Any(n => n.FullPath == newPath)) return;

        if (_createFolders)
        {
            // A move destination must exist: create it through the core, then reload the tree.
            await BusyAction.RunAsync(sender as Button, async () =>
            {
                await _notes.CreateFolderAsync(parentPath, name);
                await LoadFoldersAsync();
            }, new BusyOptions { FailureTitle = AppStrings.Get("NewFolder") });
        }
        else
        {
            var depth = CountSegments(newPath) - 1;
            var newNode = new FolderNode { Name = name, FullPath = newPath, Depth = depth, IsExpanded = false, IsVisible = true };
            var insertIndex = _allNodes.FindIndex(n => string.Compare(n.FullPath, newPath, StringComparison.Ordinal) > 0);
            if (insertIndex < 0) insertIndex = _allNodes.Count;
            _allNodes.Insert(insertIndex, newNode);
            EnsureParentExpanded(newPath);
        }

        var selected = _allNodes.FirstOrDefault(n => n.FullPath == newPath);
        if (selected is not null) SetSelected(selected);
        RebuildVisibleList(SearchEntry.Text);
    }

    private void EnsureParentExpanded(string childPath)
    {
        var parentPath = GetParentPath(childPath);
        if (parentPath is null) return;
        var parentNode = _allNodes.FirstOrDefault(n => n.FullPath == parentPath);
        if (parentNode is not null && !parentNode.IsExpanded)
        {
            parentNode.IsExpanded = true;
            foreach (var child in _allNodes)
                if (GetParentPath(child.FullPath) == parentNode.FullPath)
                    child.IsVisible = true;
            EnsureParentExpanded(parentPath);
        }
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => RebuildVisibleList(e.NewTextValue);

    // busy-exempt: returns the picked folder to the caller; nothing is sent and nothing waits
    private async void OnSelectClicked(object? sender, EventArgs e)
    {
        if (_selectedPath is null) return;
        _answered = true;
        var path = _selectedPath;
        await Navigation.PopAsync();
        _result.TrySetResult(path);
    }

    private static bool IsDescendantOf(string path, string ancestorPath)
        => path.StartsWith(ancestorPath.TrimEnd('/') + "/", StringComparison.Ordinal);

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
