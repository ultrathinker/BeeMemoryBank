using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Mobile.Services;
using BeeMemoryBank.Sync;
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
        CounterLabel.Text = "Connecting…";
        _eventsApplied = 0;

        try
        {
            using var scope = _services.CreateScope();
            var sp = scope.ServiceProvider;

            var whitelist = sp.GetRequiredService<IWhitelistRepository>();
            var syncClient = sp.GetRequiredService<SyncClient>();
            var nodeRepo = sp.GetRequiredService<INodeIdentityRepository>();

            var peers = (await whitelist.GetAllActiveAsync())
                .Where(n => !string.IsNullOrEmpty(n.ApiAddress))
                .ToList();

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
                    CounterLabel.Text = $"Downloading from {peer.DisplayName}… ({_eventsApplied} events)";
                    var applied = await syncClient.SyncWithAsync(http, peer.ApiAddress!, token);
                    _eventsApplied += applied;
                    roundTotal += applied;
                    CounterLabel.Text = $"Downloaded {_eventsApplied} events";
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
            ErrorLabel.Text = $"Sync failed: {ex.Message}";
            ErrorLabel.IsVisible = true;
            RetryButton.IsVisible = true;
            CounterLabel.Text = $"{_eventsApplied} events downloaded before error";
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
        await RunAsync();
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        bool confirmed = await DisplayAlert(
            "Cancel and delete",
            "This will DELETE this node and all data downloaded so far. You'll have to set up again. Continue?",
            "Delete", "Keep trying");
        if (!confirmed) return;

        _cts?.Cancel();
        await WipeAndResetAsync();
    }

    private async Task WipeAndResetAsync()
    {
        App.StopSyncService();
        _services.GetService<IBiometricService>()?.Clear();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "beememorybank.db");
        if (File.Exists(dbPath))
        {
            try { File.Delete(dbPath); } catch { }
        }

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
