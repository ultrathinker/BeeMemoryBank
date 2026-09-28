using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Mobile.Services.Blind;
using QRCoder;

namespace BeeMemoryBank.Mobile.Pages;

/// <summary>
/// The blind copy's only screen (plan section 10): state, the two pairing codes, backups (now, "Save
/// to…", schedule), the log, and "Disconnect and wipe". There is no unlock: a blind copy has no
/// master password.
/// </summary>
public partial class BlindHomePage : ContentPage
{
    private static readonly BlindBackupSchedule[] Schedules = [BlindBackupSchedule.Off, BlindBackupSchedule.Daily, BlindBackupSchedule.Weekly];

    private readonly BlindPhoneState _state;
    private readonly BlindPhonePairing _pairing;
    private readonly BlindPhoneBackupRunner _backups;
    private readonly BlindHeavyWork _work;
    private readonly BlindPhoneLog _log;
    private readonly IDeviceStateProvider _device;
    private readonly IServiceProvider _services;
    private IDispatcherTimer? _timer;

    public BlindHomePage(BlindPhoneState state, BlindPhonePairing pairing, BlindPhoneBackupRunner backups,
        BlindHeavyWork work, BlindPhoneLog log, IDeviceStateProvider device, IServiceProvider services)
    {
        InitializeComponent();
        _state = state;
        _pairing = pairing;
        _backups = backups;
        _work = work;
        _log = log;
        _device = device;
        _services = services;
        SchedulePicker.ItemsSource = new[] { "Off", "Daily", "Weekly" };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _work.Progress += OnJobProgress;
        ShowPhoneCode();
        Refresh();
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(5);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        _work.Progress -= OnJobProgress;
        _timer?.Stop();
        base.OnDisappearing();
    }

    private void Refresh()
    {
        NameLabel.Text = _state.DisplayName ?? "(no name)";
        NodeLabel.Text = _state.NodeId is { } id ? $"Node {id}" : "No identity";
        PairLabel.Text = _state.CallCode is { } call
            ? $"Calls {call.Address} (node {call.NodeId.ToString()[..8]}…)"
            : "Not paired yet: do steps 1 and 2 below.";
        LoadLabel.Text = _state.InitialLoadDone
            ? "First load: done."
            : "First load: waits for Wi-Fi and the charger after pairing.";
        SyncLabel.Text = $"Last sync: {When(_state.LastSyncAt)}";
        BackupLabel.Text = $"Last backup: {When(_state.LastBackupAt)}";
        ConditionLabel.Text = BlindPhoneWork.WhyNot(BlindPhoneJob.Backup, _device.Current()) is { } wait
            ? $"A backup would wait now: {wait}"
            : "Wi-Fi and charger present: backups can run.";

        SchedulePicker.SelectedIndex = Array.IndexOf(Schedules, _state.Schedule);
        var files = _backups.Backups();
        BackupsLabel.Text = files.Count == 0
            ? "No backups on the phone yet."
            : string.Join("\n", files.Select(f => $"{f.Name}  ({f.Length / 1024} KiB)"));
        SaveToButton.IsEnabled = files.Count > 0;
        BackupNowButton.IsEnabled = _state.CallCode != null;

        LogLabel.Text = string.Join("\n", _log.Latest(30).Select(e => $"{e.At.ToLocalTime():dd.MM HH:mm}  {e.Message}"));
    }

    private void ShowPhoneCode()
    {
        var code = _pairing.PhoneCode()?.ToString();
        PhoneCodeImage.IsVisible = code != null;
        RePairButton.IsVisible = code == null && _pairing.IsPaired;
        PhoneCodeLabel.Text = code
            ?? (_pairing.IsPaired
                ? "Paired. The code was used up; to connect this phone to another computer, choose Re-pair."
                : "No identity. Wipe and set the phone up again.");
        if (code == null) return;
        using var generator = new QRCodeGenerator();
        var png = new PngByteQRCode(generator.CreateQrCode(code, QRCodeGenerator.ECCLevel.Q)).GetGraphic(10);
        PhoneCodeImage.Source = ImageSource.FromStream(() => new MemoryStream(png));
    }

    private void OnJobProgress(string title, double progress) => MainThread.BeginInvokeOnMainThread(() =>
    {
        JobProgress.IsVisible = JobLabel.IsVisible = progress < 1;
        JobProgress.Progress = progress;
        JobLabel.Text = $"{title}: {progress:P0}";
    });

    private async void OnCopyPhoneCodeClicked(object? sender, EventArgs e)
    {
        if (_pairing.PhoneCode()?.ToString() is { } code) await Clipboard.Default.SetTextAsync(code);
    }

    private void OnConnectClicked(object? sender, EventArgs e)
    {
        var error = _pairing.AcceptCallCode(CallCodeEntry.Text ?? "");
        CallErrorLabel.Text = error;
        CallErrorLabel.IsVisible = error != null;
        if (error == null) CallCodeEntry.Text = "";
        ShowPhoneCode();
        Refresh();
    }

    private async void OnRePairClicked(object? sender, EventArgs e)
    {
        if (!await DisplayAlertAsync("Re-pair",
                "This makes a new phone code for adding the phone on a computer again. The current connection stays until the computer's new answer is accepted.",
                "Make a new code", "Cancel"))
            return;
        _pairing.StartRePair();
        ShowPhoneCode();
        Refresh();
    }

    private void OnBackupNowClicked(object? sender, EventArgs e)
    {
#if ANDROID
        Platforms.Android.BlindBackupService.Start(Platform.AppContext);
#endif
        _log.Add("backup", "Backup asked for.");
        Refresh();
    }

    private async void OnSaveToClicked(object? sender, EventArgs e)
    {
        if (_backups.Backups().FirstOrDefault() is not { } latest) return;
        try
        {
#if ANDROID
            if (await Platforms.Android.SafExport.SaveAsync(latest.FullName))
                _log.Add("backup", $"Saved {latest.Name} to the chosen place.");
#endif
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Save to…", ex.Message, "OK");
        }
        Refresh();
    }

    private void OnScheduleChanged(object? sender, EventArgs e)
    {
        if (SchedulePicker.SelectedIndex >= 0) _state.Schedule = Schedules[SchedulePicker.SelectedIndex];
    }

    private async void OnWipeClicked(object? sender, EventArgs e)
    {
        var name = _state.DisplayName ?? "";
        var typed = await DisplayPromptAsync("Disconnect and wipe",
            $"This deletes the blind copy from this phone. Type its name \"{name}\" to confirm.",
            "Wipe", "Cancel");
        if (typed == null) return;
        if (typed.Trim() != name)
        {
            await DisplayAlertAsync("Disconnect and wipe", "The name does not match. Nothing was deleted.", "OK");
            return;
        }
        BlindPhoneReset.WipeAndRestart(_services);
    }

    private static string When(DateTimeOffset? at) => at is { } t ? t.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "never";
}
