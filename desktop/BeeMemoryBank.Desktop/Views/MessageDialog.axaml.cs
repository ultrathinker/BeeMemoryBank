using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BeeMemoryBank.Desktop.Views;

/// <summary>
/// Generic OK-only message dialog used for surfacing ProfileService exceptions from the
/// manage window (e.g. "Cannot forget the only remaining profile"). Kept deliberately
/// small — the brief allows an inline message where possible, but a per-row error needs
/// somewhere to live without taking row space.
/// </summary>
public partial class MessageDialog : Window
{
    public MessageDialog() : this(string.Empty, string.Empty)
    {
        // Designer fallback.
    }

    public MessageDialog(string title, string body)
    {
        InitializeComponent();
        TitleText.Text = string.IsNullOrEmpty(title) ? "BeeMemoryBank" : title;
        BodyText.Text = body ?? string.Empty;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
