using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Services;

namespace BeeMemoryBank.Mobile.Pages;

public partial class UnlockPage : ContentPage
{
    private readonly SessionService _session;
    private readonly IBiometricService _biometric;
    private readonly PostUnlockRouter _router;

    public UnlockPage(SessionService session, IBiometricService biometric, PostUnlockRouter router)
    {
        InitializeComponent();
        _session = session;
        _biometric = biometric;
        _router = router;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = InitBiometricButtonAsync();
    }

    private async Task InitBiometricButtonAsync()
    {
        var available = await _biometric.IsAvailableAsync();
        var hasStored = _biometric.HasStoredPassword();

        FingerprintButton.IsVisible = available && hasStored;
        OrLabel.IsVisible = available && hasStored;

        // If fingerprint is available, trigger it automatically on first appearance
        if (available && hasStored)
            _ = TryFingerprintUnlockAsync();
    }

    // ── Fingerprint unlock ────────────────────────────────────────────────────

    private async void OnFingerprintClicked(object? sender, EventArgs e)
        => await TryFingerprintUnlockAsync();

    private async Task TryFingerprintUnlockAsync()
    {
        ErrorLabel.IsVisible = false;
        LoadingIndicator.IsRunning = true;
        LoadingIndicator.IsVisible = true;
        FingerprintButton.IsEnabled = false;
        UnlockButton.IsEnabled = false;

        try
        {
            var password = await _biometric.GetPasswordAsync();
            if (password == null)
            {
                // User cancelled — just show password field, no error message
                return;
            }

            var ok = await Task.Run(() => _session.UnlockAsync(password));
            if (ok)
            {
                await _router.RouteAsync();
            }
            else
            {
                // Stored password no longer matches (user changed it?) — clear biometric
                _biometric.Clear();
                FingerprintButton.IsVisible = false;
                OrLabel.IsVisible = false;
                ErrorLabel.Text = "Fingerprint data outdated. Please enter password.";
                ErrorLabel.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = $"Error: {ex.Message}";
            ErrorLabel.IsVisible = true;
        }
        finally
        {
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
            FingerprintButton.IsEnabled = true;
            UnlockButton.IsEnabled = true;
        }
    }

    // ── Password unlock ───────────────────────────────────────────────────────

    private async void OnUnlockClicked(object? sender, EventArgs e)
    {
        var password = PasswordEntry.Text ?? "";
        if (string.IsNullOrEmpty(password))
        {
            ErrorLabel.Text = "Enter your password.";
            ErrorLabel.IsVisible = true;
            return;
        }

        ErrorLabel.IsVisible = false;
        UnlockButton.IsEnabled = false;
        FingerprintButton.IsEnabled = false;
        LoadingIndicator.IsRunning = true;
        LoadingIndicator.IsVisible = true;

        await Task.Yield(); // let loader render before Argon2id starts

        try
        {
            var ok = await Task.Run(() => _session.UnlockAsync(password));
            if (!ok)
            {
                ErrorLabel.Text = "Wrong password.";
                ErrorLabel.IsVisible = true;
                PasswordEntry.Text = "";
                return;
            }

            // Password correct — offer to enable fingerprint if not yet set up
            await OfferFingerprintEnrollmentAsync(password);

            await _router.RouteAsync();
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = $"Error: {ex.Message}";
            ErrorLabel.IsVisible = true;
        }
        finally
        {
            UnlockButton.IsEnabled = true;
            FingerprintButton.IsEnabled = true;
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
        }
    }

    private async Task OfferFingerprintEnrollmentAsync(string password)
    {
        var available = await _biometric.IsAvailableAsync();
        if (!available || _biometric.HasStoredPassword()) return;

        var yes = await DisplayAlert(
            "Enable Fingerprint",
            "Use fingerprint to unlock next time instead of typing your password?",
            "Enable", "Skip");

        if (!yes) return;

        var stored = await _biometric.StorePasswordAsync(password);
        if (!stored)
        {
            await DisplayAlert("Fingerprint", "Could not save fingerprint. You can try again later.", "OK");
        }
    }
}
