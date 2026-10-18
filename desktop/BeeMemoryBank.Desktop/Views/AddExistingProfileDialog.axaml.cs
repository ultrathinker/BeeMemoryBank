using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Profiles;

namespace BeeMemoryBank.Desktop.Views;

/// <summary>
/// Registers a folder that already holds a vault as a profile. Nothing on disk changes: the
/// profile simply points at the folder, and the node opens it on the next switch.
/// </summary>
public partial class AddExistingProfileDialog : Window
{
    /// <summary>The profile that was added, or null if the user cancelled.</summary>
    public ProfileEntry? AddedProfile { get; private set; }

    private readonly ProfileService _profiles;

    public AddExistingProfileDialog()
    {
        // Designer fallback, see CreateStorageDialog.
        _profiles = null!;
        InitializeComponent();
    }

    public AddExistingProfileDialog(ProfileService profiles)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        InitializeComponent();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(dialogResult: false);
    }

    // busy-exempt: opens the system folder picker, nothing is sent and nothing waits on the node
    private async void OnBrowseClick(object? sender, RoutedEventArgs e)
    {
        var path = await FolderPicker.PickAsync(this, "Choose the folder of an existing profile");
        if (path == null) return;

        FolderBox.Text = path;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            NameBox.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        }
    }

    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        await AddAsync();
    }

    private async Task AddAsync()
    {
        HideError();

        var validation = Services.StorageInputValidator.ValidateAddExisting(NameBox.Text, FolderBox.Text);
        if (!validation.IsValid)
        {
            ShowError(validation.Error!);
            return;
        }

        var folder = validation.ExplicitDataPath!;
        switch (VaultFiles.Inspect(folder))
        {
            case VaultFolderState.Missing:
                ShowError("This folder does not exist.");
                return;
            case VaultFolderState.Empty:
            case VaultFolderState.Other:
                ShowError($"No Bee Memory Bank profile was found in this folder (it has no {VaultFiles.DatabaseFileName}).");
                return;
        }

        CancelButton.IsEnabled = false;
        try
        {
            // The one busy helper (BMB-311) disables the button, shows its loader and puts a failure in the error line below it.
            await BusyAction.RunAsync(this, AddButton, async () =>
            {
                try
                {
                    AddedProfile = await Task.Run(() => _profiles.AddProfile(validation.Name!, folder));
                    Close(dialogResult: true);
                }
                catch (Exception ex) when (ex is not ArgumentException)
                {
                    throw new InvalidOperationException($"Could not add the profile: {ex.Message}", ex);
                }
            }, new BusyOptions { Result = (ok, text) => { if (!ok) ShowError(text); } });
        }
        finally
        {
            CancelButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void HideError()
    {
        ErrorText.Text = string.Empty;
        ErrorText.IsVisible = false;
    }
}
