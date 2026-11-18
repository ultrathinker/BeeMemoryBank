using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Mobile.Controls;
using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Mobile.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Pages;

public partial class InitialSyncPage : ContentPage
{
    private readonly IServiceProvider _services;
    private readonly IHttpClientFactory _httpClientFactory;
    private CancellationTokenSource? _cts;
    private bool _running;
    private int _eventsApplied;

    public InitialSyncPage(IServiceProvider services, IHttpClientFactory httpClientFactory)
    {
        InitializeComponent();
        _services = services;
        _httpClientFactory = httpClientFactory;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!_running) _ = RunAsync();
    }

    private async Task RunAsync()
    {
        _running = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Spinner.IsVisible = true;
        Spinner.IsRunning = true;
        RetryButton.IsVisible = false;
        ErrorLabel.IsVisible = false;
        CounterLabel.Text = AppStrings.Get("InitialSyncConnecting");
        _eventsApplied = 0;

        try
        {
            using var scope = _services.CreateScope();
            var sp = scope.ServiceProvider;

            var whitelist = sp.GetRequiredService<IWhitelistRepository>();
            var nodeRepo = sp.GetRequiredService<INodeIdentityRepository>();

            // Every peer that has a way to be reached now (its address, what it announced at the join, an address typed here), not only those whose row
            // has an address.
            var peers = (await PhonePeers.CallableAsync(sp, await whitelist.GetAllActiveAsync())).ToList();

            if (peers.Count == 0)
            {
                // Nothing to sync against — consider initial sync trivially complete
                // so the user isn't trapped behind a gate with no way forward.
                await nodeRepo.MarkInitialSyncCompletedAsync();
                await FinishAndGoToStatusAsync();
                return;
            }

            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(5);

            // Keep pulling in rounds until every peer reports zero new events in a
            // full pass. This handles the case where peer A's events reference peer
            // B's events that arrive later.
            while (!token.IsCancellationRequested)
            {
                int roundTotal = 0;
                foreach (var peer in peers)
                {
                    if (token.IsCancellationRequested) break;
                    CounterLabel.Text = AppStrings.Format("InitialSyncDownloadingFormat", peer.DisplayName, _eventsApplied);
                    var applied = await PeerSessions.SyncWithPeerAsync(sp, http, peer, token);
                    _eventsApplied += applied;
                    roundTotal += applied;
                    CounterLabel.Text = AppStrings.Format("InitialSyncDownloadedFormat", _eventsApplied);
                }

                if (roundTotal == 0) break;
            }

            if (token.IsCancellationRequested) return;

            await nodeRepo.MarkInitialSyncCompletedAsync();
            await FinishAndGoToStatusAsync();
        }
        catch (OperationCanceledException)
        {
            // Ignore — cancel path handled elsewhere
        }
        catch (Exception ex)
        {
            Spinner.IsRunning = false;
            Spinner.IsVisible = false;
            ErrorLabel.Text = AppStrings.Format("InitialSyncFailedFormat", PeerContactLog.Describe(ex));
            ErrorLabel.IsVisible = true;
            RetryButton.IsVisible = true;
            CounterLabel.Text = AppStrings.Format("InitialSyncBeforeErrorFormat", _eventsApplied);
        }
        finally
        {
            _running = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task FinishAndGoToStatusAsync()
    {
        _services.GetRequiredService<PostUnlockRouter>().MarkInitialSyncCompleted();
        App.StartSyncService();
        await Shell.Current.GoToAsync("//status");
    }

    private async void OnRetryClicked(object? sender, EventArgs e)
    {
        if (_running) return;
        // The one busy helper (BMB-311): the button is off and spins; this page is itself the pop-up of the long download (spinner, counter,
        // error line), so no second one is put over it.
        await BusyAction.RunAsync(sender as Button, RunAsync);
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        bool confirmed = await AppDialog.ConfirmAsync(this, AppStrings.Get("CancelDeleteTitle"),
            AppStrings.Get("CancelDeleteDesc"), AppStrings.Get("Delete"), AppStrings.Get("CancelDeleteKeep"), danger: true);
        if (!confirmed) return;

        _cts?.Cancel();
        // A long operation (BMB-311): the one busy helper puts up the pop-up until the app restarts; a failure is said in full.
        await BusyAction.RunAsync(sender as Button, WipeAndResetAsync, new BusyOptions
        {
            Long = true,
            Title = AppStrings.Get("DeletingDeviceTitle"),
            Message = AppStrings.Get("DeletingDeviceMessage"),
        });
    }

    private async Task WipeAndResetAsync()
    {
        App.StopSyncService();
        _services.GetService<IBiometricService>()?.Clear();

        // The whole vault file set: on an encrypted phone the first sync runs after the encryption, and deleting the database alone left the boot
        // store behind - the next start was a dead end. A file that cannot go is said by the busy helper, not ignored.
        await PhoneWipe.ReturnToPlainAsync(_services);

#if ANDROID
        var intent = Android.App.Application.Context.PackageManager!
            .GetLaunchIntentForPackage(Android.App.Application.Context.PackageName!)!;
        intent.AddFlags(Android.Content.ActivityFlags.ClearTop | Android.Content.ActivityFlags.NewTask);
        Android.App.Application.Context.StartActivity(intent);
        Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
#else
        await Shell.Current.GoToAsync("//setup");
#endif
    }

    protected override bool OnBackButtonPressed() => true;
}
