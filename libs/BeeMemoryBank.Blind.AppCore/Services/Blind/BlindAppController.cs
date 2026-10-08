using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>One finished backup file, as a host shows it.</summary>
public sealed record BlindAppBackup(string Name, long Size);

/// <summary>
/// UI-safe snapshot of the blind app: what a host window or a tray tooltip shows. Immutable; every call to
/// <see cref="BlindAppController.GetStatus"/> reads the current state again.
/// </summary>
/// <param name="ActiveJob">"Sync", "First load" or "Backup" while one runs, otherwise null.</param>
/// <param name="JobProgress">0..1 while the running job reports progress, otherwise null.</param>
/// <param name="Endpoint">The address of the paired computer, or null before pairing.</param>
/// <param name="AwaitingAnswer">True while a pairing code is shown and the computer's answer is still to come (first pairing or Re-pair).</param>
/// <param name="StartError">Set when the app could not set itself up (see <see cref="BlindAppController.InitializeAsync"/>).</param>
/// <param name="KeyStoreUnavailable">Set (a short reason without secrets) when the host's secret store did not answer - locked, access denied, blocked.
/// <see cref="BackupKeyLost"/> and <see cref="AwaitingAnswer"/> are then not known and are reported false: a store that does not answer is not a lost key.</param>
/// <param name="LastFailure">The failure the first load or backup is in now (the same error repeated counts up), or null.</param>
public sealed record BlindAppStatus(Guid? NodeId, string? DisplayName, bool IsPaired, bool InitialLoadDone,
    DateTimeOffset? LastSyncAt, DateTimeOffset? LastBackupAt, BlindBackupSchedule Schedule, bool BackupKeyLost,
    string? ActiveJob, double? JobProgress, IReadOnlyList<BlindAppBackup> Backups,
    IReadOnlyList<BlindPhoneLog.Entry> RecentLog,
    string? Endpoint = null, bool AwaitingAnswer = false, string? StartError = null,
    string? KeyStoreUnavailable = null, BlindAppFailure? LastFailure = null);

/// <summary>How the last long job failed, for the screen: the error's own words, when, and how many times in a row.</summary>
public sealed record BlindAppFailure(string Title, string Message, DateTimeOffset At, int Attempts);

/// <summary>
/// Single entry point for hosts that do not need to know the Android implementation details: every rule of the
/// blind app (pairing, one job at a time, backups, wipe) sits behind it, a host only supplies the
/// operating-system seams and a screen.
/// </summary>
public sealed class BlindAppController(
    BlindStartup startup, BlindPhoneState state, BlindMobilePairing pairing, BlindHeavyWork heavy,
    BlindPhoneBackupRunner backups, BlindPhoneLog log, IBlindPhoneSync sync,
    IBlindPaths paths, IServiceProvider services, TimeProvider time, IBlindBackupExporter? exporter = null,
    BlindAppOptions? options = null, BlindActivity? activity = null) : IBlindAppController
{
    private const string DefaultDisplayName = "Blind copy";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _jobLock = new();
    private string? _activeJob;
    private double? _jobProgress;
    private string? _startError;
    private int _progressHooked;

    /// <summary>Raised when something a screen shows changed: a job started, moved on or ended. May come from any thread.</summary>
    public event Action? Changed;

    /// <summary>
    /// Opens the database and makes the blind identity if there is none (the name comes from
    /// <see cref="BlindAppOptions.DisplayNameFactory"/>). A failure is logged and kept in
    /// <see cref="BlindAppStatus.StartError"/>; it is not thrown, because the screen has to open to say what to do
    /// (typically: disconnect and wipe).
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        HookProgress();
        try
        {
            await startup.EnsureReadyAsync().WaitAsync(ct);
            if (!pairing.HasIdentity)
                await pairing.CreateIdentityAsync(ChooseDisplayName(), ct);
            _startError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _startError = ex.Message;
            log.Add("start", $"Could not set the app up: {ex.Message}");
        }
        RaiseChanged();
    }

    /// <summary>The code to show to the computer (text and QR), or null when no answer is awaited.</summary>
    public string? PairingCode()
    {
        try
        {
            return pairing.PhoneCodeText();
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            return null; // the secret store did not answer; GetStatus() says so (KeyStoreUnavailable)
        }
    }

    /// <summary>Accepts the computer's answer. Null on success, otherwise why not (words for the screen).</summary>
    public string? AcceptCallCode(string text)
    {
        var error = pairing.AcceptCallCode(text);
        if (error is null) RaiseChanged();
        return error;
    }

    /// <summary>Makes a new pairing code (to connect to another computer); the current connection stays until an answer is accepted.</summary>
    public void StartRePair()
    {
        pairing.StartRePair();
        RaiseChanged();
    }

    public void SetSchedule(BlindBackupSchedule schedule)
    {
        state.Schedule = schedule;
        RaiseChanged();
    }

    /// <summary>
    /// The first load if it is not done, then a backup when one is due (or always when <paramref name="forceBackup"/>),
    /// one job at a time. Returns the sentence for the screen; never throws for an ordinary stop or failure (it is in the
    /// log), only for cancellation.
    /// </summary>
    public async Task<string> RunHeavyAsync(bool forceBackup, CancellationToken ct = default)
    {
        HookProgress();
        await _gate.WaitAsync(ct);
        SetActive(state.InitialLoadDone ? "Backup" : "First load", null);
        try
        {
            var result = await heavy.RunAsync(forceBackup, ct);
            // A failure was already written (or, being the same as the last one, deliberately not repeated) by the job itself.
            if (heavy.LastFailure?.Result != result) BlindRunReport.Record(log, forceBackup ? "backup" : "run", result);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An error nobody planned for: the log is where the user can read it, the next slot tries again.
            BlindRunReport.RecordFailure(log, forceBackup ? "backup" : "run", ex);
            return $"Stopped by an unexpected error: {ex.Message}";
        }
        finally
        {
            SetActive(null, null);
            _gate.Release();
        }
    }

    public Task<string> RequestBackup(CancellationToken ct = default) => RunHeavyAsync(true, ct);

    /// <summary>
    /// One sync round with the paired computer. Waits for a running job (one at a time), needs a network, a pairing and a
    /// finished first load. Any failure is written to the log ("Sync failed: ...") and returned as a sentence; a stale
    /// position that needs a new first load is handled by <see cref="IBlindPhoneSync"/>, which marks the load as not done.
    /// </summary>
    public async Task<string> RequestSyncAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        SetActive("Sync", null);
        // Registered, so that "Disconnect and wipe" can stop this round and wait for it; refused while the wipe runs.
        using var operation = activity?.TryBegin(BlindActivity.Sync, ct);
        try
        {
            if (activity is not null && operation is null) return "The copy is being wiped; nothing was started.";
            ct = operation?.Token ?? ct;
            if (state.CallCode is not { } target) return "Not paired yet.";
            if (!state.InitialLoadDone) return "The first load is not done yet.";
            try
            {
                await sync.SyncOnceAsync(target, ct);
                state.LastSyncAt = time.GetUtcNow();
                return "Synced.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (BlindFeaturePendingException ex)
            {
                return ex.Message;
            }
            catch (Exception ex)
            {
                // Any exception, as the Android worker does: a sync that dies on something unexpected must not vanish.
                // With the inner exceptions' words: "The SSL connection could not be established, see inner exception." alone cannot tell a
                // refused pin from a closed port.
                var reason = BlindRunReport.Reason(ex);
                log.Add("sync", $"Sync failed: {reason}");
                return $"Sync failed: {reason}";
            }
        }
        finally
        {
            SetActive(null, null);
            _gate.Release();
        }
    }

    /// <summary>
    /// Copies a finished backup to the place the user chose with the host's picker. <paramref name="backupName"/> is a file
    /// name from <see cref="BlindAppStatus.Backups"/> (a path is reduced to its name: only files of the backup folder are exported).
    /// </summary>
    public async Task<long> ExportBackupAsync(string backupName, CancellationToken ct = default)
    {
        if (exporter is null) throw new InvalidOperationException("No service registered for IBlindBackupExporter.");
        var name = Path.GetFileName(backupName);
        var file = backups.Backups().FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal))
            ?? throw new FileNotFoundException("This backup is not in the list any more.", name);
        await using var target = await exporter.CreateAsync(file.Name, ct);
        var copied = await BlindBackupExport.CopyAsync(file.FullName, target, null, ct);
        log.Add("backup", $"Saved {file.Name} to the chosen place.");
        return copied;
    }

    /// <summary>
    /// Disconnect and wipe: the host stops its work, the keys, state and every file of the blind copy are removed, then
    /// the host returns to the first-run state (<see cref="IBlindLifecycle.RestartAfterWipe"/>). The running work is stopped and waited for
    /// first (<see cref="BlindPhoneReset.WipeAsync"/>). Throws an <see cref="AggregateException"/> naming what is left when the work did not
    /// stop in time (nothing deleted) or a step failed; call it again to finish.
    /// </summary>
    public async Task DisconnectAndWipeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await BlindPhoneReset.WipeAndRestartAsync(services, paths.DataDirectory, ct: ct).ConfigureAwait(false);
    }

    public BlindAppStatus GetStatus()
    {
        string? activeJob;
        double? progress;
        lock (_jobLock) { activeJob = _activeJob; progress = _jobProgress; }
        // A round the host's scheduler runs on its own (the Android sync worker registers in BlindActivity) is shown too.
        if (activeJob is null && activity?.IsActive(BlindActivity.Sync) == true) activeJob = "Sync";

        var paired = state.CallCode is not null;

        // The host's secret store can throw or block (a locked or denied keychain). That is not a lost key and must not push anyone to a
        // wipe: the status says that the store did not answer, and reports the two answers it could not give as "no".
        var keyLost = false;
        var awaiting = false;
        string? keyStoreProblem = null;
        try { keyLost = pairing.BackupKeyLost; }
        catch (Exception ex) when (!IsFatal(ex)) { keyStoreProblem = ShortReason(ex); }
        try { awaiting = pairing.AwaitingAnswer; }
        catch (Exception ex) when (!IsFatal(ex)) { keyStoreProblem ??= ShortReason(ex); }

        var failure = heavy.LastFailure is { } f && (f.Title != "First load" || !state.InitialLoadDone)
            ? new BlindAppFailure(f.Title, f.Message, f.At, f.Count)
            : null;
        return new BlindAppStatus(state.NodeId, state.DisplayName, paired,
            state.InitialLoadDone, state.LastSyncAt, state.LastBackupAt, state.Schedule, keyLost,
            activeJob, progress,
            backups.Backups().Select(file => new BlindAppBackup(file.Name, file.Length)).ToList(), log.Latest(30),
            Endpoint: state.CallCode?.Address, AwaitingAnswer: awaiting, StartError: _startError,
            KeyStoreUnavailable: keyStoreProblem, LastFailure: failure);
    }

    /// <summary>Type and short message of an exception, for a sentence on the screen: the store's own words, never a secret.</summary>
    private static string ShortReason(Exception ex)
    {
        var message = (ex.Message ?? "").ReplaceLineEndings(" ").Trim();
        if (message.Length > 120) message = message[..120] + "...";
        return message.Length == 0 ? ex.GetType().Name : $"{ex.GetType().Name}: {message}";
    }

    /// <summary>Tells the host that the screen is out of date. A handler that throws must not break the job or the call that raised the event.</summary>
    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) when (!IsFatal(ex)) { }
    }

    private string ChooseDisplayName()
    {
        var name = options?.DisplayNameFactory?.Invoke();
        return string.IsNullOrWhiteSpace(name) ? DefaultDisplayName : name.Trim();
    }

    private static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException or AccessViolationException or StackOverflowException or ThreadAbortException;

    /// <summary>Review note of stage 1: the job's progress reaches <see cref="BlindAppStatus.JobProgress"/>.</summary>
    private void HookProgress()
    {
        if (Interlocked.Exchange(ref _progressHooked, 1) == 1) return;
        heavy.Progress += (title, progress) =>
        {
            lock (_jobLock)
            {
                // Only while a job started through this controller runs. The heavy job names what it is doing now
                // ("First load", "Backup"); show that instead of the generic name.
                if (_activeJob is null) return;
                _activeJob = title;
                _jobProgress = progress;
            }
            RaiseChanged();
        };
    }

    private void SetActive(string? job, double? progress)
    {
        lock (_jobLock) { _activeJob = job; _jobProgress = progress; }
        RaiseChanged();
    }
}
