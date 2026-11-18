using BeeMemoryBank.Mobile.Localization;

namespace BeeMemoryBank.Mobile.Controls;

public partial class InputPopup : ContentView
{
    private readonly TaskCompletionSource<string?> _tcs = new();

    public Task<string?> ResultTask => _tcs.Task;

    // The hosting modal page sets this so it can pop itself FIRST and only then hand back the answer:
    // a TaskCompletionSource continuation runs synchronously, and a caller that navigates right after
    // the answer must not do so while the popup is still the top modal.
    internal Action<string?>? AnswerHandler;

    internal void Complete(string? value) => _tcs.TrySetResult(value);

    public InputPopup(string title, string message, string? accept = null, string? cancel = null,
        string? placeholder = null, string? initialValue = null, int maxLength = 200, bool isPassword = false)
    {
        InitializeComponent();
        TitleLabel.Text = title;
        MessageLabel.Text = message;
        AcceptButton.Text = accept ?? AppStrings.Get("Ok");
        CancelButton.Text = cancel ?? AppStrings.Get("Cancel");
        InputEntry.Placeholder = placeholder ?? "";
        InputEntry.Text = initialValue ?? "";
        InputEntry.MaxLength = maxLength;
        InputEntry.IsPassword = isPassword;

        Loaded += (_, _) => InputEntry.Focus();
    }

    private void Answer(string? value)
    {
        InputEntry.Unfocus();
        if (AnswerHandler is { } handler) handler(value);
        else _tcs.TrySetResult(value);
    }

    private void OnCancelClicked(object? sender, EventArgs e) => Answer(null);

    private void OnAcceptClicked(object? sender, EventArgs e) => Answer(InputEntry.Text);

    // Answer "cancel" (the system back or a tap on the dim goes through the hosting modal page).
    public void Cancel() => Answer(null);
}
