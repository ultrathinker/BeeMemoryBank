using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Vault.Sealed;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>
/// "Encrypt this iPhone's database" (docs/design/step-b-spec.md 10.8, 12.2): the same service the servers run, in process, from this page with a
/// progress bar that keeps the screen on. Reached from Settings only while <see cref="PhoneVaultLifecycle.PhoneEncryptionAvailable"/> is true.
/// </summary>
public partial class EncryptDatabasePage : ContentPage
{
    private readonly SealMigrationService _seal;
    private readonly PhoneVaultLifecycle _lifecycle;

    public EncryptDatabasePage(SealMigrationService seal, PhoneVaultLifecycle lifecycle)
    {
        InitializeComponent();
        _seal = seal;
        _lifecycle = lifecycle;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ShowStatus();
    }

    private void ShowStatus()
    {
        var encrypted = _lifecycle.IsSealed;
        Form.IsVisible = !encrypted;
        StatusLabel.Text = encrypted
            ? "This iPhone's database is encrypted. It closes whenever the app goes to the background, and the master password (or Face ID) opens it."
            : "Encrypt the notes on this iPhone so that a copy of its storage shows nothing. The way back is to remove the vault here and join again from another device.";
    }

    private async void OnEncrypt(object? sender, EventArgs e)
    {
        ErrorLabel.IsVisible = false;
        var options = new SealOptions(AcceptNoSnapshot: OnlyCopyCheck.IsChecked);
        try
        {
            EncryptButton.IsEnabled = false;
            var preflight = await _seal.PreflightAsync(options);
            // The tick is only offered when it is the blocker in the way (spec 10.8 phone_no_copy).
            OnlyCopyRow.IsVisible = preflight.Blockers.Any(b => b.Code == SealBlockers.PhoneNoCopy) || OnlyCopyCheck.IsChecked;
            if (!preflight.CanStart)
            {
                ShowError(preflight.Blockers[0].Message);
                return;
            }

            // The screen stays on for as long as the database is rewritten (spec 10.8).
            DeviceDisplay.Current.KeepScreenOn = true;
            await SecretFields.RunAsync(() => _seal.StartAsync(PasswordEntry.Text ?? "", options), () => PasswordEntry.Text = "");
            Progress.IsVisible = true;
            StepLabel.IsVisible = true;
            SealProgress? progress;
            do
            {
                await Task.Delay(500);
                progress = _seal.Progress;
                Progress.Progress = (progress?.Percent ?? 0) / 100.0;
                StepLabel.Text = progress?.Step ?? "";
            }
            while (progress is null || progress.Phase is SealProgress.Idle or SealProgress.Running);

            if (progress.Phase == SealProgress.Failed) ShowError(progress.Error ?? "The encryption failed; the iPhone is unchanged.");
            else ShowStatus();
        }
        catch (UnauthorizedAccessException)
        {
            ShowError("The master password is not correct.");
        }
        catch (SealMigrationException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            DeviceDisplay.Current.KeepScreenOn = false;
            EncryptButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.IsVisible = true;
    }
}
