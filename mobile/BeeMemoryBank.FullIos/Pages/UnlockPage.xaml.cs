using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.FullIos.Controls;
using BeeMemoryBank.FullIos.Localization;
using BeeMemoryBank.FullIos.Services;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>The locked vault: the master password, or Face ID / Touch ID when it is turned on (asked at once when the page appears).</summary>
public partial class UnlockPage : ContentPage
{
    private readonly FullVault _vault;
    private readonly QuickUnlock _quick;
    private readonly AppFlow _flow;
    private readonly IServiceProvider _services;
    private bool _askedOnce;
    /// <summary>Face ID turned itself off on this page (its secret no longer opened the vault): the password unlock offers it again.</summary>
    private bool _quickTurnedOff;

    public UnlockPage(FullVault vault, QuickUnlock quick, AppFlow flow, IServiceProvider services)
    {
        InitializeComponent();
        GateScroll.Attach(GateViewer, GateRoot);
        // The other gate cards fill the page width minus the margins because their entries ask
        // for the offered width; this card has none, so give it the same width (display only).
        GateViewer.SizeChanged += (_, _) =>
        {
            if (GateViewer.Width > 0)
                UnlockCard.WidthRequest = Math.Min(400, GateViewer.Width - 48);
        };
        _vault = vault;
        _quick = quick;
        _flow = flow;
        _services = services;
    }

    /// <summary>
    /// Runs before anything is unlocked: on an encrypted phone the database is closed, and nothing here reads it (the app died at
    /// every cold start on that read). The node's name comes from FullVault, which reads the boot store's mirror while the database is closed; the
    /// hint shows only where it is readable. An async void page event that throws ends the app, so a failure here is logged and the page stays
    /// usable with its plain subtitle: the person can still type the password.
    /// </summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            var identity = await _vault.IdentityAsync();
            SubtitleLabel.Text = identity is null || string.IsNullOrWhiteSpace(identity.DisplayName) ? AppStrings.Get("UnlockSubtitle") : identity.DisplayName;
            var quickVisible = _quick.IsEnabled && _quick.IsAvailable;
            QuickButton.IsVisible = quickVisible;
            OrLabel.IsVisible = quickVisible;
            QuickButton.Text = AppStrings.Format("UnlockQuickFormat", _quick.BiometryName ?? "");
            await ShowHintAsync();
            await OfferQuickUnlockAsync();
        }
        catch (Exception ex)
        {
            _services.GetService<ILogger<UnlockPage>>()?.LogError(ex, "Preparing the unlock page failed");
            SubtitleLabel.Text = AppStrings.Get("UnlockSubtitle");
        }
    }

    /// <summary>
    /// The hint the create page promises to show here, read without unlocking: on a plain phone the user table is open while locked; on an
    /// encrypted one the database is closed before unlock, FullVault reads nothing and no hint shows. Display only.
    /// </summary>
    private async Task ShowHintAsync()
    {
        HintLabel.IsVisible = false;
        if (await _vault.PasswordHintAsync() is not { } hint) return;
        HintLabel.Text = AppStrings.Format("UnlockHintFormat", hint);
        HintLabel.IsVisible = true;
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
            switch (await _quick.UnlockAsync(AppStrings.Get("UnlockSubtitle")))
            {
                case QuickUnlockResult.Unlocked:
                    _flow.ShowMain();
                    break;
                case QuickUnlockResult.UnlockedButTurnedOff:
                    // Open, but an older Face ID could not be kept: the notes, then the question to turn it on again.
                    _flow.ShowMain();
                    _ = _flow.OfferQuickUnlockAgainAsync();
                    break;
                case QuickUnlockResult.TurnedOff:
                    _quickTurnedOff = true;
                    QuickButton.IsVisible = false;
                    OrLabel.IsVisible = false;
                    ShowError(AppStrings.Format("UnlockQuickOff", _quick.BiometryName ?? ""));
                    break;
            }
        }, new BusyOptions { Result = (ok, text) => { if (!ok) ShowError(text); }, AlsoDisable = [PasswordEntry, UnlockButton] });
    }

    private async void OnUnlock(object? sender, EventArgs e)
    {
        var password = PasswordEntry.Text ?? "";
        if (password.Length == 0)
        {
            ShowError(AppStrings.Get("UnlockEmpty"));
            return;
        }
        ErrorLabel.IsVisible = false;
        // The one busy helper (BMB-311): both unlock buttons are off and the tapped one spins through the key derivation.
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            // The field is emptied whatever the unlock ends in (SecretFields): opened, refused, or an exception.
            if (await SecretFields.RunAsync(() => Task.Run(() => _vault.UnlockAsync(password)), () => PasswordEntry.Text = ""))
            {
                _flow.ShowMain();
                // Once, as before 2.12: the password after a Face ID that no longer opened; then the question to turn it on again.
                if (_quickTurnedOff) _ = _flow.OfferQuickUnlockAgainAsync();
            }
            else
                ShowError(AppStrings.Get("UnlockWrong"));
        }, new BusyOptions { Result = (ok, text) => { if (!ok) ShowError(text); }, AlsoDisable = [PasswordEntry, QuickButton] });
    }

    private void ShowError(string text)
    {
        ErrorLabel.Text = text;
        ErrorLabel.IsVisible = true;
    }

#if DEBUG || BMB_E2E
    /// <summary>The design check photographs the wrong-password state through the page's own unlock path.</summary>
    internal void WrongPasswordForE2E()
    {
        PasswordEntry.Text = "not-the-master-password";
        OnUnlock(null, EventArgs.Empty);
    }
#endif
}
