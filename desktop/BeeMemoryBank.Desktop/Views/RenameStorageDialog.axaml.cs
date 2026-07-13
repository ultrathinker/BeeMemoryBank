using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BeeMemoryBank.Desktop.Views;

/// <summary>
/// Tiny rename dialog: single text field, returns the trimmed name (or null if cancelled).
/// Uses <see cref="Services.StorageInputValidator.ValidateRename"/> so empty / overlong
/// names are caught inline before the caller ever touches ProfileService.
/// </summary>
public partial class RenameStorageDialog : Window
{
    public RenameStorageDialog() : this(string.Empty)
    {
        // Designer fallback.
    }

    public RenameStorageDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName ?? string.Empty;
        NameBox.AttachedToVisualTree += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectionStart = 0;
            NameBox.SelectionEnd = NameBox.Text?.Length ?? 0;
        };
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(dialogResult: null);
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var validation = Services.StorageInputValidator.ValidateRename(NameBox.Text);
        if (!validation.IsValid)
        {
            ErrorText.Text = validation.Error!;
            ErrorText.IsVisible = true;
            return;
        }
        Close(dialogResult: validation.Name);
    }
}
