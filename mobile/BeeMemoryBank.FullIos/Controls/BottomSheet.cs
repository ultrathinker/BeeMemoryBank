namespace BeeMemoryBank.FullIos.Controls;

/// <summary>
/// The design's bottom sheet (dialog.bmb-dialog.bmb-sheet-dialog): a surface panel with 26dp
/// rounded top corners that rises from the bottom over the dimmed screen. A tap on the dim
/// closes it. Copied from the Android app's BottomSheet with the pop-first close order: the
/// sheet leaves the modal stack before its closed signal, so a caller awaiting it resumes with
/// the sheet already gone. The page's safe area keeps the panel above the home indicator.
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
        private readonly Grid _dim;
        private bool _closing;

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
                    Style = Application.Current?.Resources.TryGetValue("DialogTitle", out var s) == true && s is Style style ? style : null,
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
                // Hug the content at the bottom of the screen: in an Auto grid row the ScrollView
                // would take the whole allowed height and the sheet would fill the screen.
                VerticalOptions = LayoutOptions.End,
                Content = new ScrollView { Content = stack, VerticalOptions = LayoutOptions.End },
            };
            // A tap inside the panel is the panel's own, not the dim's.
            _panel.GestureRecognizers.Add(new TapGestureRecognizer());

            _dim = new Grid();
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => _ = CloseAsync();
            _dim.GestureRecognizers.Add(tap);
            _dim.Children.Add(_panel);
            Content = _dim;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            // The sheet never covers the whole screen: the dim above stays tappable.
            if (Window is { } window)
                _panel.MaximumHeightRequest = window.Height * 0.85;
            // A Shell modal keeps its content above the home indicator whatever the safe-area
            // switches say (checked on the simulator: neither lifts it). The sheet must rise from
            // the very bottom of the screen, so measure the leftover gap once laid out and reach
            // down by it; the home indicator then draws over the panel's bottom padding.
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () =>
            {
                if (Window is not { } window) return;
                var gap = window.Height - (_panel.Y + _panel.Height);
                if (gap > 1)
                    _dim.Margin = new Thickness(0, 0, 0, -gap);
            });
        }

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
                // the modal stack changed under us: nothing left to close
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
