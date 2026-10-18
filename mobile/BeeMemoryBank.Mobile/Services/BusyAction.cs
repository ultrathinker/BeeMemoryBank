namespace BeeMemoryBank.Busy;

/// <summary>What a button's work is called and where its result goes (BMB-311).</summary>
public sealed class BusyOptions
{
    /// <summary>
    /// A long operation (a first sync, an encryption, a backup, a wipe, a pairing ...): a modal page with a loader and "please wait" covers the
    /// app until the work ends, so nothing can be tapped a second time. A short one only disables its button and spins in it.
    /// </summary>
    public bool Long { get; init; }

    /// <summary>The headline of the pop-up of a long operation.</summary>
    public string Title { get; init; } = "Working...";

    /// <summary>The line under the headline.</summary>
    public string Message { get; init; } = "Please wait. This can take a while. Do not close the app.";

    /// <summary>
    /// Where the result is written, next to the button: the success text (when <see cref="Success"/> is set) or the whole error. Without one
    /// the error is shown in a dialog on the current page, with its full text.
    /// </summary>
    public Label? ResultLabel { get; init; }

    /// <summary>The text shown in <see cref="ResultLabel"/> when the work ends well; nothing is shown when it is null.</summary>
    public string? Success { get; init; }

    /// <summary>The headline of the dialog that shows a failure when there is no label for it (the whole error text is its body).</summary>
    public string FailureTitle { get; init; } = "That did not work";

    /// <summary>For a page that shows its results in its own way: called with (ok, text) instead of the label or the dialog.</summary>
    public Action<bool, string>? Result { get; init; }

    /// <summary>For a button that is to stay off when the work ends well (a one-way step): it is not switched back on.</summary>
    public bool KeepDisabled { get; init; }

    /// <summary>Other controls of the page that are off while the work runs (the fingerprint button next to the unlock button, a field).</summary>
    public IReadOnlyList<VisualElement> AlsoDisable { get; init; } = [];
}

/// <summary>What a long piece of work tells the pop-up while it runs.</summary>
public sealed class BusyProgress
{
    private readonly BusyAction.IBusyView? _view;

    internal BusyProgress(BusyAction.IBusyView? view) => _view = view;

    /// <summary>Moves the bar (0 to 1; null leaves it as it is) and the line under it (null leaves it as it is). Safe to call from any thread.</summary>
    public void Report(double? fraction, string? step)
        => MainThread.BeginInvokeOnMainThread(() => _view?.Update(fraction, step));
}

/// <summary>
/// THE one way a button of the app runs work that waits for something (BMB-311): a second tap while it runs is ignored, the button is disabled at
/// once and spins, a long operation puts up a modal "please wait", and the result - or the full error - is shown where the button is.
/// A handler that bypasses it fails BusyHelperGuardTests unless it says why with a <c>busy-exempt:</c> comment.
/// <para>
/// The target is whatever was tapped: a <see cref="Button"/>, a toolbar or swipe item (<see cref="MenuItem"/>), any other control, or null
/// (a tap on a row or a gesture: only the "already running" guard applies).
/// </para>
/// <para>
/// The source file is shared by the four MAUI apps (Android full, Android blind, iPhone full, iPhone blind): each links it into its project.
/// </para>
/// </summary>
public static class BusyAction
{
    private const string Spinner = "◐◓◑◒";

    // The targets whose work is in flight. UI thread only.
    private static readonly HashSet<object> Running = [];

    /// <summary>Runs <paramref name="work"/> behind <paramref name="target"/>. True when it ended well; false when it failed, was cancelled, or was already running.</summary>
    public static Task<bool> RunAsync(BindableObject? target, Func<Task> work, BusyOptions? options = null)
        => RunAsync(target, _ => work(), options, work.Method);

    /// <summary>The same, for work that can say how far it is: <see cref="BusyProgress.Report"/> moves the bar of the pop-up of a long operation.</summary>
    public static Task<bool> RunAsync(BindableObject? target, Func<BusyProgress, Task> work, BusyOptions? options = null)
        => RunAsync(target, work, options, work.Method);

    private static async Task<bool> RunAsync(BindableObject? target, Func<BusyProgress, Task> work, BusyOptions? options, object identity)
    {
        options ??= new BusyOptions();
        object key = (object?)target ?? identity;
        if (!Running.Add(key)) return false;   // a second tap: nothing starts

        var face = Face.Of(target);
        var original = face.Text;
        var wasEnabled = face.Enabled;
        var others = options.AlsoDisable.Where(o => o.IsEnabled).ToList();
        IDispatcherTimer? spin = null;
        Page? modal = null;
        INavigation? navigation = null;
        BusyPage? popup = null;
        var ended = false;
        EventHandler? onUnloaded = null;
        Guarded(() => { if (options.ResultLabel is { } label) label.IsVisible = false; });
        try
        {
            face.Enabled = false;
            spin = StartSpinner(target, face, original);
            // The page of the target can leave while the work goes on (the activity is re-created on a rotation, the page is replaced or disposed): its
            // spinner stops with it, instead of ticking on a control nobody sees.
            if (target is VisualElement element)
            {
                onUnloaded = (_, _) => spin?.Stop();
                element.Unloaded += onUnloaded;
            }
            foreach (var other in others) other.IsEnabled = false;
            if (options.Long)
            {
                navigation = CurrentNavigation();
                if (navigation != null)
                {
                    modal = popup = new BusyPage(options.Title, options.Message);
                    await navigation.PushModalAsync(modal, false);
                }
            }

            await work(new BusyProgress(popup));

            ended = true;
            if (options.Success is { } done) await ShowResultAsync(options, ok: true, done);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;   // the user (or the app) stopped it: there is nothing to report
        }
        catch (Exception ex)
        {
            // Closed before the dialog, or the dialog would sit under the pop-up.
            await CloseModalQuietlyAsync(navigation, modal);
            modal = null;
            await ShowResultAsync(options, ok: false, Describe(ex));
            return false;
        }
        finally
        {
            // The key is freed by the OUTER finally, whatever the clean-up inside it does: a platform exception from the pop-up's dismissal, from the spinner or from a
            // control of a page that is gone must never leave the key taken (the same button refused for ever).
            try
            {
                await CloseModalQuietlyAsync(navigation, modal);
                Guarded(() => spin?.Stop());
                if (onUnloaded != null && target is VisualElement element) Guarded(() => element.Unloaded -= onUnloaded);
                // What the work itself put on the button (a new caption) is not overwritten. Every touch of a control is guarded: when its page is gone
                // (disposed with its activity) there is nothing left to restore, and the end of the work must still free the key below.
                Guarded(() =>
                {
                    if (IsSpinnerText(face.Text)) face.Text = original;
                    if (!face.Enabled && !(ended && options.KeepDisabled)) face.Enabled = wasEnabled;
                    foreach (var other in others) other.IsEnabled = true;
                });
            }
            finally
            {
                Running.Remove(key);
            }
        }
    }

    /// <summary>True while <paramref name="target"/> has work in flight.</summary>
    public static bool IsRunning(BindableObject target) => Running.Contains(target);

    /// <summary>
    /// The story of an exception for the screen: the text of the failures of local work and of everything inside them, and the kind of failure (never the words) of
    /// a call to another device. The one mechanism is <see cref="BeeMemoryBank.Sync.FailureText.Story"/>; it tells the kinds apart by the type of the exception.
    /// </summary>
    public static string Describe(Exception ex) => BeeMemoryBank.Sync.FailureText.Story(ex);

    private static bool IsSpinnerText(string? text) => !string.IsNullOrEmpty(text) && Spinner.Contains(text[0]);

    private static IDispatcherTimer? StartSpinner(BindableObject? target, Face face, string? original)
    {
        if (target == null || !face.HasText) return null;
        var timer = target.Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(160);
        var step = 0;
        timer.Tick += (_, _) => face.Text = $"{Spinner[step++ % Spinner.Length]} {original}";
        face.Text = $"{Spinner[0]} {original}";
        timer.Start();
        return timer;
    }

    /// <summary>
    /// Touches a control of a page that may be gone (BMB-311 fix round): a disposed or detached control throws, and an end of work that throws leaves the key
    /// taken and the next tap refused. What cannot be touched is not there to be restored.
    /// </summary>
    private static void Guarded(Action touch)
    {
        try { touch(); }
        catch (Exception) { /* the control went with its page */ }
    }

    private static async Task ShowResultAsync(BusyOptions options, bool ok, string text)
    {
        if (options.Result is { } custom)
        {
            try
            {
                custom(ok, text);
                return;
            }
            catch (Exception) when (!ok)
            {
                // The page that wanted the result is gone: a failure is still said, on the page that is shown now.
            }
        }
        else if (options.ResultLabel is { } label && label.Handler is not null)
        {
            label.Text = text;
            label.TextColor = ok ? Colors.Green : Colors.Red;
            label.IsVisible = true;
            return;
        }
        // No place for the result - or its place is gone (a label with no handler belongs to a page that was disposed): a success is not said to
        // nobody, a failure is said in a dialog of the page that is shown now.
        if (ok) return;
        var page = CurrentPage();
        if (page != null) await page.DisplayAlertAsync(options.FailureTitle, text, "OK");
    }

    private static Page? CurrentPage() => Shell.Current?.CurrentPage ?? Application.Current?.Windows.FirstOrDefault()?.Page;

    private static INavigation? CurrentNavigation() => Shell.Current?.Navigation ?? CurrentPage()?.Navigation;

    /// <summary>Closes the pop-up as well as it can: a platform that refuses (the modal stack changed, the page is gone) is not a reason to skip the rest of the clean-up.</summary>
    private static async Task CloseModalQuietlyAsync(INavigation? navigation, Page? modal)
    {
        try { await CloseModalAsync(navigation, modal); }
        catch (Exception) { /* nothing left to close, or nothing that can be closed now */ }
    }

    private static async Task CloseModalAsync(INavigation? navigation, Page? modal)
    {
        if (navigation == null || modal == null) return;
        // The pop-up goes now when it is the one on top, and the moment it is shown again when something was pushed over it (an old pop-up is never
        // left standing under the page that came after it).
        if (modal is BusyPage popup)
        {
            await popup.DismissAsync();
            return;
        }
        if (navigation.ModalStack.LastOrDefault() == modal) await navigation.PopModalAsync(false);
    }

    /// <summary>The caption and the on/off switch of whatever was tapped, whatever its type.</summary>
    private readonly struct Face
    {
        private readonly Button? _button;
        private readonly MenuItem? _item;
        private readonly VisualElement? _element;

        private Face(Button? button, MenuItem? item, VisualElement? element)
        {
            _button = button;
            _item = item;
            _element = element;
        }

        public static Face Of(BindableObject? target) => target switch
        {
            Button b => new Face(b, null, null),
            MenuItem m => new Face(null, m, null),
            VisualElement v => new Face(null, null, v),
            _ => default,
        };

        public bool HasText => _button != null || _item != null;

        public string? Text
        {
            get => _button?.Text ?? _item?.Text;
            set
            {
                if (_button != null) _button.Text = value;
                else if (_item != null) _item.Text = value;
            }
        }

        public bool Enabled
        {
            get => _button?.IsEnabled ?? _item?.IsEnabled ?? _element?.IsEnabled ?? true;
            set
            {
                if (_button != null) _button.IsEnabled = value;
                else if (_item != null) _item.IsEnabled = value;
                else if (_element != null) _element.IsEnabled = value;
            }
        }
    }

    /// <summary>What the pop-up of a long operation lets the work change.</summary>
    internal interface IBusyView
    {
        void Update(double? fraction, string? step);
    }

    /// <summary>The pop-up of a long operation: a loader and a line, no way out but the end of the work (the back button does nothing).</summary>
    private sealed class BusyPage : ContentPage, IBusyView
    {
        private readonly ProgressBar _bar = new() { IsVisible = false };
        private readonly Label _step = new() { IsVisible = false, HorizontalTextAlignment = TextAlignment.Center };

        public BusyPage(string title, string message)
        {
            Title = title;
            Content = new VerticalStackLayout
            {
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Center,
                Padding = new Thickness(32),
                Spacing = 16,
                Children =
                {
                    new ActivityIndicator { IsRunning = true, HeightRequest = 56, WidthRequest = 56, HorizontalOptions = LayoutOptions.Center },
                    new Label { Text = title, FontSize = 20, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center },
                    new Label { Text = message, HorizontalTextAlignment = TextAlignment.Center },
                    _bar,
                    _step,
                },
            };
            AutomationId = "BusyPage";
        }

        private bool _dismissed;

        /// <summary>Closes the pop-up when it is on top; otherwise it closes itself the next time it is shown (<see cref="OnAppearing"/>).</summary>
        public async Task DismissAsync()
        {
            _dismissed = true;
            await PopIfOnTopAsync();
        }

        private async Task PopIfOnTopAsync()
        {
            try
            {
                if (Navigation.ModalStack.LastOrDefault() == this) await Navigation.PopModalAsync(false);
            }
            catch (InvalidOperationException)
            {
                // the stack changed under us (the activity was re-created): there is nothing of this pop-up left to close
            }
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            if (_dismissed) await PopIfOnTopAsync();
        }

        public void Update(double? fraction, string? step)
        {
            if (fraction is { } f)
            {
                _bar.IsVisible = true;
                _bar.Progress = Math.Clamp(f, 0, 1);
            }
            if (step != null)
            {
                _step.Text = step;
                _step.IsVisible = true;
            }
        }

        protected override bool OnBackButtonPressed() => true;
    }
}
