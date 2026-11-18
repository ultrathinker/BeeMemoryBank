using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Mobile.Controls;
using BeeMemoryBank.Mobile.Localization;
using BeeMemoryBank.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

#if ANDROID
using Android.Content;
using Android.OS;
#endif

namespace BeeMemoryBank.Mobile.Pages;

/// <summary>
/// Joining the owner's memory bank, in the iPhone JoinPage's shape: the code at full width with Paste
/// beside its label, the master password with one line under it, this phone's name, Join, the explanation folded under
/// "More". No hint on join - the computer's password is reused, the core accepts a null hint. The create mode keeps its
/// hint (a new standalone vault on this phone). Right after a join or a create the database is encrypted with no button
/// (PhoneSealing); an unlock never encrypts.
/// </summary>
public partial class SetupPage : ContentPage
{
    private readonly NodeSetupService _setupSvc;
    private readonly SessionService _session;
    private readonly PostUnlockRouter _router;
    private readonly IServiceProvider _services;
    private bool _busy;
    private string? _sealProblem;

    public SetupPage(NodeSetupService setupSvc, SessionService session, PostUnlockRouter router, IServiceProvider services)
    {
        InitializeComponent();
        _setupSvc = setupSvc;
        _session = session;
        _router = router;
        _services = services;
        ModeSegments.Items = [AppStrings.Get("SetupModeJoin"), AppStrings.Get("SetupModeCreate")];
        ModeSegments.SelectedIndex = 0;
        ModeSegments.SelectionChanged += (_, index) =>
        {
            JoinPanel.IsVisible = index == 0;
            CreatePanel.IsVisible = index == 1;
        };
        PrefillDefaults();
    }

    // Pre-fill sensible defaults so the user can just type the password and continue. The code field
    // stays empty (the placeholder shows the expected shape).
    private void PrefillDefaults()
    {
        var deviceName = Microsoft.Maui.Devices.DeviceInfo.Current.Name;
        if (string.IsNullOrWhiteSpace(deviceName)) deviceName = AppStrings.Get("ConnectDeviceNamePlaceholder");
        if (string.IsNullOrWhiteSpace(NameEntry.Text)) NameEntry.Text = deviceName;
        if (string.IsNullOrWhiteSpace(CreateNameEntry.Text)) CreateNameEntry.Text = deviceName;
    }

    // busy-exempt: returns to the Welcome screen; nothing is sent and nothing waits
    private async void OnBackClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("//welcome");

    protected override bool OnBackButtonPressed()
    {
        _ = Shell.Current.GoToAsync("//welcome");
        return true;
    }

    // busy-exempt: copies the clipboard into the code field; nothing is sent and nothing waits
    private async void OnPasteClicked(object? sender, EventArgs e)
    {
        var text = await Clipboard.Default.GetTextAsync();
        if (!string.IsNullOrWhiteSpace(text)) ServerUrlEntry.Text = text.Trim();
    }

    // busy-exempt: opens Play services' own scanner screen; nothing is sent, and the wait is the person at the camera
    private async void OnScanClicked(object? sender, EventArgs e)
    {
        var scanner = _services.GetService<IQrScanner>();
        if (scanner is null)
        {
            await AppDialog.AlertAsync(this, AppStrings.Get("JoinScanQr"), AppStrings.Get("QrScannerUnavailable"));
            return;
        }
        try
        {
            var text = await scanner.ScanAsync();
            // A cancelled scan leaves the field as it was.
            if (!string.IsNullOrWhiteSpace(text)) ServerUrlEntry.Text = text.Trim();
        }
        catch (QrScannerUnavailableException)
        {
            await AppDialog.AlertAsync(this, AppStrings.Get("JoinScanQr"), AppStrings.Get("QrScannerUnavailable"));
        }
    }

    private void OnMoreClicked(object? sender, EventArgs e) => MoreLabel.IsVisible = !MoreLabel.IsVisible;

    private async void OnSetupClicked(object? sender, EventArgs e)
    {
        if (_busy) return; // re-entrancy guard: ignore double-taps while a setup is in flight

        var name = NameEntry.Text?.Trim() ?? "";
        var serverUrl = ServerUrlEntry.Text?.Trim() ?? "";
        var password = PasswordEntry.Text ?? "";

        ErrorLabel.IsVisible = false;
        ResetButton.IsVisible = false;

        if (string.IsNullOrWhiteSpace(name)) { ShowError(ErrorLabel, AppStrings.Get("SetupNameRequired")); return; }
        // The same policy the iPhone app checks (FullVault.CheckNewPassword): the core's own
        // validator, at least 8 characters with an upper- and a lowercase letter and a digit.
        try { UserService.ValidatePassword(password); }
        catch (ArgumentException ex) { ShowError(ErrorLabel, ex.Message); return; }

        // A join code from a computer's Connect page: the join goes only to the computer whose key
        // it pins. A damaged code is refused, never read as a plain address — that would drop the pin.
        JoinCode? code = null;
        if (JoinCode.LooksLikeJoinCode(serverUrl) && !JoinCode.TryParse(serverUrl, out code))
        {
            ShowError(ErrorLabel, AppStrings.Get("JoinCodeDamaged"));
            return;
        }
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            ShowError(ErrorLabel, AppStrings.Get("JoinCodeRequired"));
            return;
        }

        // No hint on join: the password is the computer's, the core accepts null.
        await RunSetupAsync(isJoin: true, name, code?.Address ?? NormalizeUrl(serverUrl), password, null, code, SetupButton, ErrorLabel);
    }

    private async void OnCreateClicked(object? sender, EventArgs e)
    {
        if (_busy) return;

        var name = CreateNameEntry.Text?.Trim() ?? "";
        var password = CreatePasswordEntry.Text ?? "";
        var passwordHint = PasswordHintEntry.Text?.Trim() ?? "";

        CreateErrorLabel.IsVisible = false;
        ResetButton.IsVisible = false;

        if (string.IsNullOrWhiteSpace(name)) { ShowError(CreateErrorLabel, AppStrings.Get("SetupNameRequired")); return; }
        try { UserService.ValidatePassword(password); }
        catch (ArgumentException ex) { ShowError(CreateErrorLabel, ex.Message); return; }
        if (PasswordHintPolicy.Validate(passwordHint, password, name) is { } hintProblem)
        {
            ShowError(CreateErrorLabel, hintProblem);
            return;
        }

        await RunSetupAsync(isJoin: false, name, "", password, passwordHint, null, CreateButton, CreateErrorLabel);
    }

    private async Task RunSetupAsync(bool isJoin, string name, string serverUrl, string password, string? passwordHint,
        JoinCode? code, Button source, Label error)
    {
        // Set busy BEFORE the standalone confirmation dialog: that dialog yields to the message
        // loop, so leaving the guard until after it would let a second tap slip through.
        _busy = true;
        try
        {
            if (!isJoin)
            {
                bool confirmed = await AppDialog.ConfirmAsync(this, AppStrings.Get("StandaloneTitle"),
                    AppStrings.Get("StandaloneDesc"), AppStrings.Get("StandaloneConfirm"), AppStrings.Get("Cancel"));
                if (!confirmed) return; // finally resets the busy state
            }

            // A long operation (BMB-311): the one busy helper puts up the "please wait" pop-up (a second
            // tap is impossible) with rotating status messages, and puts the whole error under the button.
            var failed = false;
            var ran = await BusyAction.RunAsync(source, async busy =>
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
                            await _setupSvc.JoinAsync(name, serverUrl, password, code, passwordHint);
                        else
                            await _setupSvc.InitAsync(name, password, passwordHint);

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
                Title = AppStrings.Get(isJoin ? "JoiningTitle" : "CreatingTitle"),
                Message = AppStrings.Get("SetupLongMessage"),
                Result = (ok, text) =>
                {
                    if (ok) return;
                    failed = true;
                    ShowError(error, AppStrings.Format("SetupErrorFormat", text));
                    // A failed join can leave a partial node ("already initialized") with no other way
                    // out — expose the reset so the user can wipe and retry from this screen.
                    ResetButton.IsVisible = true;
                },
            });
            if (!ran || failed) return;

            // Every database is encrypted, no button: right after a create or a join,
            // behind its own "please wait" pop-up; a failure is said over the notes after the routing.
            await EncryptIfNeededAsync();

            // After the pop-up is gone: a navigation under a modal page is not reliable.
            RequestBatteryOptimizationException();
            await Permissions.RequestAsync<Permissions.PostNotifications>();
            await _router.RouteAsync();
            if (_sealProblem is not null && Shell.Current?.CurrentPage is { } page)
                await AppDialog.AlertAsync(page, AppStrings.Get("AutoEncryptTitle"), _sealProblem);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// The phone's database encrypted right after a create or a join (the iPhone's AppFlow.EncryptThenShowMainAsync,
    /// same rules): behind the busy pop-up with the screen kept on; a failure is written to the app's log file and
    /// said in a dialog over the notes. An unlock never comes here.
    /// </summary>
    private async Task EncryptIfNeededAsync()
    {
        _sealProblem = null;
        var sealing = _services.GetRequiredService<PhoneSealing>();
        if (!sealing.IsNeeded) return;
        await BusyAction.RunAsync(null, async busy =>
        {
            DeviceDisplay.Current.KeepScreenOn = true;
            try
            {
                var run = sealing.SealAsync(await StartsNetworkAsync());
                while (!run.IsCompleted)
                {
                    if (sealing.Progress is { } p) busy.Report(p.Done, p.Step);
                    await Task.WhenAny(run, Task.Delay(400));
                }
                await run;
            }
            catch (PhoneSealingException ex)
            {
                _sealProblem = ex.Code == PhoneSealingException.OldNetwork
                    ? AppStrings.Get("AutoEncryptOldNetwork")
                    : AppStrings.Format("AutoEncryptFailed", ex.Message);
                NoteEncryptionFailure(ex.Code + ": " + ex.Message);
            }
            finally
            {
                DeviceDisplay.Current.KeepScreenOn = false;
            }
        }, new BusyOptions
        {
            Long = true,
            Title = AppStrings.Get("AutoEncryptTitle"),
            Message = AppStrings.Get("AutoEncryptMessage"),
            Result = (ok, text) =>
            {
                if (ok) return;
                // This path is a failure that is NOT a PhoneSealingException with our own text: the dialog says our own calm sentence,
                // never the raw exception text; the detail goes to the app's log file.
                _sealProblem = AppStrings.Format("AutoEncryptFailed", AppStrings.Get("AutoEncryptFailedReason"));
                NoteEncryptionFailure(text);
            },
        });
    }

    /// <summary>A vault alone on this phone (no other device in its list) starts its own network, so it may switch the new sync format on.</summary>
    private async Task<bool> StartsNetworkAsync()
    {
        using var scope = _services.CreateScope();
        var self = (await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Interfaces.INodeIdentityRepository>().GetAsync())?.NodeId;
        var peers = await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Interfaces.IWhitelistRepository>().GetAllActiveAsync();
        return peers.All(p => p.NodeId == self);
    }

    /// <summary>The last reason the database could not be encrypted, beside the vault folder (no note text, no key, no password).</summary>
    private static void NoteEncryptionFailure(string reason)
    {
        System.Diagnostics.Debug.WriteLine("Phone database encryption failed: " + reason);
        try
        {
            File.WriteAllText(Path.Combine(FileSystem.AppDataDirectory, "encrypt-last-error.txt"),
                DateTime.UtcNow.ToString("O") + " " + reason + System.Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a diagnostic: the dialog has said it already.
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

    // Cycles short status messages every few seconds during the long setup so the user always sees
    // something happening, even while a single backend step (e.g. snapshot download) runs.
    private static async Task CycleStatusAsync(BusyProgress busy, bool isJoin, CancellationToken ct)
    {
        var messages = isJoin
            ? new[]
            {
                AppStrings.Get("SetupStatusContacting"),
                AppStrings.Get("SetupStatusVerifying"),
                AppStrings.Get("SetupStatusKeys"),
                AppStrings.Get("SetupStatusDownloading"),
                AppStrings.Get("SetupStatusImporting"),
                AppStrings.Get("SetupStatusAlmost")
            }
            : new[]
            {
                AppStrings.Get("SetupStatusCreating"),
                AppStrings.Get("SetupStatusEncryption"),
                AppStrings.Get("SetupStatusAlmost")
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

    private static void ShowError(Label label, string message)
    {
        label.Text = message;
        label.IsVisible = true;
    }

    // Wipe all local data and restart — the escape hatch from a partial/locked setup state.
    private async void OnResetClicked(object? sender, EventArgs e)
    {
        bool confirmed = await AppDialog.ConfirmAsync(this, AppStrings.Get("ResetLocalTitle"),
            AppStrings.Get("ResetLocalDesc"), AppStrings.Get("Delete"), AppStrings.Get("Cancel"), danger: true);
        if (!confirmed) return;

        // A long operation (BMB-311): the one busy helper puts up the pop-up until the app restarts.
        await BusyAction.RunAsync(sender as Button, async () =>
        {
            App.StopSyncService();
            _services.GetService<IBiometricService>()?.Clear();
            // Clear the ingest key too — it is bound to the OLD node identity. Leaving it means the next
            // setup's enrolment is skipped and background sync signs with the wrong seed until it self-heals.
            _services.GetService<IIngestKeyStore>()?.Clear();

            // The whole vault file set, every sidecar with it (an orphaned -wal next to a fresh db gets replayed on the next open): a flow that had
            // already encrypted the vault left its boot store behind, and the next start was a dead end.
            await PhoneWipe.ReturnToPlainAsync(_services);

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
        }, new BusyOptions
        {
            Long = true,
            Title = AppStrings.Get("ResettingPhoneTitle"),
            Message = AppStrings.Get("ResettingPhoneMessage"),
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
