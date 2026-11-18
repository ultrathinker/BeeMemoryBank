using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Mobile.Controls;
using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Mobile.Services;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;
#if ANDROID
using BeeMemoryBank.Mobile.Platforms.Android;
#endif

namespace BeeMemoryBank.Mobile.Pages;

public record ActivityItem(string Summary, string TimeAgo);

/// <summary>
/// The mock-up's Sync screen, built from the working logic the old Status page had: the sync state,
/// the invisible mode, the peers and their addresses, sync now, the recent events and the background
/// sync pulse. The "waits for approval" card is the old "Action Required" state — the phone knows no
/// way to reach a peer (<see cref="SyncStatusService.IsRegisteredOnServer"/> is false) — and it shows
/// the device's real name and the short id the Admin page shows, not an invented code.
/// </summary>
public partial class StatusPage : ContentPage
{
    private readonly SyncStatusService _statusSvc;
    private readonly IServiceProvider _services;
    private bool _loading;
    private List<PeerCard> _peers = [];

    public StatusPage(SyncStatusService statusSvc, IServiceProvider services)
    {
        InitializeComponent();
        _statusSvc = statusSvc;
        _services = services;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
        _services.GetRequiredService<SyncNotificationService>().ClearPendingUpdates();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _statusSvc.CancelSync();
        _statusSvc.StopPolling();
    }

    private static bool IsOffline()
    {
        var access = Connectivity.Current.NetworkAccess;
        return access != NetworkAccess.Internet && access != NetworkAccess.ConstrainedInternet;
    }

    private async Task LoadAsync()
    {
        await Task.Run(() => _statusSvc.RefreshAsync());
        _statusSvc.StartPolling();

        // The approval state takes over the whole screen, exactly as the mock-up draws it.
        var registered = _statusSvc.IsRegisteredOnServer;
        PendingCard.IsVisible = !registered;
        NormalScreen.IsVisible = registered;
        if (!registered)
        {
            PendingDeviceName.Text = _statusSvc.NodeName;
            PendingDeviceId.Text = _statusSvc.NodeIdShort;
            return;
        }

        var offline = IsOffline();
        // Real failures in red, a computer that is simply away in yellow, each in the app's own words.
        var disconnected = _statusSvc.LastProblemCode == SyncStatusService.ProblemDisconnected;
        var error = _statusSvc.LastProblemCode is not null && !disconnected;
        OfflineBanner.IsVisible = offline;
        ErrorBanner.IsVisible = error;
        DisconnectedBanner.IsVisible = disconnected;
        if (error) ErrorText.Text = Controls.SyncText.Problem(_statusSvc.LastProblemCode, "") ?? "";
        UnreachableBanner.IsVisible = !error && _statusSvc.LastUnreachable > 0;
        UnreachableText.Text = _statusSvc.LastUnreachable > 0 ? Controls.SyncText.Unreachable(_statusSvc.LastUnreachablePeer ?? "") : "";

        // Self-heal the ingest key (a transient Keystore failure at first setup left background backup
        // dead); a banner only if it still isn't armed afterwards, so the failure is never silent.
        var enroller = _services.GetRequiredService<IngestKeyEnroller>();
        await enroller.TryEnrollAsync();
        BgWarningBanner.IsVisible = !enroller.IsArmed();

        UpdateStateCard(offline, error);
        UpdateInvisibleSwitch();
        UpdateBackgroundSyncValue();
        await LoadPeersAsync();
        await LoadActivityAsync();
    }

    private void UpdateStateCard(bool offline, bool error)
    {
        string pillKey, foreground, border, background;
        if (offline) { pillKey = "SyncOfflineTitle"; foreground = "TextMuted"; border = "Border"; background = "Surface"; }
        else if (error) { pillKey = "SyncStateError"; foreground = "Error"; border = "ErrorBorder"; background = "ErrorBackground"; }
        else if (_statusSvc.LastProblemCode == SyncStatusService.ProblemDisconnected) { pillKey = "SyncStateDisconnected"; foreground = "TextMuted"; border = "Border"; background = "Surface"; }
        else if (_statusSvc.LastUnreachable > 0) { pillKey = "SyncStateUnreachable"; foreground = "Warning"; border = "WarningBorder"; background = "WarningBackground"; }
        else if (_statusSvc.IsInvisible) { pillKey = "SyncStateInvisible"; foreground = "TextMuted"; border = "Border"; background = "Surface"; }
        else { pillKey = "SyncStateReady"; foreground = "Success"; border = "SuccessBorder"; background = "SuccessBackground"; }

        StatePillLabel.Text = AppStrings.Get(pillKey);
        StatePillLabel.TextColor = ThemeManager.Token(foreground);
        StatePill.BackgroundColor = ThemeManager.Token(background);
        StatePill.Stroke = ThemeManager.Token(border);

        LastSyncRow.Label = AppStrings.Get(error ? "SyncLastSuccessSync" : "SyncLastSync");
        LastSyncValue.Text = _statusSvc.LastSyncTime == default
            ? AppStrings.Get("SyncNever")
            : ContentFormat.FullDate(_statusSvc.LastSyncTime);

        // The mock-up disables "sync now" only when there is no network.
        SyncNowButton.IsEnabled = !offline;
    }

    private void UpdateInvisibleSwitch()
    {
        _loading = true;
        InvisibleSwitch.IsToggled = _statusSvc.IsInvisible;
        _loading = false;
    }

    private void UpdateBackgroundSyncValue()
    {
        var (lastCycleUtc, _, _, _) = SyncHeartbeat.Read();
        BgSyncValue.Text = lastCycleUtc == null
            ? AppStrings.Get("SyncBgNever")
            : AppStrings.Format("SyncBgLastRunFormat", TimeAgo(lastCycleUtc.Value));
    }

    private void OnInvisibleToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        _statusSvc.IsInvisible = e.Value;
        UpdateStateCard(IsOffline(), _statusSvc.LastProblemCode is not null && _statusSvc.LastProblemCode != SyncStatusService.ProblemDisconnected);
    }

    // ---- devices ----

    private async Task LoadPeersAsync()
    {
        try
        {
            var peers = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
                var identity = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
                var local = await identity.GetAsync();
                var all = await whitelist.GetAllActiveAsync();
                // Defense-in-depth fail-closed filter: if identity read fails, show nothing rather
                // than risk leaking the self-row (self never lives in tbl_whitelist).
                if (local == null) return new List<PeerCard>();

#if ANDROID
                var interval = TimeSpan.FromMinutes(SyncForegroundService.IsRunning ? 5 : 15);
#else
                var interval = TimeSpan.FromMinutes(15);
#endif
                var (lastCycleUtc, _, _, _) = SyncHeartbeat.Read();
                var ownCycleRecent = lastCycleUtc is { } at && DateTime.UtcNow - at < TimeSpan.FromMinutes(20);

                var channels = scope.ServiceProvider.GetRequiredService<PeerChannelService>();
                var cards = new List<PeerCard>();
                foreach (var peer in all.Where(n => n.NodeId != local.NodeId))
                    cards.Add(await channels.CardAsync(peer, interval, ownCycleRecent));
                return cards;
            });
            _peers = peers;
            DevicesList.Children.Clear();
            NoDevicesLabel.IsVisible = peers.Count == 0;
            foreach (var card in peers)
                DevicesList.Children.Add(BuildDeviceRow(card));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadPeers error: {ex.Message}");
        }
    }

    private View BuildDeviceRow(PeerCard card)
    {
        var resources = Application.Current?.Resources;
        var theme = ThemeManager.Effective == AppTheme.Light ? "light" : "dark";
        var field = new VerticalStackLayout { Spacing = 2 };
        field.Children.Add(new Label { Text = card.DisplayName, Style = (Style?)resources?["ItemTitle"] });
        // The reachability line in the app's own words; a computer that is simply away in yellow.
        if (card.ReachState is { } state)
        {
            var away = Controls.SyncText.IsUnreachableState(state);
            var text = away ? Controls.SyncText.Unreachable(card.Entry.DisplayName) : Controls.SyncText.Reachability(state, card.Entry.DisplayName);
            if (text is not null)
                field.Children.Add(new Label
                {
                    Text = text,
                    Style = (Style?)resources?["ItemMeta"],
                    TextColor = ThemeManager.Token(away ? "Warning" : "TextSecondary"),
                });
        }
        field.Children.Add(new Label
        {
            Text = card.AddressLine,
            FontFamily = "monospace",
            FontSize = 12,
            LineBreakMode = LineBreakMode.TailTruncation,
            TextColor = ThemeManager.Token("TextSecondary"),
        });

        var grid = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)], ColumnSpacing = 8 };
        grid.Children.Add(field);
        Grid.SetColumn(field, 0);
        var chevron = new Image { Source = $"icon_row_chevron_{theme}.svg", WidthRequest = 18, HeightRequest = 18, VerticalOptions = LayoutOptions.Center };
        grid.Children.Add(chevron);
        Grid.SetColumn(chevron, 1);

        var row = new Border
        {
            BackgroundColor = ThemeManager.Token("Surface"),
            Stroke = ThemeManager.Token("Border"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(12) },
            Padding = new Thickness(12),
            Content = grid,
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await ShowDeviceSheetAsync(card);
        row.GestureRecognizers.Add(tap);
        return row;
    }

    /// <summary>The mock-up's device sheet: change the address, remove an address typed here, or remove the device.</summary>
    private async Task ShowDeviceSheetAsync(PeerCard card)
    {
        var chosen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answered = false;
        var resources = Application.Current?.Resources;
        var stack = new VerticalStackLayout { Spacing = 8 };
        stack.Children.Add(new Label { Text = card.AddressLine, FontFamily = "monospace", FontSize = 12, TextColor = ThemeManager.Token("TextSecondary") });
        BottomSheet.BottomSheetPage? sheet = null;

        void Add(string key, string styleKey, string action)
        {
            var button = new Button { Text = AppStrings.Get(key), Style = (Style?)resources?[styleKey] };
            button.Clicked += async (_, _) =>
            {
                // Close the sheet FIRST, then answer: the address work pushes another modal right after.
                answered = true;
                if (sheet != null) await sheet.CloseAsync();
                chosen.TrySetResult(action);
            };
            stack.Children.Add(button);
        }

        Add("DeviceChangeAddress", "SecondaryButton", "change");
        Add("DeviceRemoveAddress", "SecondaryButton", "removeaddr");
        Add("DeviceRemoveDevice", "DangerButton", "removedev");
        var cancel = new Button { Text = AppStrings.Get("Cancel"), Style = (Style?)resources?["TextButton"], HorizontalOptions = LayoutOptions.Fill };
        cancel.Clicked += async (_, _) =>
        {
            answered = true;
            if (sheet != null) await sheet.CloseAsync();
            chosen.TrySetResult("none");
        };
        stack.Children.Add(cancel);

        sheet = new BottomSheet.BottomSheetPage(stack, card.DisplayName);
        sheet.Disappearing += (_, _) => { if (!answered) chosen.TrySetResult("none"); };
        await Navigation.PushModalAsync(sheet);
        switch (await chosen.Task)
        {
            case "change": await ChangeAddressForPeerAsync(card.Entry); break;
            case "removeaddr": await RemoveChannelForPeerAsync(card); break;
            case "removedev": await RevokePeerAsync(card.Entry); break;
        }
    }

    // busy-exempt: opens the device sheet; the address work behind its buttons runs through BusyAction below
    private async void OnAddAddressClicked(object? sender, EventArgs e)
    {
        if (_peers.Count == 0)
        {
            await AppDialog.AlertAsync(this, AppStrings.Get("SyncDevicesCard"), AppStrings.Get("SyncNoDevices"));
            return;
        }
        var entry = _peers.Count == 1 ? _peers[0].Entry : await PickDeviceAsync(_peers);
        if (entry != null) await AddAddressForPeerAsync(entry);
    }

    private async Task<WhitelistEntry?> PickDeviceAsync(List<PeerCard> peers)
    {
        var chosen = new TaskCompletionSource<WhitelistEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answered = false;
        var resources = Application.Current?.Resources;
        var stack = new VerticalStackLayout { Spacing = 8 };
        BottomSheet.BottomSheetPage? sheet = null;
        foreach (var peer in peers)
        {
            var button = new Button { Text = peer.DisplayName, Style = (Style?)resources?["SecondaryButton"] };
            button.Clicked += async (_, _) =>
            {
                // Close the sheet FIRST, then answer: the caller acts on the picked device right after.
                answered = true;
                if (sheet != null) await sheet.CloseAsync();
                chosen.TrySetResult(peer.Entry);
            };
            stack.Children.Add(button);
        }
        var cancel = new Button { Text = AppStrings.Get("Cancel"), Style = (Style?)resources?["TextButton"], HorizontalOptions = LayoutOptions.Fill };
        cancel.Clicked += async (_, _) =>
        {
            answered = true;
            if (sheet != null) await sheet.CloseAsync();
            chosen.TrySetResult(null);
        };
        stack.Children.Add(cancel);
        sheet = new BottomSheet.BottomSheetPage(stack, AppStrings.Get("PickDeviceTitle"));
        sheet.Disappearing += (_, _) => { if (!answered) chosen.TrySetResult(null); };
        await Navigation.PushModalAsync(sheet);
        return await chosen.Task;
    }

    private async Task ChangeAddressForPeerAsync(WhitelistEntry entry)
    {
        var newUrl = await this.ShowInputPopupAsync(
            AppStrings.Get("DeviceChangeAddress"),
            AppStrings.Format("ChangeAddressPromptFormat", entry.DisplayName),
            accept: AppStrings.Get("Save"), cancel: AppStrings.Get("Cancel"),
            placeholder: AppStrings.Get("AddressPlaceholder"), initialValue: entry.ApiAddress ?? "");
        if (string.IsNullOrWhiteSpace(newUrl)) return;

        var typed = newUrl;
        await BusyAction.RunAsync(null, async () =>
        {
            // PeerAddressService is the one writer of a peer's address (BMB-307): the same policy, the
            // same check of what answers there, the event, the audit line. Its sentences never quote the
            // address, so none is put in a dialog here.
            var result = await ChangeAddressAsync(entry.NodeId, typed, saveWhenUnanswered: false);
            if (result.Outcome == PeerAddressOutcome.NoAnswer)
            {
                // Nothing answered: warn but allow saving (the address may be filled in before the
                // server is live). The first session that reaches it checks who answers.
                var proceed = await AppDialog.ConfirmAsync(this, AppStrings.Get("WarningTitle"), result.Message,
                    AppStrings.Get("SaveAnyway"), AppStrings.Get("Cancel"));
                if (!proceed) return;
                result = await ChangeAddressAsync(entry.NodeId, typed, saveWhenUnanswered: true);
            }
            if (!result.Succeeded)
            {
                await AppDialog.AlertAsync(this, AppStrings.Get("NotSavedTitle"), result.Message);
                return;
            }
            var unchanged = result.Outcome == PeerAddressOutcome.Unchanged;
            await AppDialog.AlertAsync(this,
                AppStrings.Get(unchanged ? "NothingToChangeTitle" : "AddressAddedTitle"),
                unchanged ? result.Message : AppStrings.Format("AddressUpdatedFormat", entry.DisplayName));
            await LoadPeersAsync();
        }, new BusyOptions { FailureTitle = AppStrings.Get("FailedToUpdateTitle") });
    }

    private async Task AddAddressForPeerAsync(WhitelistEntry entry)
    {
        var typed = await this.ShowInputPopupAsync(
            AppStrings.Get("AddAddressTitle"),
            AppStrings.Format("AddAddressPromptFormat", entry.DisplayName),
            accept: AppStrings.Get("Save"), cancel: AppStrings.Get("Cancel"),
            placeholder: AppStrings.Get("AddressPlaceholder"));
        if (string.IsNullOrWhiteSpace(typed)) return;

        var text = typed;
        await BusyAction.RunAsync(null, async () =>
        {
            // The address is made canonical, then asked who it is with THIS peer's TLS anchor. Its
            // sentences never quote the address, so none is put in a dialog here.
            var result = await AddChannelAsync(entry.NodeId, text, saveWithoutAnswer: false);
            if (result.CanSaveAnyway)
            {
                var proceed = await AppDialog.ConfirmAsync(this, AppStrings.Get("WarningTitle"), result.Message,
                    AppStrings.Get("SaveAnyway"), AppStrings.Get("Cancel"));
                if (!proceed) return;
                result = await AddChannelAsync(entry.NodeId, text, saveWithoutAnswer: true);
            }
            if (!result.Succeeded)
            {
                await AppDialog.AlertAsync(this, AppStrings.Get("NotSavedTitle"), result.Message);
                return;
            }
            await AppDialog.AlertAsync(this, AppStrings.Get("AddressAddedTitle"), result.Message);
            await LoadPeersAsync();
        }, new BusyOptions { FailureTitle = AppStrings.Get("FailedToAddAddressTitle") });
    }

    private async Task RemoveChannelForPeerAsync(PeerCard card)
    {
        var removable = card.Channels.Where(c => c.CanRemove).ToList();
        if (removable.Count == 0)
        {
            await AppDialog.AlertAsync(this, AppStrings.Get("NoLocalAddressTitle"), AppStrings.Get("NoLocalAddressDesc"));
            return;
        }
        var line = removable.Count == 1 ? removable[0] : await PickAddressAsync(removable);
        if (line == null) return;

        var confirmed = await AppDialog.ConfirmAsync(this, AppStrings.Get("DeviceRemoveAddress"),
            AppStrings.Get("RemoveAddressDesc"), AppStrings.Get("Remove"), AppStrings.Get("Cancel"), danger: true);
        if (!confirmed) return;

        await BusyAction.RunAsync(null, async () =>
        {
            using (var scope = _services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<PeerChannelService>().RemoveAsync(line.PeerNodeId, line.Address);
            await LoadPeersAsync();
        }, new BusyOptions { FailureTitle = AppStrings.Get("FailedToRemoveAddressTitle") });
    }

    private async Task<PeerChannelLine?> PickAddressAsync(List<PeerChannelLine> lines)
    {
        var chosen = new TaskCompletionSource<PeerChannelLine?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answered = false;
        var resources = Application.Current?.Resources;
        var stack = new VerticalStackLayout { Spacing = 8 };
        BottomSheet.BottomSheetPage? sheet = null;
        foreach (var line in lines)
        {
            var button = new Button { Text = line.Address, Style = (Style?)resources?["SecondaryButton"] };
            button.Clicked += async (_, _) =>
            {
                // Close the sheet FIRST, then answer: the caller acts on the picked address right after.
                answered = true;
                if (sheet != null) await sheet.CloseAsync();
                chosen.TrySetResult(line);
            };
            stack.Children.Add(button);
        }
        var cancel = new Button { Text = AppStrings.Get("Cancel"), Style = (Style?)resources?["TextButton"], HorizontalOptions = LayoutOptions.Fill };
        cancel.Clicked += async (_, _) =>
        {
            answered = true;
            if (sheet != null) await sheet.CloseAsync();
            chosen.TrySetResult(null);
        };
        stack.Children.Add(cancel);
        sheet = new BottomSheet.BottomSheetPage(stack, AppStrings.Get("PickAddressTitle"));
        sheet.Disappearing += (_, _) => { if (!answered) chosen.TrySetResult(null); };
        await Navigation.PushModalAsync(sheet);
        return await chosen.Task;
    }

    private async Task RevokePeerAsync(WhitelistEntry entry)
    {
        var confirmed = await AppDialog.ConfirmAsync(this, AppStrings.Get("DeviceRemoveDevice"),
            AppStrings.Format("RemoveDeviceDescFormat", entry.DisplayName),
            AppStrings.Get("Delete"), AppStrings.Get("Cancel"), danger: true);
        if (!confirmed) return;

        await BusyAction.RunAsync(null, async () =>
        {
            await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
                var eventLogger = scope.ServiceProvider.GetRequiredService<IEventLogger>();
                // Log first, then stamp the row with that version — see WhitelistEndpoints for why
                // the order matters (a stale add must lose to this revoke).
                var version = await eventLogger.LogWhitelistRevokeAsync(entry.NodeId);
                await whitelist.RevokeAsync(entry.NodeId, version);
            });
            await LoadPeersAsync();
        });
    }

    private async Task<PeerChannelResult> AddChannelAsync(Guid nodeId, string typed, bool saveWithoutAnswer)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PeerChannelService>().AddAsync(nodeId, typed, saveWithoutAnswer);
    }

    private async Task<PeerAddressResult> ChangeAddressAsync(Guid nodeId, string typed, bool saveWhenUnanswered)
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PeerAddressService>().ChangeAsync(nodeId, typed, "phone", "phone", saveWhenUnanswered);
    }

    // ---- recent events ----

    private async Task LoadActivityAsync()
    {
        try
        {
            var items = await Task.Run(async () =>
            {
                using var scope = _services.CreateScope();
                var eventRepo = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
                var events = await eventRepo.GetRecentAsync(5);
                return events.Select(evt => new ActivityItem(GetEventSummary(evt), TimeAgo(evt.CreatedAt))).ToList();
            });
            EventsList.Children.Clear();
            NoEventsLabel.IsVisible = items.Count == 0;
            var resources = Application.Current?.Resources;
            foreach (var item in items)
            {
                var grid = new Grid { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)], ColumnSpacing = 10 };
                var text = new Label { Text = item.Summary, Style = (Style?)resources?["ItemMeta"], TextColor = ThemeManager.Token("Text"), LineBreakMode = LineBreakMode.TailTruncation, VerticalOptions = LayoutOptions.Center };
                var time = new Label { Text = item.TimeAgo, Style = (Style?)resources?["ItemMeta"], VerticalOptions = LayoutOptions.Center };
                grid.Children.Add(text);
                Grid.SetColumn(text, 0);
                grid.Children.Add(time);
                Grid.SetColumn(time, 1);
                EventsList.Children.Add(grid);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadActivity error: {ex.Message}");
        }
    }

    private static string GetEventSummary(SyncEvent evt) => evt.EventType switch
    {
        "article_create" => AppStrings.Get("EventArticleCreated"),
        "article_update" => AppStrings.Get("EventArticleUpdated"),
        "article_delete" => AppStrings.Get("EventArticleDeleted"),
        "comment_create" => AppStrings.Get("EventCommentAdded"),
        "comment_delete" => AppStrings.Get("EventCommentDeleted"),
        "whitelist_add" => AppStrings.Get("EventDeviceAdded"),
        "whitelist_revoke" => AppStrings.Get("EventDeviceRemoved"),
        _ => evt.EventType
    };

    private static string TimeAgo(DateTime utc)
    {
        var diff = DateTime.UtcNow - utc;
        if (diff < TimeSpan.Zero) diff = TimeSpan.Zero;
        if (diff.TotalMinutes < 1) return AppStrings.Get("TimeJustNow");
        if (diff.TotalHours < 1) return AppStrings.Format("TimeMinutesAgoFormat", (int)diff.TotalMinutes);
        if (diff.TotalDays < 1) return AppStrings.Format("TimeHoursAgoFormat", (int)diff.TotalHours);
        if (diff.TotalDays < 7) return AppStrings.Format("TimeDaysAgoFormat", (int)diff.TotalDays);
        return ContentFormat.ShortDate(utc);
    }

    // ---- sync now / check again / refresh ----

    // busy-exempt: copies to the clipboard, nothing is sent and nothing waits
    private async void OnCopyNodeIdClicked(object? sender, EventArgs e)
    {
        await Clipboard.Default.SetTextAsync(_statusSvc.NodeId);
    }

    // busy-exempt: re-reads the local peer channels to see whether the device was approved; nothing is sent
    private async void OnCheckAgainClicked(object? sender, EventArgs e)
    {
        await LoadAsync();
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        // Pulling the screen down asks for a sync round (BMB-311); the pull's own spinner is the pull's.
        try
        {
            await BusyAction.RunAsync(RefreshView, async () =>
            {
                var syncNotify = _services.GetRequiredService<SyncNotificationService>();
                if (!syncNotify.HasPendingUpdates)
                    await _statusSvc.SyncNowAsync();
                await LoadAsync();
                syncNotify.ClearPendingUpdates();
            }, new BusyOptions { FailureTitle = AppStrings.Get("SyncingTitle") });
        }
        finally
        {
            RefreshView.IsRefreshing = false;
        }
    }

    private async void OnSyncNowClicked(object? sender, EventArgs e)
    {
        if (_statusSvc.IsInvisible)
        {
            await AppDialog.AlertAsync(this, AppStrings.Get("SyncInvisibleTitle"), AppStrings.Get("SyncInvisibleTurnOff"));
            return;
        }
        // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up, so a second
        // tap is impossible, and says in full why a sync failed.
        await BusyAction.RunAsync(SyncNowButton, async () =>
        {
            try
            {
                await _statusSvc.SyncNowAsync();
                _services.GetRequiredService<SyncNotificationService>().ClearPendingUpdates();
            }
            finally
            {
                await LoadAsync();
            }
            // A peer that really failed is said in full in the app's own words, never as a pass (BMB-307);
            // a computer that is simply away is not an error - the yellow banner says it.
            var problems = _statusSvc.LastProblems;
            if (problems.Count > 0)
                throw new InvalidOperationException(string.Join("\n", problems.Select(p => Controls.SyncText.Problem(p.Code, p.Name))));
        }, new BusyOptions
        {
            Long = true,
            Title = AppStrings.Get("SyncingTitle"),
            Message = AppStrings.Get("SyncingMessage"),
            FailureTitle = AppStrings.Get("SyncFailedTitle"),
        });
    }
}
