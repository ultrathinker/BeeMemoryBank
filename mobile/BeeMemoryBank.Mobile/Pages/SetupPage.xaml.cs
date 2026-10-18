using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Models;
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
    // The server URL stays empty (the placeholder shows the expected shape): a half-typed
    // domain meant nothing to anyone but the author, and an empty field is the standalone
    // choice.
    private void PrefillDefaults()
    {
        if (string.IsNullOrWhiteSpace(NameEntry.Text))
        {
            var deviceName = Microsoft.Maui.Devices.DeviceInfo.Current.Name;
            NameEntry.Text = string.IsNullOrWhiteSpace(deviceName) ? "My Node" : deviceName;
        }
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

        // A join code from a computer's Connect page: the join goes only to the computer whose key
        // it pins. A damaged code is refused, never read as a plain address — that would drop the pin.
        JoinCode? code = null;
        if (JoinCode.LooksLikeJoinCode(serverUrl) && !JoinCode.TryParse(serverUrl, out code))
        {
            ShowError("This join code is damaged. Copy it again from Connect a device on the computer.");
            return;
        }

        // Set busy BEFORE the standalone confirmation dialog: that dialog yields to the message
        // loop, so leaving the guard until after it would let a second tap slip through and start
        // a parallel setup.
        _busy = true;
        try
        {
            bool isJoin = !string.IsNullOrWhiteSpace(serverUrl);
            if (isJoin)
            {
                serverUrl = code?.Address ?? NormalizeUrl(serverUrl);
            }
            else
            {
                bool confirmed = await DisplayAlert(
                    "Standalone Node",
                    "No server URL provided. This node will NOT sync with any server. Articles will not be accessible from other devices. Continue?",
                    "Create Standalone", "Cancel");
                if (!confirmed) return; // finally resets the busy state
            }

            // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up (a second tap is impossible) with rotating status
            // messages so the user sees progress during the long join, and puts the whole error under the button when it fails.
            var failed = false;
            var ran = await BusyAction.RunAsync(SetupButton, async busy =>
            {
                using var stop = new CancellationTokenSource();
                _ = CycleStatusAsync(busy, isJoin, stop.Token);
                try
                {
                    // Heavy work (Argon2id KDF, network, snapshot import) runs off the UI thread so the
                    // status ticker keeps updating and the screen stays responsive on slow/old devices.
                    await Task.Run(async () =>
                    {
                        if (isJoin)
                            await _setupSvc.JoinAsync(name, serverUrl, password, code);
                        else
                            await _setupSvc.InitAsync(name, password);

                        await _session.UnlockAsync(password);
                    });
                }
                finally
                {
                    stop.Cancel();
                }
            }, new BusyOptions
            {
                Long = true,
                Title = isJoin ? "Joining the network" : "Creating your node",
                Message = "Please wait. This can take several minutes on a slow connection. Do not close the app.",
                Result = (ok, text) =>
                {
                    if (ok) return;
                    failed = true;
                    ShowError($"Error: {text}");
                    // A failed join can leave a partial node ("already initialized") with no other way
                    // out — expose the reset so the user can wipe and retry from this screen.
                    ResetButton.IsVisible = true;
                },
            });
            if (!ran || failed) return;

            // After the pop-up is gone: a navigation under a modal page is not reliable.
            RequestBatteryOptimizationException();
            await Permissions.RequestAsync<Permissions.PostNotifications>();
            await _router.RouteAsync();
        }
        finally
        {
            _busy = false;
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

    // Cycles short status messages every few seconds during the long setup so the user always
    // sees something happening, even while a single backend step (e.g. snapshot download) runs.
    private static async Task CycleStatusAsync(BusyProgress busy, bool isJoin, CancellationToken ct)
    {
        var messages = isJoin
            ? new[]
            {
                "Contacting the server…",
                "Verifying your password…",
                "Exchanging encryption keys…",
                "Downloading your library…",
                "Importing articles…",
                "Almost done…"
            }
            : new[]
            {
                "Creating your node…",
                "Setting up encryption…",
                "Almost done…"
            };

        int i = 0;
        while (!ct.IsCancellationRequested)
        {
            busy.Report(null, messages[Math.Min(i, messages.Length - 1)]);
            i++;
            try { await Task.Delay(5000, ct); }
            catch (TaskCanceledException) { break; }
        }
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

        // A long operation (BMB-311): the one busy helper puts up the pop-up until the app restarts; a failure is said in full under the button.
        await BusyAction.RunAsync(sender as Button, () =>
        {
            App.StopSyncService();
            _services.GetService<IBiometricService>()?.Clear();
            // Clear the ingest key too — it's bound to the OLD node identity. Leaving it means the next
            // setup's enrolment is skipped (HasEnrolledKey()==true) and background sync signs with the
            // wrong seed until it self-heals after a failed cycle.
            _services.GetService<IIngestKeyStore>()?.Clear();
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
            return Task.CompletedTask;
#else
            return Shell.Current.GoToAsync("//setup");
#endif
        }, new BusyOptions
        {
            Long = true,
            Title = "Resetting this phone",
            Message = "Please wait. The local data is being removed and the app restarts by itself.",
            ResultLabel = ErrorLabel,
        });
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
