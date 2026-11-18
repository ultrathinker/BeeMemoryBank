using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Mobile.Services;

namespace BeeMemoryBank.Mobile.Controls;

/// <summary>
/// The mock-up's dialog (dialog.bmb-dialog): a centered surface card with 24dp corners over the
/// dimmed screen, a bold title, a secondary body and the button row. A tap on the dim or the
/// system back answers "cancel". This is the app's own dialog; the system biometric prompt stays
/// Android's window (BiometricService).
/// </summary>
public static class AppDialog
{
    /// <summary>A question with two answers; true when the accept button was tapped.</summary>
    public static Task<bool> ConfirmAsync(Page parent, string title, string message,
        string? accept = null, string? cancel = null, bool danger = false)
        => ShowAsync(parent, title, message, accept ?? AppStrings.Get("Ok"), cancel ?? AppStrings.Get("Cancel"), danger);

    /// <summary>A message with one OK button.</summary>
    public static Task AlertAsync(Page parent, string title, string message, string? ok = null)
        => ShowAsync(parent, title, message, ok ?? AppStrings.Get("Ok"), null, false);

    /// <summary>
    /// The mock-up's three-answer dialog (the editor's "save changes?"): a primary button, a
    /// secondary one and a quiet cancel, stacked. Returns 0, 1 or 2 — the button tapped; the dim
    /// and the system back answer 2.
    /// </summary>
    public static async Task<int> ThreeWayAsync(Page parent, string title, string message,
        string primary, string secondary, string? cancel = null)
    {
        var dialog = new DialogPage(title, message,
            [(primary, "PrimaryButton"), (secondary, "SecondaryButton"), (cancel ?? AppStrings.Get("Cancel"), "TextButton")],
            vertical: true, cancelIndex: 2);
        await parent.Navigation.PushModalAsync(dialog);
        return await dialog.ResultTask;
    }

    private static async Task<bool> ShowAsync(Page parent, string title, string message,
        string accept, string? cancel, bool danger)
    {
        var buttons = new List<(string Text, string Style)>();
        if (cancel is not null) buttons.Add((cancel, "SecondaryButton"));
        buttons.Add((accept, danger ? "DangerButton" : "PrimaryButton"));
        var acceptIndex = buttons.Count - 1;
        var dialog = new DialogPage(title, message, buttons, vertical: false, cancelIndex: 0);
        await parent.Navigation.PushModalAsync(dialog);
        return await dialog.ResultTask == acceptIndex;
    }

    private sealed class DialogPage : ContentPage
    {
        private readonly TaskCompletionSource<int> _result = new();
        private readonly int _cancelIndex;

        public Task<int> ResultTask => _result.Task;

        public DialogPage(string title, string message, List<(string Text, string Style)> buttons, bool vertical, int cancelIndex)
        {
            BackgroundColor = ThemeManager.Token("Scrim");
            AutomationId = "AppDialog";
            _cancelIndex = cancelIndex;   // the dim and the system back answer this button

            var resources = Application.Current?.Resources;
            var stack = new VerticalStackLayout { Spacing = 0 };
            stack.Children.Add(new Label { Text = title, Style = (Style?)resources?["DialogTitle"] });
            stack.Children.Add(new Label { Text = message, Style = (Style?)resources?["DialogBody"] });

            Layout row = vertical
                ? new VerticalStackLayout { Spacing = 8 }
                : new HorizontalStackLayout { Spacing = 12, HorizontalOptions = LayoutOptions.End };
            for (var i = 0; i < buttons.Count; i++)
            {
                var index = i;
                var button = new Button
                {
                    Text = buttons[i].Text,
                    Style = (Style?)resources?[buttons[i].Style],
                };
                if (vertical) button.HorizontalOptions = LayoutOptions.Fill;
                button.Clicked += (_, _) => _ = CloseAsync(index);
                row.Children.Add(button);   // Layout.Children is IList<IView>; a cast to IList<View> throws
            }
            stack.Children.Add(row);

            var panel = new Border
            {
                BackgroundColor = ThemeManager.Token("Surface"),
                Stroke = ThemeManager.Token("BorderStrong"),
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(24) },
                Padding = new Thickness(20),
                Margin = new Thickness(16),
                MaximumWidthRequest = 400,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Content = stack,
            };
            panel.GestureRecognizers.Add(new TapGestureRecognizer());   // a tap inside is not the dim's

            var dim = new Grid();
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => _ = CloseAsync(_cancelIndex);
            dim.GestureRecognizers.Add(tap);
            dim.Children.Add(panel);
            Content = dim;
        }

        private bool _closing;

        private async Task CloseAsync(int answer)
        {
            if (_closing) return;   // a second tap while closing answers nothing new
            _closing = true;
            // Pop FIRST, then give the answer. A TaskCompletionSource runs its continuation
            // synchronously, so a caller that navigates right after the answer (the editor's "..")
            // must see this dialog already gone — otherwise Shell pops the dialog, not the page,
            // and the editor traps the person.
            try
            {
                if (Navigation.ModalStack.LastOrDefault() == this)
                    await Navigation.PopModalAsync();
            }
            catch (InvalidOperationException)
            {
                // the modal stack changed under us: nothing left to pop
            }
            _result.TrySetResult(answer);
        }

        protected override bool OnBackButtonPressed()
        {
            _ = CloseAsync(_cancelIndex);
            return true;
        }
    }
}
