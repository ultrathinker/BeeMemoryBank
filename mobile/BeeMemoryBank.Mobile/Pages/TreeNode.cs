using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BeeMemoryBank.Mobile.Pages;

public class TreeNode : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsFolder { get; set; }
    public Guid? ArticleId { get; set; }
    public int Depth { get; set; }

    private bool _isExpanded = true;
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

    public Thickness DepthMargin => new(Depth * 20, 2, 0, 2);

    public string Arrow => IsFolder ? (IsExpanded ? "▼" : "▶") : "";

    public string Icon => IsFolder ? "📁" : "📄";

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
