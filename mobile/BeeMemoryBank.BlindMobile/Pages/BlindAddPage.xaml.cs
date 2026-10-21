using BeeMemoryBank.Busy;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindMobile.Pages;

/// <summary>
/// Adds an account: the person pastes the one-time invitation of the computer's owner (or opens its <c>bmb-invite:</c> link, which fills the box), sees what it is - whose copy, from which computer, the
/// limits this phone will give it, how long it still works - and presses Add. Nothing is sent and nothing is written before Add. The preview and every sentence come from
/// <see cref="IBlindAccountsController.PreviewInvitation"/>; the page builds none. "Add with a pairing code" is the old way, per account.
/// </summary>
public partial class BlindAddPage : ContentPage
{
    private readonly BlindDevice _device;
    private readonly IServiceProvider _services;
    private readonly IBlindQrScanner? _scanner;

    public BlindAddPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        _device = services.GetRequiredService<BlindDevice>();
        _scanner = services.GetService<IBlindQrScanner>();
        ScanButton.IsVisible = _scanner is not null;
    }

    /// <summary>A link the system handed over: it goes through the same gate as a paste, into the box, and gets its preview. It is not added.</summary>
    public void Offer(string text) => TakeIntoBox(BlindInviteInput.Accept(text));

    /// <summary>
    /// What the gate let through goes into the box; what it refused is said in the gate's own sentence and the box is left as it is. A text over the limit is never given to the box (the box, its change
    /// callback and the preview would each handle it).
    /// </summary>
    private void TakeIntoBox(BlindInviteInputResult taken)
    {
        if (taken.Refusal is not null) ShowResult(taken.Refusal);
        else if (taken.Text is not null) InviteEntry.Text = taken.Text;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            // The preview needs the list of accounts (the cap, a copy of the same computer already here).
            await _device.StartAsync();
            ShowPreview(InviteEntry.Text);
        }
        catch (Exception)
        {
            ShowResult(BlindPageSentences.DeviceUnreadable);
        }
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e) => ShowPreview(e.NewTextValue);

    /// <summary>From the text alone: pure, cheap, never throws.</summary>
    private void ShowPreview(string? text)
    {
        try
        {
            ResultLabel.IsVisible = false;
            if (string.IsNullOrWhiteSpace(text))
            {
                PreviewFrame.IsVisible = false;
                AddButton.IsEnabled = false;
                return;
            }

            // Past the limit nothing is looked at (the box has a MaxLength, so this is a text that came another way): the gate's sentence, no copy, no parse.
            if (text.Length > BlindInviteInput.MaxLength)
            {
                PreviewFrame.IsVisible = false;
                AddButton.IsEnabled = false;
                ShowResult(BlindInviteInput.TooLong);
                return;
            }

            var preview = _device.Accounts.PreviewInvitation(text);
            PreviewFrame.IsVisible = true;
            DescriptionLabel.IsVisible = preview.SourceName.Length > 0;
            DescriptionLabel.Text = preview.Description;
            LimitsLabel.IsVisible = preview.CanAdd;
            LimitsLabel.Text = preview.Limits;
            ValidityLabel.Text = preview.Validity(DateTimeOffset.UtcNow);
            ValidityLabel.IsVisible = ValidityLabel.Text.Length > 0;
            RefusalLabel.Text = preview.Refusal;
            RefusalLabel.IsVisible = preview.Refusal is not null;
            AddButton.IsEnabled = preview.CanAdd;
        }
        catch (Exception)
        {
            PreviewFrame.IsVisible = false;
            AddButton.IsEnabled = false;
            ShowResult(BlindPageSentences.PreviewFailed);
        }
    }

    // busy-exempt: reads the clipboard, nothing is sent
    private async void OnPasteClicked(object? sender, EventArgs e)
    {
        try
        {
            TakeIntoBox(await BlindInviteInput.FromClipboardAsync(() => Clipboard.Default.GetTextAsync()));
        }
        catch (Exception)
        {
            ShowResult(BlindPageSentences.ClipboardUnreadable);
        }
    }

    // busy-exempt: the system's scanner shows its own screen and returns the text; the text only fills the box
    private async void OnScanClicked(object? sender, EventArgs e)
    {
        try
        {
            if (_scanner is null) return;
            TakeIntoBox(await BlindInviteInput.FromScannerAsync(_scanner));
        }
        catch (Exception)
        {
            ShowResult(BlindPageSentences.ScannerUnavailable);
        }
    }

    private async void OnAddClicked(object? sender, EventArgs e)
    {
        // A long operation (BMB-311): the new account makes its own identity, asks the computer to accept the invitation and waits for its answer; the pop-up covers the app until then.
        // The invitation's secret is held in memory until the computer has answered, and the box is emptied at once after that.
        var added = false;
        string? error = null;
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            var result = await _device.Accounts.AddFromInvitationAsync(InviteEntry.Text);
            error = result.Error;
            added = result.AccountId is not null;
            if (added) InviteEntry.Text = "";
        }, new BusyOptions
        {
            Long = true,
            Title = "Adding the copy",
            Message = "Please wait. This phone is asking the computer to accept the invitation.",
            FailureTitle = "Add account",
        });
        try
        {
            if (error is not null) ShowResult(error);
            if (added) await Navigation.PopAsync();
        }
        catch (Exception ex)
        {
            ShowResult(BusyAction.Describe(ex));
        }
    }

    private async void OnPairingClicked(object? sender, EventArgs e)
    {
        string? id = null;
        string? error = null;
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            var result = await _device.Accounts.AddAsync(null);
            id = result.AccountId;
            error = result.Error;
        }, new BusyOptions { FailureTitle = "Add with a pairing code" });
        try
        {
            if (error is not null) ShowResult(error);
            if (id is null) return;
            // The new account shows its phone code on its own page; this page is no longer needed behind it.
            await Navigation.PushAsync(new BlindHomePage(_services, id));
            Navigation.RemovePage(this);
        }
        catch (Exception ex)
        {
            ShowResult(BusyAction.Describe(ex));
        }
    }

    private void ShowResult(string text)
    {
        try
        {
            ResultLabel.Text = text;
            ResultLabel.IsVisible = true;
        }
        catch (Exception)
        {
            // the page is gone; nothing is left to tell
        }
    }
}
