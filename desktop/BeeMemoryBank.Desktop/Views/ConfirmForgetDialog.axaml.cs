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
            $"Хранилище «{profileName}» будет убрано из списка.\n\n" +
            $"Данные ОСТАЮТСЯ на диске по пути:\n{dataPath}";
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
