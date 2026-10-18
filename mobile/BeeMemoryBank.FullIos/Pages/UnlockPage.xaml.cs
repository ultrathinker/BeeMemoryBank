using BeeMemoryBank.Busy;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>The locked vault: the master password, or Face ID / Touch ID when it is turned on (asked at once when the page appears).</summary>
public partial class UnlockPage : ContentPage
{
    private readonly FullVault _vault;
    private readonly QuickUnlock _quick;
    private readonly AppFlow _flow;
    private bool _askedOnce;

    public UnlockPage(FullVault vault, QuickUnlock quick, AppFlow flow)
    {
        InitializeComponent();
        _vault = vault;
        _quick = quick;
        _flow = flow;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var identity = await _vault.IdentityAsync();
        NodeLabel.Text = identity is null ? "" : identity.DisplayName;
        QuickButton.IsVisible = _quick.IsEnabled && _quick.IsAvailable;
        QuickButton.Text = "Unlock with " + _quick.BiometryName;
        await OfferQuickUnlockAsync();
    }

    /// <summary>
    /// Asks for Face ID / Touch ID once per lock, and only while the app is in front: a vault locked as the app left the screen shows this
    /// page in the background, where iOS would refuse the prompt; AppFlow asks again when the app is active.
    /// </summary>
    public async Task OfferQuickUnlockAsync()
    {
        if (_askedOnce || _flow.SuppressAutoPrompt || !_quick.IsEnabled || !_quick.IsAvailable || !AppFlow.IsActive) return;
        _askedOnce = true;
        await QuickUnlockAsync();
    }

    private async void OnQuickUnlock(object? sender, EventArgs e) => await QuickUnlockAsync();

    // The one busy helper (BMB-311): both unlock buttons are off and the Face ID one spins; a failure is written under them.
    private async Task QuickUnlockAsync()
    {
        ErrorLabel.IsVisible = false;
        await BusyAction.RunAsync(QuickButton, async () =>
        {
            switch (await _quick.UnlockAsync("Open your memory bank"))
            {
                case QuickUnlockResult.Unlocked:
                    _flow.ShowMain();
                    break;
                case QuickUnlockResult.TurnedOff:
                    QuickButton.IsVisible = false;
                    ShowError($"{_quick.BiometryName} was turned off for this memory bank (its key changed). Unlock with the master password; you can turn it on again in Settings.");
                    break;
            }
        }, new BusyOptions { Result = (ok, text) => { if (!ok) ShowError(text); }, AlsoDisable = [PasswordEntry, UnlockButton] });
    }

    private async void OnUnlock(object? sender, EventArgs e)
    {
        var password = PasswordEntry.Text ?? "";
        if (password.Length == 0)
        {
            ShowError("Enter the master password.");
            return;
        }
        ErrorLabel.IsVisible = false;
        // The one busy helper (BMB-311): both unlock buttons are off and the tapped one spins through the key derivation.
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            // The field is emptied whatever the unlock ends in (SecretFields): opened, refused, or an exception.
            if (await SecretFields.RunAsync(() => Task.Run(() => _vault.UnlockAsync(password)), () => PasswordEntry.Text = ""))
                _flow.ShowMain();
            else
                ShowError("Wrong password.");
        }, new BusyOptions { Result = (ok, text) => { if (!ok) ShowError(text); }, AlsoDisable = [PasswordEntry, QuickButton] });
    }

    private void ShowError(string text)
    {
        ErrorLabel.Text = text;
        ErrorLabel.IsVisible = true;
    }
}
