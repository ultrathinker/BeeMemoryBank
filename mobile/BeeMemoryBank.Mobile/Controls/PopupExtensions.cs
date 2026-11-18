using BeeMemoryBank.Mobile.Services;

namespace BeeMemoryBank.Mobile.Controls;

public static class PopupExtensions
{
    public static async Task<string?> ShowInputPopupAsync(this ContentPage page,
        string title, string message,
        string? accept = null, string? cancel = null,
        string? placeholder = null, string? initialValue = null, int maxLength = 200, bool isPassword = false)
    {
        var popup = new InputPopup(title, message, accept, cancel, placeholder, initialValue, maxLength, isPassword);
        var modal = new InputPopupPage(popup);
        await page.Navigation.PushModalAsync(modal);
        // The modal pops itself before completing the popup's task, so the answer reaches the caller
        // with the popup already off the modal stack.
        return await popup.ResultTask;
    }

    // A modal page so the scrim dims the whole window - the tab bar and the status bar too - the
    // same way the bottom sheets and AppDialog do. An in-page overlay could not reach them.
    private sealed class InputPopupPage : ContentPage
    {
        private readonly InputPopup _popup;
        private bool _closing;

        public InputPopupPage(InputPopup popup)
        {
            _popup = popup;
            popup.AnswerHandler = answer => _ = CloseAsync(answer);
            BackgroundColor = ThemeManager.Token("Scrim");
            AutomationId = "InputPopup";

            var dim = new Grid();
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => _ = CloseAsync(null);
            dim.GestureRecognizers.Add(tap);
            dim.Children.Add(popup);
            Content = dim;
        }

        // Pop FIRST, then hand back the answer (see AppDialog): a TaskCompletionSource continuation
        // runs synchronously, so a caller that navigates right after the answer must see this popup
        // already gone, not still the top modal.
        private async Task CloseAsync(string? answer)
        {
            if (_closing) return;   // a second tap while closing answers nothing new
            _closing = true;
            try
            {
                if (Navigation.ModalStack.LastOrDefault() == this)
                    await Navigation.PopModalAsync();
            }
            catch (InvalidOperationException)
            {
                // the modal stack changed under us (the activity was re-created): nothing left to close
            }
            _popup.Complete(answer);
        }

        protected override bool OnBackButtonPressed()
        {
            _ = CloseAsync(null);
            return true;
        }
    }
}
