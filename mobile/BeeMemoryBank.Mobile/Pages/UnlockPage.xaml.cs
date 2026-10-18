using BeeMemoryBank.Busy;
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

    // The one busy helper (BMB-311): both unlock buttons are off and the tapped one spins until the vault is open or the reason is on the
    // screen; a failure is said in full in the error line.
    private async Task TryFingerprintUnlockAsync()
    {
        ErrorLabel.IsVisible = false;
        await BusyAction.RunAsync(FingerprintButton, async () =>
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
        }, new BusyOptions { ResultLabel = ErrorLabel, AlsoDisable = [UnlockButton] });
    }

    // ── Password unlock ───────────────────────────────────────────────────────

    private void OnTogglePasswordClicked(object? sender, EventArgs e)
    {
        PasswordEntry.IsPassword = !PasswordEntry.IsPassword;
        TogglePasswordButton.Text = PasswordEntry.IsPassword ? "Show" : "Hide";
    }

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
        // The one busy helper (BMB-311); the page's own loader stays for the key derivation, which takes a moment.
        await BusyAction.RunAsync(UnlockButton, async () =>
        {
            LoadingIndicator.IsRunning = true;
            LoadingIndicator.IsVisible = true;
            try
            {
                await Task.Yield(); // let loader render before Argon2id starts

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
            finally
            {
                LoadingIndicator.IsRunning = false;
                LoadingIndicator.IsVisible = false;
            }
        }, new BusyOptions { ResultLabel = ErrorLabel, AlsoDisable = [FingerprintButton] });
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
