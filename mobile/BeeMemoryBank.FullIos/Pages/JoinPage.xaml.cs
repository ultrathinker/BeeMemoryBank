using BeeMemoryBank.Busy;
using BeeMemoryBank.FullIos.Services;

namespace BeeMemoryBank.FullIos.Pages;

/// <summary>
/// Joining the owner's memory bank: a join code from a computer's "Connect a device" (or a server's https address), the master password,
/// a name. The join itself is the Android app's (NodeSetupService): the key exchange, then the whole vault downloaded as a snapshot.
/// </summary>
public partial class JoinPage : ContentPage
{
    private static readonly string[] Steps =
    [
        "Contacting your computer…",
        "Checking the master password…",
        "Exchanging keys…",
        "Downloading your memory bank…",
        "Importing your notes…",
        "Almost done…",
    ];

    private readonly FullVault _vault;
    private readonly AppFlow _flow;

    public JoinPage(FullVault vault, AppFlow flow)
    {
        InitializeComponent();
        _vault = vault;
        _flow = flow;
        NameEntry.Text = FullVault.NodeName(null, DeviceInfo.Current.Name);
    }

    /// <summary>A code that arrived as a link (the Camera scanned the computer's QR code).</summary>
    public void UseCode(string code) => CodeEditor.Text = code;

    // busy-exempt: pastes from the clipboard, nothing is sent and nothing waits
    private async void OnPaste(object? sender, EventArgs e)
    {
        var text = await Clipboard.Default.GetTextAsync();
        if (!string.IsNullOrWhiteSpace(text)) CodeEditor.Text = text.Trim();
    }

    private async void OnJoin(object? sender, EventArgs e)
    {
        ErrorLabel.IsVisible = false;
        var code = CodeEditor.Text ?? "";
        var password = PasswordEntry.Text ?? "";
        var name = FullVault.NodeName(NameEntry.Text, DeviceInfo.Current.Name);

        // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up with the steps of the join, so Join cannot be tapped
        // twice; the whole error is written under the button when it fails.
        var ran = await BusyAction.RunAsync(sender as Button, async busy =>
        {
            using var steps = new CancellationTokenSource();
            _ = ShowStepsAsync(busy, steps.Token);
            try
            {
                // The field is emptied whatever the join ends in (SecretFields), not just when it works.
                await SecretFields.RunAsync(() => Task.Run(() => _vault.JoinAsync(name, code, password)), () => PasswordEntry.Text = "");
            }
            finally
            {
                steps.Cancel();
            }
        }, new BusyOptions
        {
            Long = true,
            Title = "Joining your memory bank",
            Message = "Please wait. The whole memory bank is downloaded; this can take several minutes on a slow connection.",
            ResultLabel = ErrorLabel,
        });
        if (!ran) return;
        // After the pop-up is gone: the Face ID question and the move to the main screen must not sit under it.
        await _flow.OfferQuickUnlockAsync(this);
        _flow.ShowMain();
    }

    private static async Task ShowStepsAsync(BusyProgress busy, CancellationToken ct)
    {
        for (var i = 0; !ct.IsCancellationRequested; i++)
        {
            busy.Report(null, Steps[Math.Min(i, Steps.Length - 1)]);
            try { await Task.Delay(4000, ct); }
            catch (TaskCanceledException) { break; }
        }
    }
}
