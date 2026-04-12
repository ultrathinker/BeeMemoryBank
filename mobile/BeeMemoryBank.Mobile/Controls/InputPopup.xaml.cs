namespace BeeMemoryBank.Mobile.Controls;

public partial class InputPopup : ContentView
{
    private readonly TaskCompletionSource<string?> _tcs = new();

    public Task<string?> ResultTask => _tcs.Task;

    public InputPopup(string title, string message, string accept = "OK", string cancel = "Cancel",
        string? placeholder = null, string? initialValue = null, int maxLength = 200)
    {
        InitializeComponent();
        TitleLabel.Text = title;
        MessageLabel.Text = message;
        AcceptButton.Text = accept;
        CancelButton.Text = cancel;
        InputEntry.Placeholder = placeholder ?? "";
        InputEntry.Text = initialValue ?? "";
        InputEntry.MaxLength = maxLength;

        Loaded += (_, _) => InputEntry.Focus();
    }

    private void OnCancelClicked(object? sender, EventArgs e)
    {
        InputEntry.Unfocus();
        _tcs.TrySetResult(null);
    }

    private void OnAcceptClicked(object? sender, EventArgs e)
    {
        InputEntry.Unfocus();
        _tcs.TrySetResult(InputEntry.Text);
    }
}
