using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BeeMemoryBank.Desktop.Views;

/// <summary>
/// Generic two-button confirmation. Returns <c>true</c> when the user presses the confirm
/// button and <c>false</c> on cancel or when the window is closed.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog() : this("Bee Memory Bank", string.Empty, "OK", "Cancel")
    {
        // Designer fallback.
    }

    public ConfirmDialog(string heading, string body, string confirmText, string cancelText)
    {
        InitializeComponent();
        TitleText.Text = heading;
        BodyText.Text = body;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(dialogResult: false);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        Close(dialogResult: true);
    }
}
