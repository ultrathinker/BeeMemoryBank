using System.Collections.ObjectModel;
using System.Windows.Input;
using BeeMemoryBank.BlindDesktop.Scheduling;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindDesktop.ViewModels;

/// <summary>One finished backup in the list, with its own "Save to..." command.</summary>
public sealed class BackupRow(string name, long size, ICommand save)
{
    public string Name { get; } = name;
    public string SizeText { get; } = $"{size / 1024} KiB";
    public ICommand SaveCommand { get; } = save;
}

/// <summary>
/// What the window shows, mirroring the Android blind home page: identity and pairing code (text and QR), the field for the
/// computer's answer, the paired endpoint, first load / last sync / last backup, why a job waits, the running job and its progress,
/// "Sync now" / "Back up now", the schedule, the backups with "Save to...", the last 30 log lines, Re-pair, "Disconnect and wipe"
/// with a typed-name confirmation, and the autostart choice. It holds no blind logic: every action goes to <see cref="IBlindAppController"/>
/// or the scheduler's requests, and every text comes from <see cref="BlindAppStatus"/>.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    public static readonly string[] ScheduleNames = ["Off", "Daily", "Weekly"];
    private static readonly BlindBackupSchedule[] Schedules = [BlindBackupSchedule.Off, BlindBackupSchedule.Daily, BlindBackupSchedule.Weekly];

    private readonly IBlindAppController _app;
    private readonly IBlindWorkRequests _work;
    private readonly IBlindAutostart _autostart;
    private readonly Func<string, byte[]> _qr;
    private readonly Action<Action> _post;
    private int _refreshPending;
    private readonly Action<string, Exception>? _problem;
    private bool _applying;
    private string? _qrFor;
    private string? _displayName;
    private bool _disposed;

    private string _identityText = "";
    private string _nodeText = "";
    private string _pairText = "";
    private string _loadText = "";
    private string _syncText = "";
    private string _backupText = "";
    private string _jobText = "";
    private bool _jobVisible;
    private double _jobProgress;
    private bool _showPairingCode;
    private string? _pairingCodeText;
    private byte[]? _pairingQrPng;
    private string _pairingMessage = "";
    private string _callCodeInput = "";
    private string? _connectError;
    private bool _canRePair;
    private bool _canConnect;
    private bool _canSyncNow;
    private bool _canBackupNow;
    private int _scheduleIndex = 2;
    private string _logText = "";
    private string? _notice;
    private string? _keyLostText;
    private string? _startErrorText;
    private string? _keyStoreNoticeText;
    private bool _autostartOn;
    private bool _autostartKnown = true;
    private bool _wipePanelOpen;
    private string _wipeTyped = "";
    private bool _wipeBusy;
    private bool _isPaired;
    private IReadOnlyList<BackupRow> _backups = [];

    /// <param name="qr">Makes the PNG of a pairing code; in the app it is the QR library, in tests anything.</param>
    /// <param name="post">Runs an action on the UI thread; the default runs it at once (tests).</param>
    /// <param name="problem">Where a failed command or refresh is written besides the notice on the screen (the error log); tests pass nothing.</param>
    /// <param name="statusArea">What the platform calls the place of the app's icon in the window's own words: "tray" or "menu bar".</param>
    public MainViewModel(IBlindAppController app, IBlindWorkRequests work, IBlindAutostart autostart,
        Func<string, byte[]> qr, Action<Action>? post = null, Action<string, Exception>? problem = null, string statusArea = "tray")
    {
        StatusArea = statusArea;
        _problem = problem;
        _app = app;
        _work = work;
        _autostart = autostart;
        _qr = qr;
        _post = post ?? (action => action());

        // Every command runs on the UI thread: a failure becomes a notice, it never escapes into the UI loop.
        Action<Exception> failed = CommandFailed;
        ConnectCommand = new RelayCommand(Connect, () => _canConnect, failed);
        SyncNowCommand = new RelayCommand(SyncNow, () => _canSyncNow, failed);
        BackupNowCommand = new RelayCommand(BackupNow, () => _canBackupNow, failed);
        RePairCommand = new RelayCommand(RePair, () => _canRePair, failed);
        CopyPairingCodeCommand = new RelayCommand(() => { if (_pairingCodeText is { } code) CopyRequested?.Invoke(code); },
            () => _pairingCodeText is not null, failed);
        OpenWipeCommand = new RelayCommand(() => { WipeTyped = ""; WipePanelOpen = true; }, onError: failed);
        CancelWipeCommand = new RelayCommand(() => { WipePanelOpen = false; WipeTyped = ""; }, () => !_wipeBusy, failed);
        QuitCommand = new RelayCommand(() => QuitRequested?.Invoke(), onError: failed);
        ConfirmWipeCommand = new RelayCommand(() => _ = WipeAsync(), CanConfirmWipe, failed);

        _app.Changed += RequestRefresh;
        _work.UserJobReported += OnUserJobReported;
    }

    /// <summary>"tray" on Windows, "menu bar" on macOS.</summary>
    public string StatusArea { get; }

    /// <summary>The autostart toggle's own words.</summary>
    public string AutostartText => $"Start in the {StatusArea} when I sign in";

    /// <summary>What closing the window does.</summary>
    public string CloseHintText => $"Closing this window keeps the app running in the {StatusArea}.";

    /// <summary>Raised when the user asks to copy the pairing code; the window puts it on the clipboard.</summary>
    public event Action<string>? CopyRequested;

    /// <summary>Raised by the Quit button; the shell ends the app (closing the window only hides it).</summary>
    public event Action? QuitRequested;

    public ICommand ConnectCommand { get; }
    public ICommand SyncNowCommand { get; }
    public ICommand BackupNowCommand { get; }
    public ICommand RePairCommand { get; }
    public ICommand CopyPairingCodeCommand { get; }
    public ICommand OpenWipeCommand { get; }
    public ICommand CancelWipeCommand { get; }
    public ICommand ConfirmWipeCommand { get; }
    public ICommand QuitCommand { get; }

    public string IdentityText { get => _identityText; private set => Set(ref _identityText, value); }
    public string NodeText { get => _nodeText; private set => Set(ref _nodeText, value); }
    public string PairText { get => _pairText; private set => Set(ref _pairText, value); }
    public string LoadText { get => _loadText; private set => Set(ref _loadText, value); }
    public string SyncText { get => _syncText; private set => Set(ref _syncText, value); }
    public string BackupText { get => _backupText; private set => Set(ref _backupText, value); }
    public string JobText { get => _jobText; private set => Set(ref _jobText, value); }
    public bool JobVisible { get => _jobVisible; private set => Set(ref _jobVisible, value); }

    /// <summary>0..1 for a progress bar.</summary>
    public double JobProgress { get => _jobProgress; private set => Set(ref _jobProgress, value); }

    public bool ShowPairingCode { get => _showPairingCode; private set => Set(ref _showPairingCode, value); }
    public string? PairingCodeText { get => _pairingCodeText; private set => Set(ref _pairingCodeText, value); }
    public byte[]? PairingQrPng { get => _pairingQrPng; private set => Set(ref _pairingQrPng, value); }

    /// <summary>The sentence shown instead of the code: paired and used up, or why the app could not set itself up.</summary>
    public string PairingMessage { get => _pairingMessage; private set => Set(ref _pairingMessage, value); }

    /// <summary>The computer's answer typed or pasted by the user.</summary>
    public string CallCodeInput
    {
        get => _callCodeInput;
        set { if (Set(ref _callCodeInput, value)) ConnectError = null; }
    }

    public string? ConnectError { get => _connectError; private set { if (Set(ref _connectError, value)) Raise(nameof(HasConnectError)); } }
    public bool HasConnectError => !string.IsNullOrEmpty(_connectError);

    public bool CanRePair { get => _canRePair; private set { if (Set(ref _canRePair, value)) ((RelayCommand)RePairCommand).RaiseCanExecuteChanged(); } }
    public bool CanConnect { get => _canConnect; private set { if (Set(ref _canConnect, value)) ((RelayCommand)ConnectCommand).RaiseCanExecuteChanged(); } }
    public bool CanSyncNow { get => _canSyncNow; private set { if (Set(ref _canSyncNow, value)) ((RelayCommand)SyncNowCommand).RaiseCanExecuteChanged(); } }
    public bool CanBackupNow { get => _canBackupNow; private set { if (Set(ref _canBackupNow, value)) ((RelayCommand)BackupNowCommand).RaiseCanExecuteChanged(); } }

    public bool IsPaired { get => _isPaired; private set => Set(ref _isPaired, value); }

    public IReadOnlyList<string> ScheduleOptions => ScheduleNames;

    public int ScheduleIndex
    {
        get => _scheduleIndex;
        set
        {
            if (value < 0 || value >= Schedules.Length || !Set(ref _scheduleIndex, value)) return;
            if (!_applying) _app.SetSchedule(Schedules[value]);
        }
    }

    public IReadOnlyList<BackupRow> Backups { get => _backups; private set { if (Set(ref _backups, value)) Raise(nameof(HasBackups)); } }
    public bool HasBackups => _backups.Count > 0;
    public string LogText { get => _logText; private set => Set(ref _logText, value); }

    /// <summary>The line under the buttons: what the last action did.</summary>
    public string? Notice { get => _notice; private set { if (Set(ref _notice, value)) Raise(nameof(HasNotice)); } }
    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    public string? KeyLostText { get => _keyLostText; private set { if (Set(ref _keyLostText, value)) Raise(nameof(HasKeyLost)); } }
    public bool HasKeyLost => _keyLostText is not null;
    public string? KeyStoreNoticeText { get => _keyStoreNoticeText; private set { if (Set(ref _keyStoreNoticeText, value)) Raise(nameof(HasKeyStoreNotice)); } }
    public bool HasKeyStoreNotice => _keyStoreNoticeText is not null;
    public string? StartErrorText { get => _startErrorText; private set { if (Set(ref _startErrorText, value)) Raise(nameof(HasStartError)); } }
    public bool HasStartError => _startErrorText is not null;

    /// <summary>The "start with the computer" choice; setting it writes the OS entry (and reports a failure in <see cref="Notice"/>).</summary>
    public bool AutostartOn
    {
        get => _autostartOn;
        set
        {
            if (_autostartOn == value) return;
            if (!_applying)
            {
                try { _autostart.SetEnabled(value); }
                catch (Exception ex)
                {
                    Notice = $"Could not change the start-up setting: {ex.Message}";
                    Raise(); // the box goes back to what it was
                    return;
                }
            }
            Set(ref _autostartOn, value);
        }
    }

    public bool AutostartKnown { get => _autostartKnown; private set => Set(ref _autostartKnown, value); }

    public bool WipePanelOpen { get => _wipePanelOpen; private set => Set(ref _wipePanelOpen, value); }
    public string WipePrompt => $"This deletes the blind copy from this computer, with its keys, its data and its backups. Type its name \"{_displayName}\" to confirm.";

    public string WipeTyped
    {
        get => _wipeTyped;
        set
        {
            if (Set(ref _wipeTyped, value)) ((RelayCommand)ConfirmWipeCommand).RaiseCanExecuteChanged();
        }
    }

    public bool WipeBusy { get => _wipeBusy; private set => Set(ref _wipeBusy, value); }

    /// <summary>Reads the status again (off the UI thread) and shows it.</summary>
    public async Task RefreshAsync()
    {
        if (_disposed) return;
        BlindAppStatus status;
        try
        {
            status = await Task.Run(_app.GetStatus);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            Notice = $"Could not read the status: {ex.Message}";
            _problem?.Invoke("Reading the status failed", ex);
            return;
        }
        if (_disposed) return;
        try
        {
            Apply(status);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            // Showing a status is not worth ending the app for: say so, and show the next one.
            Notice = $"Could not show the status: {ex.Message}";
            _problem?.Invoke("Showing the status failed", ex);
        }
    }

    private void CommandFailed(Exception ex)
    {
        Notice = $"That did not work: {ex.Message}";
        _problem?.Invoke("A command failed", ex);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _app.Changed -= RequestRefresh;
        _work.UserJobReported -= OnUserJobReported;
    }

    private void Apply(BlindAppStatus status)
    {
        _applying = true;
        try
        {
            _displayName = status.DisplayName;
            Raise(nameof(WipePrompt));
            IdentityText = status.DisplayName ?? "(no name)";
            NodeText = status.NodeId is { } id ? $"Node {id}" : "No identity";
            IsPaired = status.IsPaired;
            PairText = status.IsPaired
                ? $"Calls {status.Endpoint}"
                : "Not paired yet: do steps 1 and 2 below.";
            LoadText = LoadTextFor(status);
            SyncText = $"Last sync: {When(status.LastSyncAt)}";
            BackupText = $"Last backup: {When(status.LastBackupAt)}";

            JobVisible = status.ActiveJob is not null;
            JobText = status.ActiveJob is null ? ""
                : status.JobProgress is { } p ? $"{status.ActiveJob}: {p:P0}" : status.ActiveJob;
            JobProgress = status.JobProgress ?? 0;

            ScheduleIndex = Math.Max(0, Array.IndexOf(Schedules, status.Schedule));

            Backups = status.Backups.Select(b => new BackupRow(b.Name, b.Size,
                new RelayCommand(() => _ = SaveBackupAsync(b.Name), onError: CommandFailed))).ToList();
            LogText = string.Join("\n", status.RecentLog.Select(e => $"{e.At.ToLocalTime():dd.MM HH:mm}  {e.Message}"));

            KeyLostText = status.BackupKeyLost
                ? "The backup key of this copy is gone from this computer's key store. The paired computer keeps a sealed copy of it, so a new key is never made silently: choose Disconnect and wipe, then pair again."
                : null;
            // The secret store did not answer (locked, access denied, busy): that is NOT a lost key and nothing here asks for a wipe.
            KeyStoreNoticeText = status.KeyStoreUnavailable is { } storeProblem
                ? $"The key store of this computer did not answer ({storeProblem}). Nothing was changed and no key is lost. Try again in a moment; if it stays, check that the computer is unlocked and that this app may use its key store."
                : null;
            StartErrorText = status.StartError is { } error
                ? $"Could not set the app up: {error} Close the app and open it again; if this stays, choose Disconnect and wipe."
                : null;

            ApplyPairing(status);

            CanSyncNow = status.IsPaired && status.InitialLoadDone;
            CanBackupNow = status.IsPaired && !status.BackupKeyLost;
            ((RelayCommand)CancelWipeCommand).RaiseCanExecuteChanged();
            ((RelayCommand)ConfirmWipeCommand).RaiseCanExecuteChanged();

            var autostart = ReadAutostart();
            AutostartKnown = autostart is not null;
            AutostartOn = autostart == true;
        }
        finally
        {
            _applying = false;
        }
    }

    private void ApplyPairing(BlindAppStatus status)
    {
        var code = status.AwaitingAnswer ? _app.PairingCode() : null;
        ShowPairingCode = code is not null;
        PairingCodeText = code;
        if (code != _qrFor)
        {
            // The picture is a convenience: the text of the code is on the screen too. A QR library that fails must not take the rest of
            // the screen with it (the answer field has to stay usable), and it is tried once per code, not on every refresh.
            _qrFor = code;
            try
            {
                PairingQrPng = code is null ? null : _qr(code);
            }
            catch (Exception ex) when (!IsFatal(ex))
            {
                PairingQrPng = null;
                Notice = $"The QR picture could not be made ({ex.Message}). Copy the code below instead.";
                _problem?.Invoke("Making the QR picture failed", ex);
            }
        }

        PairingMessage = code is not null ? ""
            : status.KeyStoreUnavailable is not null ? "The pairing code cannot be shown while the key store of this computer does not answer."
            : status.StartError is not null ? "The app could not set itself up; see the message below."
            : status.IsPaired ? "Paired. The code was used up; to connect to another computer, choose Re-pair."
            : "No identity. Choose Disconnect and wipe, then set the app up again.";

        CanConnect = code is not null;
        CanRePair = code is null && status.IsPaired;
        ((RelayCommand)CopyPairingCodeCommand).RaiseCanExecuteChanged();
    }

    private bool? ReadAutostart()
    {
        try { return _autostart.IsEnabled; }
        catch (Exception) { return null; }
    }

    private void Connect()
    {
        var error = _app.AcceptCallCode(CallCodeInput ?? "");
        ConnectError = error;
        if (error is null)
        {
            CallCodeInput = "";
            Notice = "Paired. The first load starts now.";
            _work.RequestHeavy();
        }
        _ = RefreshAsync();
    }

    private void RePair()
    {
        try
        {
            _app.StartRePair();
            Notice = "A new code was made. The current connection stays until the computer's new answer is accepted.";
        }
        catch (Exception ex)
        {
            Notice = $"Could not start re-pairing: {ex.Message}";
        }
        _ = RefreshAsync();
    }

    private void SyncNow()
    {
        _work.RequestSync();
        Notice = "Sync asked for.";
    }

    private void BackupNow()
    {
        _work.RequestBackup();
        Notice = "Backup asked for.";
    }

    private async Task SaveBackupAsync(string name)
    {
        try
        {
            var bytes = await _app.ExportBackupAsync(name);
            Notice = $"Saved {name} ({bytes / 1024} KiB) to the chosen place.";
        }
        catch (OperationCanceledException)
        {
            Notice = "Nothing was saved.";
        }
        catch (Exception ex)
        {
            Notice = $"Could not save the backup: {ex.Message}";
        }
        await RefreshAsync();
    }

    private bool CanConfirmWipe() =>
        !_wipeBusy && _displayName is not null && string.Equals(_wipeTyped.Trim(), _displayName, StringComparison.Ordinal);

    private async Task WipeAsync()
    {
        if (!CanConfirmWipe()) return;
        WipeBusy = true;
        ((RelayCommand)ConfirmWipeCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CancelWipeCommand).RaiseCanExecuteChanged();
        try
        {
            await Task.Run(() => _app.DisconnectAndWipeAsync());
            // The host has returned to its first-run state and replaced this view model.
        }
        catch (AggregateException ex)
        {
            // Not restarted over a half-wiped copy: say what is left and let the user press it again.
            Notice = $"{ex.Message} Press Disconnect and wipe again to finish.";
        }
        catch (Exception ex)
        {
            Notice = $"Could not wipe: {ex.Message}";
        }
        finally
        {
            WipeBusy = false;
            ((RelayCommand)ConfirmWipeCommand).RaiseCanExecuteChanged();
            ((RelayCommand)CancelWipeCommand).RaiseCanExecuteChanged();
        }
    }

    private void RequestRefresh()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshPending, 1) == 1) return;
        _post(() =>
        {
            Interlocked.Exchange(ref _refreshPending, 0);
            _ = RefreshAsync();
        });
    }

    private void OnUserJobReported(string sentence) => _post(() => Notice = sentence);

    private static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException or AccessViolationException or StackOverflowException or ThreadAbortException;

    /// <summary>
    /// What the first load is doing: not started because not paired, running with progress, a real failure with its retry time, or about to start.
    /// </summary>
    private string LoadTextFor(BlindAppStatus status)
    {
        if (status.InitialLoadDone) return "First load: done.";
        if (!status.IsPaired) return "First load: starts after pairing.";
        if (status.ActiveJob == "First load")
            return status.JobProgress is { } progress ? $"First load: running ({progress:P0})." : "First load: running.";
        if (status.LastFailure is { Title: "First load" } failure) return FailedLoadText(failure);
        return "First load: starting.";
    }

    private string FailedLoadText(BlindAppFailure failure)
    {
        var text = $"First load: the last try failed - {Reason(failure.Message)}";
        if (failure.Attempts > 1) text += $" ({failure.Attempts} tries in a row)";
        if (_work.FirstLoadRetry is { } retry)
        {
            text += $" Next try at {retry.NextAt.ToLocalTime():HH:mm:ss}";
            text += retry.AtCeiling ? $"; it keeps trying every {Math.Max(1, (int)retry.Every.TotalMinutes)} minutes until it works." : ".";
        }
        else
        {
            text += " It tries again soon.";
        }
        return text;
    }

    /// <summary>The error in one short line; a 401 right after pairing is the normal case and is said so (the computer has not heard of this device yet).</summary>
    private static string Reason(string message)
    {
        var text = message.ReplaceLineEndings(" ").Trim();
        if (text.Length > 140) text = text[..140] + "...";
        if (text.Contains("401", StringComparison.Ordinal))
            text += " The computer has probably not heard of this device yet; that is normal for a short time after pairing.";
        return text;
    }

    private static string When(DateTimeOffset? at) => at is { } t ? t.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "never";
}
