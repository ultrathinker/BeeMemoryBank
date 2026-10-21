using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Core.Services.BlindPhone;

/// <summary>How the last long job ended when it failed; see <see cref="BlindHeavyWork.LastFailure"/>.</summary>
/// <param name="Title">"First load" or "Backup".</param>
/// <param name="Message">What <see cref="BlindFailureText.Say"/> says about the failure, decided where it is recorded: the kind of failure or a sentence of this code, never the message of the exception
/// (a path, a key alias, a request address or a library's words). A head shows it as it is.</param>
/// <param name="Result">The sentence the run returned: "{Title} failed: {Message}".</param>
/// <param name="At">When the latest of the identical failures happened.</param>
/// <param name="Count">How many times in a row the job failed with this same sentence.</param>
public sealed record BlindHeavyFailure(string Title, string Message, string Result, DateTimeOffset At, int Count);

/// <summary>
/// The Android blind node's long jobs — the first load, then a backup when one is due or asked for —
/// run from the foreground service ("Back up now") or the scheduled worker.
/// </summary>
public sealed class BlindHeavyWork(
    BlindPhoneState state,
    IBlindReplicaSource replica,
    BlindPhoneBackupRunner backups,
    BlindPhoneLog log,
    string replicaWorkDirectory,
    TimeProvider time,
    BlindActivity? activity = null)
{
    /// <summary>What the sentence starts with when the device's gate is held by a job that was let go of (see <see cref="BlindDeviceGate"/>): not "Already running", and not a failure.</summary>
    internal const string DeviceBusyPrefix = "Paused: the device is busy";

    private BlindDeviceGate _deviceGate = new();
    private BlindHeavyFailure? _lastFailure;
    private string? _pausedWith;

    /// <summary>The limit guard, asked before a long job starts and by the HTTP pipeline during it. Null for a composition that has none (nothing is ever paused).</summary>
    public BlindLimitGuard? Guard { get; init; }

    /// <summary>Tells the source when a long job stops for a pause the source is told about.</summary>
    public BlindPauseReporter? Reporter { get; init; }

    /// <summary>What the job counted is saved when it ends.</summary>
    public BlindUsageLedger? Usage { get; init; }

    /// <summary>
    /// Told when the source answers a job's request with 401 or 403: the same seam the pull round uses (<see cref="BlindAppController.RequestSyncAsync"/>), so that an owner who revoked the device
    /// BEFORE its first load is seen too. Null for a composition without a host: the job then fails as it always did. An init property, like the others: the heads call the constructor.
    /// </summary>
    public IBlindSourceRemovalSink? RemovalSink { get; init; }

    /// <summary>What the copy holds is measured again when a long job ends (it changed: a load, a backup, a baseline).</summary>
    public BlindStoredBytes? Stored { get; init; }

    /// <summary>
    /// One long job at a time on the DEVICE: the gate the host hands to every account's runtime, or the one this instance made for itself (a copy with a single account).
    /// An init property, not a constructor parameter: the heads call the constructor, and a new type in its signature would put that type in theirs.
    /// </summary>
    public BlindDeviceGate DeviceGate
    {
        get => _deviceGate;
        init => _deviceGate = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The account this job belongs to, for the record of who holds the device's gate (set by the host; null for a copy with one account).</summary>
    internal string? GateOwnerId { get; init; }

    internal Func<string>? GateOwnerName { get; init; }

    /// <summary>
    /// The failure the job is in at the moment, or null (after a success, and before any failure). A job that keeps failing the same way is one
    /// line in the log the first time and again only at the 2nd, 4th, 8th ... failure ("(attempt N)"), so retries every few minutes do not
    /// push the useful lines out of the log; the count is here for the screen.
    /// </summary>
    public BlindHeavyFailure? LastFailure => Volatile.Read(ref _lastFailure);

    /// <summary>What the notification and the screen show while a job runs.</summary>
    public event Action<string, double>? Progress;

    /// <summary>
    /// Raised once when this account's job really starts work: the device's gate is held and no limit says "not now" (a pass that is refused, paused or has nothing to do for an unpaired copy raises
    /// nothing). A head that shows a foreground notification publishes it here, naming the account, instead of before it knows which account will run.
    /// </summary>
    public event Action? Started;

    /// <summary>Runs what is due. <paramref name="forceBackup"/> is the "Back up now" button.</summary>
    public Task<string> RunAsync(bool forceBackup, CancellationToken ct) => RunAsync(forceBackup, ct, activeOperation: null);

    /// <summary>
    /// Runs what is due while <paramref name="activeOperation"/> is registered, when a head has registered the whole work including its
    /// closing report. A scheduled worker has no such report and lets this method register its own operation.
    /// </summary>
    internal async Task<string> RunAsync(bool forceBackup, CancellationToken ct, BlindOperation? activeOperation)
    {
        // Registered before anything is touched: "Disconnect and wipe" waits for this job to end, and a job that starts while it runs is refused.
        using var registered = activeOperation is null ? activity?.TryBegin(BlindActivity.Heavy, ct) : null;
        var operation = activeOperation ?? registered;
        if (activity is not null && operation is null) return "The copy is being wiped; nothing was started.";
        ct = operation?.Token ?? ct;

        ct.ThrowIfCancellationRequested();
        var lease = _deviceGate.TryEnter(GateOwnerId, GateOwnerName);
        if (lease is null) return BusyText();
        try
        {
            Volatile.Write(ref _pausedWith, null);
            if (state.CallCode is not { } target) return "Not paired yet.";

            // A pause is not a failure: nothing starts, nothing is recorded as failed, and the next pass asks again.
            if (Guard?.Check() is { } held) return await PausedAsync(held, ct);

            // From here the job works: a head shows its notification for this account now (a failing listener must not stop the job).
            try { Started?.Invoke(); }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { }

            if (!state.InitialLoadDone)
            {
                var message = await GuardedAsync("load", "First load", async (progress, token) =>
                {
                    await replica.FetchAndInstallAsync(target, replicaWorkDirectory, progress, token);
                    state.InitialLoadDone = true;
                    log.Add("load", "First load finished.");
                    return "First load finished.";
                }, ct);
                if (!state.InitialLoadDone) return message;
            }

            // A newer baseline from the node this copy calls (docs/design/step-a-spec.md 13.3): this pass is the only place that downloads one.
            // A failure is noted for the screen and does not hold back a backup that is due.
            await GuardedAsync("update", "Baseline update", async (progress, token) =>
            {
                var check = await replica.CheckBaselineAsync(target, replicaWorkDirectory, progress, token);
                // A 304 or a baseline that was installed: the source authenticated this device and answered it, which is a contact like a sync round. A copy that holds no baseline asks nothing.
                if (check != BlindBaselineCheck.NotAsked) RemovalSink?.SourceAnsweredThisDevice();
                var updated = check == BlindBaselineCheck.Installed;
                if (updated) log.Add("update", "A newer baseline was installed.");
                return updated ? "A newer baseline was installed." : "The baseline is current.";
            }, ct);
            if (Interlocked.Exchange(ref _pausedWith, null) is { } paused) return paused;

            if (!forceBackup && !state.BackupDue(time.GetUtcNow())) return "Nothing due.";
            return await GuardedAsync("backup", "Backup", async (progress, token) =>
                (await backups.RunAsync(progress, token)).Message, ct);
        }
        finally
        {
            lease.Dispose();
            Usage?.Flush();
            // What the job set aside for its peak footprint is given back, and the folders are measured again: what it really left is the new baseline.
            Stored?.ReleaseJob();
            Stored?.Refresh();
        }
    }

    /// <summary>
    /// Why the gate could not be taken. When another account's job holds it and that job was let go of (it did not stop, and a thread cannot be killed), this is said in a sentence of its own
    /// that the scheduler knows (<see cref="DeviceBusyPrefix"/>): this account's job waits for that one, and is asked again after a wait that grows, not at once and not as a failure.
    /// Otherwise a job is simply running.
    /// </summary>
    private string BusyText()
    {
        if (_deviceGate.CurrentHolder is { Abandoned: true } holder && holder.OwnerId != GateOwnerId)
        {
            var who = string.IsNullOrWhiteSpace(holder.OwnerName) ? "another account" : holder.OwnerName;
            return $"{DeviceBusyPrefix}: the long job of {who} did not stop when it was asked to, so this one waits until it ends.";
        }
        return "Already running.";
    }

    private async Task<string> GuardedAsync(string kind, string title,
        Func<IProgress<double>, CancellationToken, Task<string>> work, CancellationToken ct)
    {
        var progress = new InlineProgress(p =>
        {
            Progress?.Invoke(title, p);
        });

        try
        {
            var result = await work(progress, ct);
            if (LastFailure is { } failed && failed.Title == title) Volatile.Write(ref _lastFailure, null);
            // The source answered and accepted the device: a refusal it gave earlier was a passing one. The first load says so here; the baseline check says so itself, and only when it asked (see
            // above). A backup downloads the node's current package unless a resumed run already holds it, and does not say which: it forgets nothing.
            if (kind == "load") RemovalSink?.SourceAnsweredThisDevice();
            return result;
        }
        catch (BlindFeaturePendingException ex)
        {
            return ex.Message;
        }
        catch (BlindPausedException ex)
        {
            // A limit said "not now" during the job: what was downloaded is kept (a first load resumes by byte range), and this is not a failure.
            var text = await PausedAsync(ex.Pause, ct);
            Volatile.Write(ref _pausedWith, text);
            return text;
        }
        catch (OperationCanceledException)
        {
            return $"{title} paused; it continues where it stopped.";
        }
        catch (HttpRequestException ex) when (BlindSourceRemoval.IsRefusal(ex) && RemovalSink is { } sink)
        {
            // The source answered 401 or 403 (to the challenge, the authentication or a download). ONE refusal proves little (a proxy or a firewall in front of a healthy source can say no for a
            // moment): it is remembered, this is not a failure ("First load failed" for ever) and the next pass asks again at its normal rhythm. A SECOND one, on a later try within a day, is the
            // source's decision - its owner revoked the device before the first load, or between two long jobs: the host marks the account removed by its owner, its card says so, and the
            // scheduler leaves it alone. A network error, a refused certificate or a 5xx is none of this and stays a failure below.
            var said = BlindSourceRemoval.Sentence(sink.SourceRefusedThisDevice());
            // Once for a burst of refused retries: the same line twice in a row is not written again (BlindRunReport.Record), and the controller does not repeat what the job has just said.
            BlindRunReport.Record(log, kind, said);
            return said;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException)
        {
            // The KIND of failure, whatever the exception wraps (the HTTP stack's own text carries the address that was dialled; an I/O message carries a path).
            return NoteFailure(kind, title, BlindFailureText.Say(ex));
        }
        finally
        {
            // However the phase ended it may have persisted something: the next admission walks the folders again.
            Stored?.Invalidate();
        }
    }

    /// <summary>The sentence of a pause; the source is told first when that is due. Never throws for a failed report.</summary>
    private async Task<string> PausedAsync(BlindPause pause, CancellationToken ct)
    {
        if (Reporter is { } reporter && state.CallCode is { } target)
        {
            try { await reporter.ReportIfDueAsync(target, pause, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* stopped: the sentence below is still true */ }
        }
        return BlindPauseText.Job(pause, time.LocalTimeZone, time.GetUtcNow());
    }

    private string NoteFailure(string kind, string title, string message)
    {
        var result = $"{title} failed: {message}";
        var previous = LastFailure;
        var count = previous is not null && previous.Result == result ? previous.Count + 1 : 1;
        Volatile.Write(ref _lastFailure, new BlindHeavyFailure(title, message, result, time.GetUtcNow(), count));
        if (count == 1) log.Add(kind, result);
        else if ((count & (count - 1)) == 0) log.Add(kind, $"{result} (attempt {count})");
        return result;
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
