using BeeMemoryBank.BlindIos.Platforms.iOS;
using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Busy;

namespace BeeMemoryBank.BlindIos.Pages;

/// <summary>
/// "Add account": paste the one-time invitation (or arrive here from the Camera app with it: a <c>bmb-invite:</c> QR code opens the app, which needs no scanner of its own), read what it is -
/// whose copy, from which computer, what this phone will allow it, how long it still works (<see cref="BlindInvitationPreview"/>, from the text alone: nothing is sent or added yet) - and press
/// Add. The invitation is a one-time capability and not an encrypted one; the page says so in AppCore's words. The old way stays: "Add with a pairing code" makes an account that shows this
/// iPhone's code on its own page. The page builds no sentence of its own.
/// </summary>
public partial class AddAccountPage : ContentPage
{
    private readonly IosBlindRuntime _runtime;
    private readonly IosSilenceNotifier _silence;

    /// <param name="invitation">The text a link opened the page with, or null.</param>
    public AddAccountPage(IosBlindRuntime runtime, IosSilenceNotifier silence, string? invitation = null)
    {
        InitializeComponent();
        _runtime = runtime;
        _silence = silence;
        // How to get the invitation here; what it IS (a one-time code, not an encrypted one; a copy this device cannot read) is the preview's own sentence, shown as it is below.
        IntroLabel.Text = "Paste the invitation of the person whose memory bank you keep a copy of (a line that starts with bmb-invite:), or scan its QR code with the Camera app: it opens this app. " +
                          "Nothing is added until you press Add and the owner confirms.";
        if (invitation is not null) InviteEditor.Text = invitation;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await _runtime.StartAsync();
            ShowPreview();
        }
        catch (Exception ex)
        {
            ShowError(BusyAction.Describe(ex));
        }
    }

    private void OnInviteTextChanged(object? sender, TextChangedEventArgs e) => ShowPreview();

    /// <summary>What the pasted text is. Never throws; the Add button is on only for an invitation that can be added.</summary>
    private void ShowPreview()
    {
        try
        {
            var text = InviteEditor.Text ?? "";
            AddErrorLabel.IsVisible = false;
            if (string.IsNullOrWhiteSpace(text))
            {
                PreviewHost.IsVisible = false;
                AddInvitationButton.IsEnabled = false;
                return;
            }

            var preview = _runtime.Accounts.PreviewInvitation(text);
            PreviewHost.IsVisible = true;
            PreviewRefusalLabel.IsVisible = preview.Refusal is not null;
            PreviewRefusalLabel.Text = preview.Refusal ?? "";
            PreviewDescriptionLabel.IsVisible = PreviewLimitsLabel.IsVisible = PreviewValidityLabel.IsVisible = preview.Refusal is null;
            if (preview.Refusal is null)
            {
                PreviewDescriptionLabel.Text = preview.Description;
                PreviewLimitsLabel.Text = preview.Limits;
                PreviewValidityLabel.Text = preview.Validity(DateTimeOffset.UtcNow);
            }
            AddInvitationButton.IsEnabled = preview.CanAdd;
        }
        catch (Exception ex)
        {
            ShowError(BusyAction.Describe(ex));
        }
    }

    // busy-exempt: pastes from the clipboard, nothing is sent and nothing waits
    private async void OnPasteClicked(object? sender, EventArgs e)
    {
        try
        {
            InviteEditor.Text = (await Clipboard.Default.GetTextAsync())?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            ShowError(BusyAction.Describe(ex));
        }
    }

    private async void OnAddInvitationClicked(object? sender, EventArgs e)
    {
        try
        {
            var text = InviteEditor.Text ?? "";
            BlindAddResult? result = null;
            // A long operation (BMB-311): the identity of the source is checked and the invitation redeemed over the network; the one busy helper covers the page until the answer is in,
            // so Add cannot be pressed twice. The refusal, if any, is AppCore's sentence in the result.
            await BusyAction.RunAsync(sender as Button, async () => result = await _runtime.Accounts.AddFromInvitationAsync(text), new BusyOptions
            {
                Long = true,
                Title = "Adding the account",
                Message = "Please wait. The invitation is being checked and handed in.",
                FailureTitle = "Add account",
            });
            if (result is { Ok: true })
            {
                InviteEditor.Text = "";
                // The account is on the Accounts page with the code to read to its owner; the page there watches for the owner's answer.
                await Navigation.PopAsync();
            }
            else if (result is not null)
            {
                ShowError(result.Error ?? "The account could not be added.");
            }
        }
        catch (Exception ex)
        {
            ShowError(BusyAction.Describe(ex));
        }
    }

    private async void OnAddWithCodeClicked(object? sender, EventArgs e)
    {
        try
        {
            // No name typed: the accounts name it themselves ("Account N").
            var name = string.IsNullOrWhiteSpace(PairingNameEntry.Text) ? null : PairingNameEntry.Text.Trim();
            if (name is not null && BlindAccountEntry.CheckName(name) is { } badName)
            {
                ShowError(badName);
                return;
            }

            BlindAddResult? result = null;
            // The identity is made on the phone, no network: a short operation, the button spins until the account exists.
            await BusyAction.RunAsync(sender as Button, async () => result = await _runtime.Accounts.AddAsync(name), new BusyOptions { FailureTitle = "Add account" });
            if (result is { Ok: true, AccountId: { } id })
            {
                // The account shows this iPhone's code on its own page; Back from there returns to the accounts.
                await Navigation.PushAsync(new BlindHomePage(_runtime, _silence, id));
                Navigation.RemovePage(this);
            }
            else if (result is not null)
            {
                ShowError(result.Error ?? "The account could not be added.");
            }
        }
        catch (Exception ex)
        {
            ShowError(BusyAction.Describe(ex));
        }
    }

    private void ShowError(string text)
    {
        try
        {
            AddErrorLabel.Text = text;
            AddErrorLabel.IsVisible = true;
        }
        catch (Exception)
        {
            // the controls are gone (the page is closing); nothing is left to tell
        }
    }
}
