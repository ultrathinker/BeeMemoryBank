namespace BeeMemoryBank.FullIos.Pages;

/// <summary>A recovery key, shown once: copied only on request (this phone only, two minutes), forgotten when the page goes.</summary>
public partial class RecoveryKeyPage : ContentPage
{
    private readonly AppFlow _flow;
    private string? _key;
    private bool _firstTime;

    public RecoveryKeyPage(AppFlow flow)
    {
        InitializeComponent();
        _flow = flow;
    }

    public void Show(string key, bool firstTime)
    {
        _key = key;
        _firstTime = firstTime;
        // Groups of four are easier to copy by hand; the key itself is the plain Base64 text (the spaces are not part of it).
        KeyLabel.Text = string.Join(" ", Enumerable.Range(0, (key.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4))));
    }

    private void OnCopy(object? sender, EventArgs e)
    {
        if (_key is null) return;
        Platforms.iOS.IosClipboard.CopySecret(_key);
        CopyButton.Text = "Copied for two minutes";
    }

    private void OnConfirmToggled(object? sender, ToggledEventArgs e) => ContinueButton.IsEnabled = e.Value;

    private async void OnContinue(object? sender, EventArgs e)
    {
        _key = null;
        KeyLabel.Text = "";
        if (_firstTime) await _flow.OfferQuickUnlockAsync(this);
        _flow.ShowMain();
    }
}
