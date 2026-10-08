using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>The locked vault: the master password, or Face ID / Touch ID when it is turned on (asked at once when the page appears).</summary>
public partial class UnlockPage : ContentPage
{
    private readonly FullVault _vault;
    private readonly QuickUnlock _quick;
    private readonly AppFlow _flow;
    private bool _busy;
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

    private async Task QuickUnlockAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
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
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnUnlock(object? sender, EventArgs e)
    {
        if (_busy) return;
        var password = PasswordEntry.Text ?? "";
        if (password.Length == 0)
        {
            ShowError("Enter the master password.");
            return;
        }
        SetBusy(true);
        try
        {
            if (await Task.Run(() => _vault.UnlockAsync(password)))
            {
                PasswordEntry.Text = "";
                _flow.ShowMain();
            }
            else
            {
                PasswordEntry.Text = "";
                ShowError("Wrong password.");
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (busy) ErrorLabel.IsVisible = false;
        PasswordEntry.IsEnabled = UnlockButton.IsEnabled = QuickButton.IsEnabled = !busy;
        Busy.IsVisible = Busy.IsRunning = busy;
    }

    private void ShowError(string text)
    {
        ErrorLabel.Text = text;
        ErrorLabel.IsVisible = true;
    }
}
