using BeeMemoryBank.Busy;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindMobile.Pages;

/// <summary>
/// The start page of the blind app: one card per account (a copy of one source's data), the device line, "Add account", the repair actions when the list of accounts and the disk disagree, and the limits of
/// this phone over every account. The page holds no logic and builds no sentence: every line is a field of <see cref="BlindAccountsView"/> / <see cref="BlindAccountCard"/>, built by the shared AppCore from one
/// snapshot of <see cref="IBlindAccountsController"/>. While it is open it asks the hubs about invitations that wait for their owner (<see cref="BlindInvitationWatch"/>). Every callback is guarded: a failure
/// becomes a sentence on the screen, never an unhandled exception on the UI thread.
/// </summary>
public partial class BlindAccountsPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly BlindDevice _device;
    private readonly Dictionary<string, CardControls> _cards = new(StringComparer.Ordinal);
    private IDispatcherTimer? _timer;
    private BlindInvitationWatch? _watch;
    private bool _refreshPending;
    private string _repairsSignature = "";

    public BlindAccountsPage(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        _device = services.GetRequiredService<BlindDevice>();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            _device.Accounts.Changed += OnChanged;

            // Opens the registry and starts every account; a damaged registry or a folder in use by another copy is a sentence in the snapshot, not an exception.
            await _device.StartAsync();
            FillDeviceForm();
            Refresh();

            if (_timer is null)
            {
                _timer = Dispatcher.CreateTimer();
                _timer.Interval = TimeSpan.FromSeconds(5);
                _timer.Tick += (_, _) => Refresh();
            }
            _timer.Start();

            // An invitation that waits for its owner is asked about every 15 seconds while this page is open (the friend is reading the code to him now).
            _watch ??= new BlindInvitationWatch(_device.Accounts);
            _watch.Start();
        }
        catch (Exception)
        {
            ShowProblem();
        }
    }

    protected override void OnDisappearing()
    {
        _device.Accounts.Changed -= OnChanged;
        _timer?.Stop();
        if (_watch is { } watch) _ = StopWatchAsync(watch);
        base.OnDisappearing();
    }

    private static async Task StopWatchAsync(BlindInvitationWatch watch)
    {
        try { await watch.StopAsync(); }
        catch (Exception) { /* it ends by itself; nothing is left to tell */ }
    }

    /// <summary>The host says something changed (any thread): one refresh on the UI thread, however many events come.</summary>
    private void OnChanged()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _refreshPending = false;
            Refresh();
        });
    }

    /// <summary>Reads the accounts again and shows them. Never throws: the 5-second timer and every handler call it.</summary>
    private void Refresh()
    {
        try
        {
            var view = BlindAccountsView.Build(_device.Accounts.GetSnapshot(), TimeZoneInfo.Local, DateTimeOffset.UtcNow);
            Show(view);
        }
        catch (Exception)
        {
            ShowProblem();
        }
    }

    private void Show(BlindAccountsView view)
    {
        ProblemLabel.Text = view.Problem;
        ProblemLabel.IsVisible = view.Problem is not null;
        DeviceLabel.Text = view.DeviceLine;
        DeviceLabel.IsVisible = view.Problem is null;

        ShowRepairs(view);
        ShowCards(view);
        EmptyLabel.IsVisible = view.Problem is null && view.Cards.Count == 0 && view.Repairs is not { Count: > 0 };

        AddButton.IsEnabled = view.CanAdd;
        AddHintLabel.Text = view.AddHint;
        AddHintLabel.IsVisible = view.AddHint.Length > 0;

        DeviceUsageLabel.Text = view.DeviceUsageProblem;
        DeviceUsageLabel.IsVisible = view.DeviceUsageProblem is not null;
        NewCountsButton.IsVisible = view.CanRepairDeviceUsage;

        // The scheduler's own text is built from an exception's message: it is not shown, only that there was a problem.
        ProblemLogLabel.Text = _device.LastProblem is not null ? BlindPageSentences.BackgroundProblem : null;
        ProblemLogLabel.IsVisible = ProblemLogLabel.Text is not null;
    }

    // ---- the cards -------------------------------------------------------------------------------------------------------------------------

    private void ShowCards(BlindAccountsView view)
    {
        var ids = view.Cards.Select(c => c.Id).ToList();
        if (!ids.SequenceEqual(_cards.Keys))
        {
            // The list changed (an account was added or removed): the cards are made again; otherwise they are updated where they stand, so that a tap is never lost to a refresh.
            CardsHost.Children.Clear();
            _cards.Clear();
            foreach (var card in view.Cards)
            {
                var controls = MakeCard(card.Id);
                _cards[card.Id] = controls;
                CardsHost.Children.Add(controls.Frame);
            }
        }
        foreach (var card in view.Cards) _cards[card.Id].Update(card);
    }

    private CardControls MakeCard(string id)
    {
        var controls = new CardControls();
        var tap = new TapGestureRecognizer();
        // busy-exempt: only opens the page of the account, nothing is sent
        tap.Tapped += async (_, _) => await OpenAccountAsync(id);
        controls.Frame.GestureRecognizers.Add(tap);
        controls.Frame.AutomationId = "account-" + id;
        controls.RetryButton.Clicked += (sender, _) => OnRetryRemovalClicked(sender as Button, id);
        return controls;
    }

    /// <summary>
    /// A removal that could not be finished (the Keystore or the phone's storage refused something) leaves the account listed as being removed, with the reason on its card; the host runs it again at the next
    /// start, and this button runs it again now. The account leaves the list only when every key, preference and file of it is confirmed gone.
    /// </summary>
    private async void OnRetryRemovalClicked(Button? button, string id)
    {
        await BusyAction.RunAsync(button, async () =>
        {
            await _device.Accounts.RemoveAsync(id);
            Refresh();
        }, new BusyOptions
        {
            Long = true,
            Title = "Removing the copy",
            Message = "Please wait while the keys, the state and the files of this copy are removed.",
            FailureTitle = "Remove this account",
        });
        Refresh();
    }

    private async Task OpenAccountAsync(string id)
    {
        try
        {
            await Navigation.PushAsync(new BlindHomePage(_services, id));
        }
        catch (KeyNotFoundException)
        {
            // Removed since the card was drawn (or being removed): the next refresh drops the card.
            Refresh();
        }
        catch (Exception ex)
        {
            await TellAsync("Blind copies", BusyAction.Describe(ex));
        }
    }

    private sealed class CardControls
    {
        public Frame Frame { get; } = new() { CornerRadius = 8, Padding = 16, HasShadow = false };
        private readonly Label _name = new() { FontAttributes = FontAttributes.Bold, FontSize = 16 };
        private readonly Label _host = new() { FontSize = 12 };
        private readonly Label _state = new() { LineBreakMode = LineBreakMode.WordWrap };
        private readonly Label _detail = new() { FontSize = 12, LineBreakMode = LineBreakMode.WordWrap };

        /// <summary>Shown while the account is being removed and the removal did not finish: runs it again.</summary>
        public Button RetryButton { get; } = new() { Text = "Try removing it again", IsVisible = false };

        public CardControls()
        {
            Frame.BackgroundColor = Colour("SurfaceColor");
            Frame.BorderColor = Colour("BorderColor");
            _host.TextColor = Colour("SecondaryTextColor");
            _detail.TextColor = Colour("SecondaryTextColor");
            Frame.Content = new VerticalStackLayout { Spacing = 4, Children = { _name, _host, _state, _detail, RetryButton } };
        }

        public void Update(BlindAccountCard card)
        {
            _name.Text = card.Name;
            _host.Text = card.SourceHost;
            _host.IsVisible = card.SourceHost.Length > 0;
            _state.Text = card.State;
            _state.TextColor = card.Kind switch
            {
                BlindCardKind.Ok => Colour("SuccessColor"),
                BlindCardKind.Paused => Colour("WarningColor"),
                BlindCardKind.Problem => Colour("ErrorColor"),
                BlindCardKind.Removing => Colour("SecondaryTextColor"),
                _ => Colour("TextColor"),
            };
            _detail.Text = string.Join(Environment.NewLine, new[] { card.Stored, card.Today, card.LastSync }.Where(line => line.Length > 0));
            RetryButton.IsVisible = !card.CanEdit;
        }

        private static Color Colour(string key) =>
            Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color ? color : Colors.Gray;
    }

    // ---- repairs ---------------------------------------------------------------------------------------------------------------------------

    /// <summary>Each difference between the list of accounts and the disk, with the one explicit action that settles it. The app settles none of them on its own.</summary>
    private void ShowRepairs(BlindAccountsView view)
    {
        var repairs = view.Repairs ?? [];
        var signature = string.Join("|", repairs.Select(r => $"{r.Kind}:{r.AccountId}"));
        if (signature == _repairsSignature) return;
        _repairsSignature = signature;

        RepairsHost.Children.Clear();
        foreach (var issue in repairs)
        {
            var box = new VerticalStackLayout { Spacing = 6 };
            box.Children.Add(new Label { Text = issue.Description, LineBreakMode = LineBreakMode.WordWrap });
            if (issue.CanAdopt)
            {
                var adopt = new Button { Text = "Use it as an account" };
                adopt.Clicked += (sender, _) => OnAdoptClicked(sender as Button, issue);
                box.Children.Add(adopt);
            }
            if (issue.CanForget)
            {
                var forget = new Button { Text = "Forget this account" };
                forget.Clicked += (sender, _) => OnForgetClicked(sender as Button, issue);
                box.Children.Add(forget);
            }
            RepairsHost.Children.Add(new Frame
            {
                BorderColor = Application.Current?.Resources.TryGetValue("WarningColor", out var warning) == true && warning is Color c ? c : Colors.Orange,
                BackgroundColor = Application.Current?.Resources.TryGetValue("SurfaceColor", out var surface) == true && surface is Color s ? s : Colors.Black,
                CornerRadius = 8, Padding = 16, HasShadow = false, Content = box,
            });
        }
    }

    private async void OnAdoptClicked(Button? button, BlindRepairIssue issue)
    {
        await BusyAction.RunAsync(button, async () =>
        {
            var problem = issue.Kind == BlindRepairKinds.LegacyRootNotListed
                ? await _device.Accounts.AdoptLegacyRootAsync()
                : await _device.Accounts.AdoptFolderAsync(issue.AccountId ?? "");
            if (problem is not null) throw BlindPageSentences.Refusal(problem);
            _repairsSignature = "";
            Refresh();
        }, new BusyOptions { FailureTitle = "Use it as an account" });
    }

    private async void OnForgetClicked(Button? button, BlindRepairIssue issue)
    {
        try
        {
            if (!await DisplayAlertAsync("Forget this account",
                    "This takes the account out of the list and removes the keys and the state this phone kept for it. Nothing else is touched. Do this only when its folder is really gone.",
                    "Forget", "Keep it"))
                return;
            await BusyAction.RunAsync(button, async () =>
            {
                var problem = await _device.Accounts.ForgetMissingAccountAsync(issue.AccountId ?? "");
                if (problem is not null) throw BlindPageSentences.Refusal(problem);
                _repairsSignature = "";
                Refresh();
            }, new BusyOptions { FailureTitle = "Forget this account" });
        }
        catch (Exception ex)
        {
            await TellAsync("Forget this account", BusyAction.Describe(ex));
        }
    }

    // ---- add -------------------------------------------------------------------------------------------------------------------------------

    // busy-exempt: only opens the Add page, nothing is sent
    private async void OnAddClicked(object? sender, EventArgs e)
    {
        try
        {
            await Navigation.PushAsync(new BlindAddPage(_services));
        }
        catch (Exception ex)
        {
            await TellAsync("Add account", BusyAction.Describe(ex));
        }
    }

    // ---- the limits of this phone ----------------------------------------------------------------------------------------------------------

    private void FillDeviceForm()
    {
        var form = BlindDeviceLimitsForm.From(_device.Accounts.GetSnapshot().Device);
        DeviceStoredEntry.Text = form.Stored;
        DeviceTrafficEntry.Text = form.TrafficPerDay;
        DeviceFormLabel.IsVisible = false;
    }

    private async void OnSaveDeviceLimitsClicked(object? sender, EventArgs e)
    {
        await BusyAction.RunAsync(sender as Button, () =>
        {
            var (limits, problems) = new BlindDeviceLimitsForm(DeviceStoredEntry.Text ?? "", DeviceTrafficEntry.Text ?? "").Validate();
            if (limits is null)
            {
                DeviceFormLabel.Text = string.Join(Environment.NewLine, problems);
                DeviceFormLabel.IsVisible = true;
                return Task.CompletedTask;
            }
            var refused = _device.Accounts.SetDeviceLimits(limits);
            DeviceFormLabel.Text = refused;
            DeviceFormLabel.IsVisible = refused is not null;
            if (refused is null) FillDeviceForm();
            Refresh();
            return Task.CompletedTask;
        }, new BusyOptions { FailureTitle = "Save phone limits" });
    }

    private async void OnStartNewCountsClicked(object? sender, EventArgs e)
    {
        await BusyAction.RunAsync(sender as Button, () =>
        {
            var refused = _device.Accounts.StartNewDeviceUsageLedger();
            if (refused is not null) throw BlindPageSentences.Refusal(refused);
            Refresh();
            return Task.CompletedTask;
        }, new BusyOptions { FailureTitle = "Start new counts" });
    }

    private void ShowProblem()
    {
        try
        {
            ProblemLabel.Text = BlindPageSentences.DeviceUnreadable;
            ProblemLabel.IsVisible = true;
        }
        catch (Exception)
        {
            // the controls are gone (the page is closing); nothing is left to tell
        }
    }

    private async Task TellAsync(string title, string message)
    {
        try { await DisplayAlertAsync(title, message, "OK"); }
        catch (Exception) { /* the page is closing */ }
    }
}
