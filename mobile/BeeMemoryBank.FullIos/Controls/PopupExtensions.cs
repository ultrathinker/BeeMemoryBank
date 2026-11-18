namespace BeeMemoryBank.FullIos.Controls;

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

    // A modal page so the scrim dims the whole window - the tab bar too - the same way the bottom
    // sheets and AppDialog do. An in-page overlay could not reach it.
    private sealed class InputPopupPage : ContentPage
    {
        private readonly InputPopup _popup;
        private bool _closing;

        public InputPopupPage(InputPopup popup)
        {
            _popup = popup;
            BackgroundColor = ThemeManager.Token("Scrim");
            AutomationId = "InputPopup";

            popup.AnswerHandler = answer => _ = CloseAsync(answer);

            var dim = new Grid();
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => _ = CloseAsync(null);
            dim.GestureRecognizers.Add(tap);
            dim.Children.Add(popup);
            Content = dim;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            // Just under the page header when the whole card still fits above the keyboard;
            // otherwise keep the higher place. The card is measured once laid out.
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () =>
            {
                if (Window is not { } window || _popup.Height <= 0) return;
                var safeTop = (window.Handler?.PlatformView as UIKit.UIWindow)?.SafeAreaInsets.Top ?? 0;
                const double navBar = 44;
                const double keyboard = 340;
                var top = safeTop + navBar + _popup.Height <= window.Height - keyboard ? navBar : 24;
                _popup.Margin = new Thickness(16, top, 16, 0);
            });
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
                // the modal stack changed under us: nothing left to pop
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
