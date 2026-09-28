using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BeeMemoryBank.Desktop.Views;

/// <summary>
/// Asks for the owner's password before a re-key, with what the re-key changes spelled out. Returns the password,
/// or null if cancelled. The verb itself checks the password; this only refuses an empty one.
/// </summary>
public partial class RekeyPasswordDialog : Window
{
    public RekeyPasswordDialog() : this(string.Empty)
    {
        // Designer fallback.
    }

    public RekeyPasswordDialog(string profileName)
    {
        InitializeComponent();
        HeadingText.Text = $"Re-key profile “{profileName}”?";
        PasswordBox.AttachedToVisualTree += (_, _) => PasswordBox.Focus();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        PasswordBox.Text = string.Empty;
        Close(dialogResult: null);
    }

    private void OnRekeyClick(object? sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Text;
        if (string.IsNullOrEmpty(password))
        {
            ErrorText.Text = "Enter your password.";
            ErrorText.IsVisible = true;
            return;
        }
        PasswordBox.Text = string.Empty;
        Close(dialogResult: password);
    }
}
