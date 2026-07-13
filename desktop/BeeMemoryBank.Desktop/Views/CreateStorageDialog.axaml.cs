using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BeeMemoryBank.Profiles;

namespace BeeMemoryBank.Desktop.Views;

/// <summary>
/// Native dialog for creating a new storage profile. Collects a name (required) and an
/// optional advanced data-path; on "Create" it calls <see cref="ProfileService.AddProfile"/>
/// and returns the resulting <see cref="ProfileEntry"/> via <see cref="CreatedProfile"/>.
///
/// Validation is split: <see cref="Services.StorageInputValidator.ValidateCreate"/> catches
/// empty/oversized names and non-absolute paths for instant inline feedback (no exception
/// trip), while deeper invariants (duplicate dataPath, registry write errors) are surfaced
/// by catching whatever <see cref="ProfileService.AddProfile"/> throws and showing it in the
/// same inline red TextBlock — no separate MessageBox.
/// </summary>
public partial class CreateStorageDialog : Window
{
    /// <summary>The profile that was created, or null if the user cancelled / creation failed.</summary>
    public ProfileEntry? CreatedProfile { get; private set; }

    private readonly ProfileService _profiles;

    public CreateStorageDialog()
    {
        // Designer / XAML loader fallback only. The real caller always passes a ProfileService
        // via the other constructor; if this one is ever used at runtime, _profiles is null!
        // and any handler that touches it will NullReferenceException — acceptable for the
        // previewer, never reached in production.
        _profiles = null!;
        InitializeComponent();
    }

    public CreateStorageDialog(ProfileService profiles)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        InitializeComponent();
        NameBox.AttachedToVisualTree += (_, _) => NameBox.Focus();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        CreatedProfile = null;
        Close(dialogResult: false);
    }

    private async void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        await CreateAsync();
    }

    private async Task CreateAsync()
    {
        HideError();

        var validation = Services.StorageInputValidator.ValidateCreate(
            NameBox.Text,
            AdvancedExpander.IsExpanded ? DataPathBox.Text : null);

        if (!validation.IsValid)
        {
            ShowError(validation.Error!);
            return;
        }

        CreateButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        try
        {
            // AddProfile is synchronous (atomic file write under a lock) but wrapped in a
            // Task.Run so any disk IO does not freeze the dialog and any thrown exception
            // (duplicate dataPath, IO error) is surfaced inline rather than via the
            // unhandled-exception handler.
            var created = await Task.Run(() =>
                _profiles.AddProfile(validation.Name!, validation.ExplicitDataPath));

            CreatedProfile = created;
            Close(dialogResult: true);
        }
        catch (ArgumentException aex)
        {
            ShowError(aex.Message);
        }
        catch (InvalidOperationException ioex)
        {
            ShowError(ioex.Message);
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось создать хранилище: {ex.Message}");
        }
        finally
        {
            CreateButton.IsEnabled = true;
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
