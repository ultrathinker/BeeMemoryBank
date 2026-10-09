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
    private bool _busy;

    public JoinPage(FullVault vault, AppFlow flow)
    {
        InitializeComponent();
        _vault = vault;
        _flow = flow;
        NameEntry.Text = FullVault.NodeName(null, DeviceInfo.Current.Name);
    }

    /// <summary>A code that arrived as a link (the Camera scanned the computer's QR code).</summary>
    public void UseCode(string code) => CodeEditor.Text = code;

    private async void OnPaste(object? sender, EventArgs e)
    {
        var text = await Clipboard.Default.GetTextAsync();
        if (!string.IsNullOrWhiteSpace(text)) CodeEditor.Text = text.Trim();
    }

    private async void OnJoin(object? sender, EventArgs e)
    {
        if (_busy) return;
        ErrorLabel.IsVisible = false;
        var code = CodeEditor.Text ?? "";
        var password = PasswordEntry.Text ?? "";
        var name = FullVault.NodeName(NameEntry.Text, DeviceInfo.Current.Name);

        SetBusy(true);
        using var steps = new CancellationTokenSource();
        _ = ShowStepsAsync(steps.Token);
        try
        {
            // The field is emptied whatever the join ends in (SecretFields), not just when it works.
            await SecretFields.RunAsync(() => Task.Run(() => _vault.JoinAsync(name, code, password)), () => PasswordEntry.Text = "");
            await _flow.OfferQuickUnlockAsync(this);
            _flow.ShowMain();
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = ex.Message;
            ErrorLabel.IsVisible = true;
        }
        finally
        {
            steps.Cancel();
            SetBusy(false);
        }
    }

    private async Task ShowStepsAsync(CancellationToken ct)
    {
        for (var i = 0; !ct.IsCancellationRequested; i++)
        {
            StatusLabel.Text = Steps[Math.Min(i, Steps.Length - 1)];
            try { await Task.Delay(4000, ct); }
            catch (TaskCanceledException) { break; }
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CodeEditor.IsEnabled = PasswordEntry.IsEnabled = NameEntry.IsEnabled = JoinButton.IsEnabled = !busy;
        Busy.IsVisible = Busy.IsRunning = busy;
        StatusLabel.IsVisible = busy;
    }
}
