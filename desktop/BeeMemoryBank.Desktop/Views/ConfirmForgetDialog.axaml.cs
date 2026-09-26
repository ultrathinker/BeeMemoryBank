using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BeeMemoryBank.Desktop.Views;

/// <summary>
/// Yes/No confirmation for the "Forget" action. Returns <c>true</c> (confirmed) or
/// <c>false</c> (cancelled). Body text is the brief's literal: explicitly tells the user
/// the data stays on disk and shows the path — matches ProfileService.ForgetProfile's
/// contract (removes the pointer only).
/// </summary>
public partial class ConfirmForgetDialog : Window
{
    public ConfirmForgetDialog() : this(string.Empty, string.Empty)
    {
        // Designer fallback.
    }

    public ConfirmForgetDialog(string profileName, string dataPath)
    {
        InitializeComponent();
        BodyText.Text =
            $"Profile “{profileName}” will be removed from the list.\n\n" +
            $"Its data STAYS on disk in:\n{dataPath}\n\n" +
            "You can bring it back later with Profiles → Add existing profile.";
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
