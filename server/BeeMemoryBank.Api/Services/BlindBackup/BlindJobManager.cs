using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Api.Services.BlindStatus;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services.BlindBackup;

/// <summary>
/// Serializes the blind node's own long-running work — backups, repo checks, the wipe — and owns
/// the CPU mode they run under (plan §8). One job at a time, always: a blind node is a small box
/// and restic already saturates whatever it is given.
///
/// <para>The mode is not advisory. It is applied three ways: every restic child gets
/// <c>GOMAXPROCS=1</c> and idle I/O plus lowest CPU priority in Economy (~90 % of the cores in
/// Fast); the node's own phases wait on a gate while Pause is set; and a running restic child is
/// stopped outright with SIGSTOP on Linux — for a subprocess, not reading its stdout for a while
/// is not pausing, it is a buffer deadlock waiting to happen.</para>
/// </summary>
public sealed class BlindJobManager
{
    private readonly string _jobsFile;
    private readonly string _modeFile;
    private readonly ILogger<BlindJobManager> _logger;
    private readonly object _sync = new();

    private RunningJob? _current;
    private List<JobRecord> _history = [];
    private BlindCpuMode _mode = BlindCpuMode.Economy;
    // Completed while not paused. Entering Pause swaps in a fresh unsignaled one; leaving Pause
    // completes it again. Awaiters hold the instance they read, so a resume released right after
    // a pause still wakes everyone who was parked.
    private TaskCompletionSource _resume = Completed();
    // Every restic process alive right now, one entry per invocation: Pause and the wipe must
    // reach each of them, and one invocation ending must not unregister another.
    private readonly HashSet<Process> _children = [];

    public BlindJobManager(string dataPath, ILogger<BlindJobManager> logger)
    {
        _logger = logger;
        var dir = Path.Combine(dataPath, "blind");
        Directory.CreateDirectory(dir);
        _jobsFile = Path.Combine(dir, "jobs.json");
        _modeFile = Path.Combine(dir, "cpu-mode");
        LoadHistory();
        LoadMode();
    }

    private static TaskCompletionSource Completed()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    public BlindCpuMode Mode
    {
        get { lock (_sync) return _mode; }
    }

    /// <summary>
    /// Switches the CPU mode. Entering Pause freezes the running job (SIGSTOP for the restic
    /// child); leaving it resumes. Idempotent.
    /// </summary>
    /// <returns>False while the wipe holds the manager: nothing may resume or stop processes then.</returns>
    public bool SetMode(BlindCpuMode mode)
    {
        lock (_sync)
        {
            if (_wiping) return false;
            var was = _mode;
            if (was == mode) return true;
            _mode = mode;

            if (mode == BlindCpuMode.Pause)
            {
                _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                SignalChildren(sigstop: true);
            }
            else
            {
                _resume.TrySetResult();
                SignalChildren(sigstop: false);
            }
            SaveMode();
            _logger.LogInformation("Blind CPU mode: {From} -> {To}", was, mode);
            return true;
        }
    }

    // The mode survives a restart: an operator who paused the node's work (say, while the host
    // does something heavier) must not find it silently back to Economy after a container
    // restart. Unreadable or absent means the default, Economy.
    private void LoadMode()
    {
        try
        {
            if (File.Exists(_modeFile) &&
                BlindCpuModeExtensions.FromName(File.ReadAllText(_modeFile).Trim()) is { } saved)
            {
                _mode = saved;
                if (saved == BlindCpuMode.Pause)
                    _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blind CPU mode file unreadable, starting in economy");
        }
    }

    private void SaveMode()
    {
        try { File.WriteAllText(_modeFile, _mode.Name()); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not persist the blind CPU mode"); }
    }

    /// <summary>Awaited by the node's own phases whenever the mode may have flipped to Pause.</summary>
    public Task WaitForResumeAsync(CancellationToken ct)
    {
        TaskCompletionSource gate;
        lock (_sync) gate = _resume;
        return gate.Task.WaitAsync(ct);
    }

    /// <summary>Progress callback from <see cref="BlindJobContext"/>; computes speed/ETA from byte samples.</summary>
    internal void Report(RunningJob job, double? fraction, long? bytesDone, long? totalBytes, string? detail)
    {
        if (fraction is < 0 or > 1) return; // a restic status glitch is not worth failing a backup over
        lock (_sync)
        {
            job.Progress = fraction;
            if (detail != null) job.Detail = detail;

            var now = DateTime.UtcNow;
            if (bytesDone is not { } done) return;

            if (job.LastSample is { } prev && now > prev.At && done > prev.Bytes)
            {
                var speed = (long)((done - prev.Bytes) / (now - prev.At).TotalSeconds);
                // Smooth: restic's counter jumps in pack-sized steps; a raw single-sample delta
                // would swing the console's speed readout wildly.
                job.SpeedBytesPerSec = job.SpeedBytesPerSec is { } prevSpeed ? (prevSpeed + speed) / 2 : speed;
                if (totalBytes is { } total && total > done && job.SpeedBytesPerSec > 0)
                    job.Eta = now.AddSeconds((total - done) / job.SpeedBytesPerSec.Value);
            }
            job.LastSample = (now, done);
        }
    }

    /// <summary>The restic runner parks each child here so Pause can stop the process, not just our reads.</summary>
    public IDisposable RegisterChild(Process child)
    {
        lock (_sync)
        {
            _children.Add(child);
            if (_mode == BlindCpuMode.Pause) Signal(child, sigstop: true);
        }
        return new ChildRegistration(this, child);
    }

    private sealed class ChildRegistration(BlindJobManager owner, Process child) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._sync) owner._children.Remove(child);
        }
    }

    private void SignalChildren(bool sigstop)
    {
        foreach (var child in _children)
            Signal(child, sigstop);
    }

    private void Signal(Process child, bool sigstop)
    {
        // kill(1), not Process.Kill: STOP/CONT suspend without terminating. The blind node is a
        // Linux container, so /bin/kill exists; if it somehow does not, the gate still holds OUR
        // phases and the child keeps running — degraded, never wrong.
        if (OperatingSystem.IsWindows()) return;
        try
        {
            if (child.HasExited) return;
            var sig = sigstop ? "-STOP" : "-CONT";
            using var k = Process.Start(new ProcessStartInfo("/bin/kill", $"{sig} {child.Id}"));
            k?.WaitForExit(500);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not {Sig} a restic child", sigstop ? "SIGSTOP" : "SIGCONT");
        }
    }

    // ── job lifecycle ───────────────────────────────────────────────────────

    /// <summary>
    /// Busy while the slot holds a job at all — not while its State says Running. The worker sets
    /// the final State before its finally clears the slot; judging by State, a TryStart in that gap
    /// would put a new job in the slot, and the old finally would then null the NEW job out.
    /// </summary>
    public bool IsBusy
    {
        get { lock (_sync) return _current is not null; }
    }

    /// <summary>
    /// Starts a job unless one is already in flight. Returns null when the manager refused — the
    /// endpoints turn that into a 409 rather than queueing silently (an operator pressing "Back up
    /// now" twice must learn the first one is still running, not discover two snapshots later).
    /// </summary>
    public RunningJob? TryStart(string kind, Func<BlindJobContext, CancellationToken, Task> work)
    {
        lock (_sync)
        {
            if (_wiping || IsBusy) return null;
            var job = new RunningJob { Kind = kind, Cts = new CancellationTokenSource() };
            job.ModeName = _mode.Name();
            _current = job;
            var token = job.Cts!.Token;
            _ = Task.Run(() => RunAsync(job, work, token));
            return job;
        }
    }

    private async Task RunAsync(RunningJob job, Func<BlindJobContext, CancellationToken, Task> work, CancellationToken ct)
    {
        var ctx = new BlindJobContext(job, this);
        job.State = BlindJobState.Running;
        try
        {
            await work(ctx, ct);
            job.State = BlindJobState.Done;
        }
        catch (OperationCanceledException)
        {
            job.State = BlindJobState.Cancelled;
        }
        catch (Exception ex)
        {
            job.State = BlindJobState.Failed;
            job.Detail = ex.Message;
            _logger.LogError(ex, "Blind job {Kind} {Id} failed", job.Kind, job.Id);
        }
        finally
        {
            job.FinishedAt = DateTime.UtcNow;
            lock (_sync)
            {
                _history.Insert(0, job.ToRecord());
                if (_history.Count > 50) _history.RemoveAt(_history.Count - 1);
                _current = null;
                // After the slot is free, never before: whoever awaits Completion and starts the
                // next job must find the manager idle (the Sunday check lost that race to
                // FinishedAt, which is set before the lock).
                job.Done.TrySetResult();
                SaveHistory();
            }
        }
    }

    /// <summary>When a job of this kind last started — running or from the persisted history; null if never.</summary>
    public DateTime? LastStartedAt(string kind)
    {
        lock (_sync)
        {
            DateTime? last = _current is { } cur && cur.Kind == kind ? cur.StartedAt : null;
            foreach (var h in _history)
                if (h.Kind == kind && (last is null || h.StartedAt > last))
                    last = h.StartedAt;
            return last;
        }
    }

    /// <summary>Completes when no job is in flight (at once if none is).</summary>
    public Task WhenIdleAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_current is { } cur) return cur.Completion.WaitAsync(ct);
            // A wipe in progress is not idle for a waiter that wants to start a job next.
            return _wiping ? _wipeEnded.Task.WaitAsync(ct) : Task.CompletedTask;
        }
    }

    // ── the wipe gate ───────────────────────────────────────────────────────

    private bool _wiping;
    // Snapshot listings in flight: not jobs, but restic runs all the same. Counted under the same
    // lock as the gate, so a listing and a wipe cannot both believe they came first.
    private int _readers;
    private TaskCompletionSource _wipeEnded = Completed();

    public bool IsWiping
    {
        get { lock (_sync) return _wiping; }
    }

    /// <summary>
    /// Closes the manager for "Disconnect and wipe" and drains it. The gate goes up BEFORE the
    /// cancel, in the same lock: from here on neither the schedule nor an operator can start a
    /// job, and no mode change can resume or stop a process. A paused job is released (its phases
    /// wake and see the cancellation; a SIGSTOPped restic is continued so the kill lands on a
    /// running process), then the job is cancelled.
    /// </summary>
    /// <returns>True once no job and no restic process is alive. False when that did not happen
    /// within <paramref name="drainTimeout"/> — the gate is lowered again and the caller must not
    /// delete anything: a job that outlived its cancellation may still be writing.</returns>
    public async Task<bool> BeginWipeAsync(TimeSpan drainTimeout)
    {
        Task idle;
        lock (_sync)
        {
            if (_wiping) return false;
            _wiping = true;
            _wipeEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Economy in memory only; the mode file goes with the wiped files.
            _mode = BlindCpuMode.Economy;
            _resume.TrySetResult();
            SignalChildren(sigstop: false);
            _current?.Cts?.Cancel();
            idle = _current?.Completion ?? Task.CompletedTask;
        }

        var deadline = DateTime.UtcNow + drainTimeout;
        if (await Task.WhenAny(idle, Task.Delay(drainTimeout)) == idle)
        {
            // Children end with their invocation (the runner kills on cancel and waits); a
            // snapshot list, which is no job, drains here too.
            while (DateTime.UtcNow < deadline)
            {
                lock (_sync)
                    if (_current is null && _children.Count == 0 && _readers == 0) return true;
                await Task.Delay(50);
            }
        }

        _logger.LogError("Blind wipe refused: a job or restic process did not stop within {Timeout}", drainTimeout);
        EndWipe();
        return false;
    }

    /// <summary>
    /// Reserves a short repository read (the console's snapshot list) — null while a job holds
    /// the slot or the wipe holds the gate. Taken in the gate's lock, so a wipe that starts after
    /// this returns waits for the returned handle to be disposed before deleting anything.
    /// </summary>
    public IDisposable? TryReserveRead()
    {
        lock (_sync)
        {
            if (_wiping || _current is not null) return null;
            _readers++;
            return new ReadReservation(this);
        }
    }

    private sealed class ReadReservation(BlindJobManager owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                lock (owner._sync) owner._readers--;
        }
    }

    /// <summary>Reopens the manager after the wipe (or after a refused one).</summary>
    public void EndWipe()
    {
        lock (_sync)
        {
            _wiping = false;
            _wipeEnded.TrySetResult();
        }
    }

    /// <summary>The <c>jobs[]</c> section of /api/blind/status: the running job first, then history.</summary>
    public List<BlindJobStatus> Snapshot()
    {
        lock (_sync)
        {
            var list = new List<BlindJobStatus>();
            if (_current is { State: BlindJobState.Queued or BlindJobState.Running } cur)
            {
                cur.ModeName = _mode.Name();
                list.Add(cur.ToStatus());
            }
            foreach (var h in _history.Take(20))
                list.Add(new BlindJobStatus(h.Id, h.Kind, h.StateName, null, h.Progress, null, null, h.Detail,
                    h.StartedAt, h.FinishedAt));
            return list;
        }
    }

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(_jobsFile))
                _history = JsonSerializer.Deserialize<List<JobRecord>>(File.ReadAllText(_jobsFile)) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Blind job history unreadable, starting empty");
            _history = [];
        }
    }

    private void SaveHistory()
    {
        try
        {
            var tmp = _jobsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_history));
            File.Move(tmp, _jobsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist blind job history");
        }
    }
}

public enum BlindJobState
{
    Queued,
    Running,
    Done,
    Failed,
    Cancelled,
}

/// <summary>Mutable state of the one running job; read and written only under the manager's lock.</summary>
public sealed class RunningJob
{
    // Time to the second plus a random suffix: the history outlives the process, so a counter
    // that restarts at 1 would hand out ids the persisted jobs already carry.
    public string Id { get; init; } = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Random.Shared.Next(0x10000):x4}";
    public string Kind { get; init; } = "";
    public BlindJobState State { get; set; } = BlindJobState.Queued;
    public double? Progress { get; set; }
    public long? SpeedBytesPerSec { get; set; }
    public DateTime? Eta { get; set; }
    public string? Detail { get; set; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }

    internal CancellationTokenSource? Cts { get; set; }
    internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the job has finished AND the manager's slot is free again.</summary>
    public Task Completion => Done.Task;
    internal string? ModeName { get; set; }
    // Last progress sample, for speed/ETA computation.
    internal (DateTime At, long Bytes)? LastSample { get; set; }

    internal string StateName() => State.ToString().ToLowerInvariant();

    public BlindJobStatus ToStatus() =>
        new(Id, Kind, StateName(), ModeName, Progress, SpeedBytesPerSec, Eta, Detail, StartedAt, FinishedAt);

    internal JobRecord ToRecord() => new()
    {
        Id = Id,
        Kind = Kind,
        StateName = StateName(),
        Progress = Progress,
        Detail = Detail,
        StartedAt = StartedAt,
        FinishedAt = FinishedAt,
    };
}

/// <summary>
/// Persisted form of a finished (or crashed) job — {dataPath}/blind/jobs.json, capped at 50. Job
/// state survives a restart so the console can still say when the last backup ran and how it
/// ended — including how far a failed one got; progress of a killed job is not kept — it is gone
/// with the process.
/// </summary>
public sealed class JobRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("state")] public string StateName { get; set; } = "";
    [JsonPropertyName("progress")] public double? Progress { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("started_at")] public DateTime StartedAt { get; set; }
    [JsonPropertyName("finished_at")] public DateTime? FinishedAt { get; set; }
}

/// <summary>
/// Handed to a job's work delegate: the job reports progress here (bytes preferred — speed and
/// ETA are computed from byte samples; a bare fraction is accepted when bytes are unknown), and
/// awaits <see cref="WaitForResumeAsync"/> between its own steps so Pause applies to node-side
/// work too, not only to subprocesses.
/// </summary>
public sealed class BlindJobContext(RunningJob job, BlindJobManager manager)
{
    public RunningJob Job => job;

    public void ReportProgress(double? fraction, long? bytesDone, long? totalBytes, string? detail = null)
    {
        manager.Report(job, fraction, bytesDone, totalBytes, detail);
    }

    public Task WaitForResumeAsync(CancellationToken ct) => manager.WaitForResumeAsync(ct);
}
