using BeeMemoryBank.Mobile.Services;

namespace BeeMemoryBank.Mobile.Controls;

/// <summary>
/// The mock-up's bottom sheet (dialog.bmb-dialog.bmb-sheet-dialog): a surface panel with 26dp
/// rounded top corners that rises from the bottom over the dimmed screen. A tap on the dim or
/// the system back closes it. The system biometric prompt is NOT drawn here: it is Android's own
/// window (BiometricService), as the product rules require.
/// </summary>
public static class BottomSheet
{
    /// <summary>Shows <paramref name="content"/> as a sheet over <paramref name="parent"/>; the task ends when the sheet closes.</summary>
    public static async Task ShowAsync(Page parent, View content, string? title = null)
    {
        var sheet = new BottomSheetPage(content, title);
        await parent.Navigation.PushModalAsync(sheet);
        await sheet.ClosedTask;
    }

    /// <summary>The sheet itself, for content whose buttons close it: build it, push it, call <see cref="CloseAsync"/>.</summary>
    public sealed class BottomSheetPage : ContentPage
    {
        private readonly TaskCompletionSource _closed = new();
        private readonly Border _panel;

        public Task ClosedTask => _closed.Task;

        public BottomSheetPage(View content, string? title = null)
        {
            BackgroundColor = ThemeManager.Token("Scrim");
            AutomationId = "BottomSheet";

            var stack = new VerticalStackLayout { Spacing = 12 };
            if (title is not null)
            {
                stack.Children.Add(new Label
                {
                    Text = title,
                    Style = (Style?)Application.Current?.Resources["DialogTitle"],
                });
            }
            stack.Children.Add(content);

            _panel = new Border
            {
                BackgroundColor = ThemeManager.Token("Surface"),
                Stroke = ThemeManager.Token("BorderStrong"),
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle
                {
                    CornerRadius = new CornerRadius(26, 26, 0, 0),
                },
                Padding = new Thickness(20, 16, 20, 24),
                // Hug the content at the bottom of the screen. In an Auto grid row the ScrollView
                // took the whole allowed height, so the sheet filled the screen (test phone 14.11).
                VerticalOptions = LayoutOptions.End,
                Content = new ScrollView { Content = stack, VerticalOptions = LayoutOptions.End },
            };
            // A tap inside the panel is the panel's own, not the dim's.
            _panel.GestureRecognizers.Add(new TapGestureRecognizer());

            var dim = new Grid();
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => _ = CloseAsync();
            dim.GestureRecognizers.Add(tap);
            dim.Children.Add(_panel);
            Content = dim;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            // The sheet never covers the whole screen: the dim above stays tappable.
            if (Window is { } window)
                _panel.MaximumHeightRequest = window.Height * 0.85;
        }

        private bool _closing;

        public async Task CloseAsync()
        {
            if (_closing) return;   // a second tap while closing closes nothing new
            _closing = true;
            // Pop first; the closed signal follows in the finally, so a caller awaiting ClosedTask
            // resumes with the sheet already off the modal stack.
            try
            {
                if (Navigation.ModalStack.LastOrDefault() == this)
                    await Navigation.PopModalAsync();
            }
            catch (InvalidOperationException)
            {
                // the modal stack changed under us (the activity was re-created): nothing left to close
            }
            finally
            {
                _closed.TrySetResult();
            }
        }

        protected override bool OnBackButtonPressed()
        {
            _ = CloseAsync();
            return true;
        }
    }
}
