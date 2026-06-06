using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

#if ANDROID
using Android.Content;
using Android.OS;
#endif

namespace BeeMemoryBank.Mobile.Pages;

public partial class SetupPage : ContentPage
{
    private readonly NodeSetupService _setupSvc;
    private readonly SessionService _session;
    private readonly PostUnlockRouter _router;
    private readonly IServiceProvider _services;
    private bool _busy;

    public SetupPage(NodeSetupService setupSvc, SessionService session, PostUnlockRouter router, IServiceProvider services)
    {
        InitializeComponent();
        _setupSvc = setupSvc;
        _session = session;
        _router = router;
        _services = services;
        PrefillDefaults();
    }

    // Pre-fill sensible defaults so the user can just type the password and continue.
    private void PrefillDefaults()
    {
        if (string.IsNullOrWhiteSpace(NameEntry.Text))
        {
            var deviceName = Microsoft.Maui.Devices.DeviceInfo.Current.Name;
            NameEntry.Text = string.IsNullOrWhiteSpace(deviceName) ? "My Node" : deviceName;
        }
        // Partial URL on purpose — the secret production domain is NOT hard-coded in source
        // (keeps it out of the public repo / pre-push secret hook); the user completes it.
        if (string.IsNullOrWhiteSpace(ServerUrlEntry.Text))
            ServerUrlEntry.Text = "https://beememorybank.";
    }

    private async void OnSetupClicked(object? sender, EventArgs e)
    {
        if (_busy) return; // re-entrancy guard: ignore double-taps while a setup is in flight

        var name = NameEntry.Text?.Trim() ?? "";
        var serverUrl = ServerUrlEntry.Text?.Trim() ?? "";
        var password = PasswordEntry.Text ?? "";

        ErrorLabel.IsVisible = false;
        ResetButton.IsVisible = false;

        if (string.IsNullOrWhiteSpace(name)) { ShowError("Enter node name"); return; }
        if (password.Length < 6) { ShowError("Password must be at least 6 characters"); return; }

        // Set busy BEFORE the standalone confirmation dialog: that dialog yields to the message
        // loop, so leaving the guard until after it would let a second tap slip through and start
        // a parallel setup.
        _busy = true;
        SetBusy(true);
        try
        {
            bool isJoin = !string.IsNullOrWhiteSpace(serverUrl);
            if (isJoin)
            {
                serverUrl = NormalizeUrl(serverUrl);
            }
            else
            {
                bool confirmed = await DisplayAlert(
                    "Standalone Node",
                    "No server URL provided. This node will NOT sync with any server. Articles will not be accessible from other devices. Continue?",
                    "Create Standalone", "Cancel");
                if (!confirmed) return; // finally resets the busy state
            }

            // Heavy work (Argon2id KDF, network, snapshot import) runs off the UI thread so the
            // spinner keeps animating and the screen stays responsive on slow/old devices.
            await Task.Run(async () =>
            {
                if (isJoin)
                    await _setupSvc.JoinAsync(name, serverUrl, password);
                else
                    await _setupSvc.InitAsync(name, password);

                await _session.UnlockAsync(password);
            });

            RequestBatteryOptimizationException();
            await Permissions.RequestAsync<Permissions.PostNotifications>();
            await _router.RouteAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Error: {ex.Message}");
            // A failed join can leave a partial node ("already initialized") with no other way
            // out — expose the reset so the user can wipe and retry from this screen.
            ResetButton.IsVisible = true;
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private static string NormalizeUrl(string serverUrl)
    {
        if (serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return serverUrl;

        // Use http for local IPs, https for domain names.
        bool isLocal = serverUrl.StartsWith("192.168.", StringComparison.Ordinal)
                    || serverUrl.StartsWith("10.", StringComparison.Ordinal)
                    || (serverUrl.StartsWith("172.", StringComparison.Ordinal)
                        && int.TryParse(serverUrl.Split('.')[1], out var octet172)
                        && octet172 >= 16 && octet172 <= 31)
                    || serverUrl.StartsWith("localhost", StringComparison.OrdinalIgnoreCase);
        return (isLocal ? "http://" : "https://") + serverUrl;
    }

    private void SetBusy(bool busy)
    {
        NameEntry.IsEnabled = !busy;
        ServerUrlEntry.IsEnabled = !busy;
        PasswordEntry.IsEnabled = !busy;
        SetupButton.IsEnabled = !busy;
        ResetButton.IsEnabled = !busy;
        LoadingIndicator.IsRunning = busy;
        LoadingIndicator.IsVisible = busy;
    }

    private void ShowError(string message)
    {
        ErrorLabel.Text = message;
        ErrorLabel.IsVisible = true;
    }

    // Wipe all local data and restart — the escape hatch from a partial/locked setup state.
    // Mirrors StatusPage.OnResetNodeClicked.
    private async void OnResetClicked(object? sender, EventArgs e)
    {
        bool confirmed = await DisplayAlert(
            "Reset local data",
            "This DELETES all local data on this device and lets you set up again. Are you sure?",
            "Reset", "Cancel");
        if (!confirmed) return;

        App.StopSyncService();
        _services.GetService<IBiometricService>()?.Clear();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var dbPath = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "beememorybank.db");
        // Delete the WAL/-shm/-journal sidecars too — SQLite runs in WAL mode, and an orphaned
        // -wal left next to a fresh db gets replayed on next open, resurrecting the wiped state.
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var f = dbPath + suffix;
            if (File.Exists(f)) File.Delete(f);
        }

#if ANDROID
        // Restart the process — the only reliable way to reinitialize singletons and re-run
        // migrations on the fresh database.
        var intent = Android.App.Application.Context.PackageManager!
            .GetLaunchIntentForPackage(Android.App.Application.Context.PackageName!)!;
        intent.AddFlags(ActivityFlags.ClearTop | ActivityFlags.NewTask);
        Android.App.Application.Context.StartActivity(intent);
        Process.KillProcess(Process.MyPid());
#else
        await Shell.Current.GoToAsync("//setup");
#endif
    }

#if ANDROID
    private void RequestBatteryOptimizationException()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(23)) return;
        var pm = (PowerManager?)Platform.CurrentActivity?.GetSystemService(Context.PowerService);
        if (pm == null) return;
        if (!pm.IsIgnoringBatteryOptimizations(Platform.CurrentActivity?.PackageName ?? ""))
        {
            var intent = new Intent(Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations);
            intent.SetData(Android.Net.Uri.Parse("package:" + Platform.CurrentActivity?.PackageName));
            Platform.CurrentActivity?.StartActivity(intent);
        }
    }
#else
    private void RequestBatteryOptimizationException() { }
#endif
}
