using BeeMemoryBank.BlindDesktop.Scheduling;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>A clock that moves only when a test says so. Timers fire, in order, inside <see cref="Advance"/>, on the calling thread.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset? start = null) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start ?? new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate) target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is { } due && due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }
                _now = next.Due!.Value;
            }
            next.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate) _timers.Remove(timer);
    }

    private DateTimeOffset Now()
    {
        lock (_gate) return _now;
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.Now() + dueTime;
            return true;
        }

        public void Fire()
        {
            Due = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero ? null : Due + _period;
            callback(state);
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>An app that does nothing real: it counts the calls, answers from a settable status, and can hold a job open until a test releases it.</summary>
internal sealed class FakeApp : IBlindAppController
{
    private int _running;
    private int _maxRunning;
    private readonly List<string> _calls = [];

    public event Action? Changed;

    public BlindAppStatus Status { get; set; } = StatusOf();
    public string SyncResult { get; set; } = "Synced.";
    public string HeavyResult { get; set; } = "Nothing due.";
    public string? AcceptError { get; set; } = "wrong code";
    public Func<CancellationToken, Task<string>>? HeavyBody { get; set; }
    public Func<CancellationToken, Task<string>>? SyncBody { get; set; }
    public Func<string, Task<long>>? ExportBody { get; set; }
    public Func<Task>? WipeBody { get; set; }
    public string? PairingCodeValue { get; set; }
    public BlindBackupSchedule? LastSchedule { get; private set; }
    public int MaxConcurrentJobs => _maxRunning;
    public List<bool> HeavyForce { get; } = [];
    public int SyncCalls { get; private set; }
    public int HeavyCalls => HeavyForce.Count;
    public int RePairCalls { get; private set; }
    public int WipeCalls { get; private set; }
    public List<string> Accepted { get; } = [];
    public List<string> Exported { get; } = [];

    public IReadOnlyList<string> Calls
    {
        get { lock (_calls) return _calls.ToList(); }
    }

    public void RaiseChanged() => Changed?.Invoke();

    public static BlindAppStatus StatusOf(bool paired = true, bool loaded = true, string? activeJob = null,
        double? progress = null, IReadOnlyList<BlindAppBackup>? backups = null, BlindBackupSchedule schedule = BlindBackupSchedule.Weekly,
        bool awaiting = false, bool keyLost = false, string? startError = null, string name = "Test PC",
        IReadOnlyList<BlindPhoneLog.Entry>? log = null, string? keyStoreUnavailable = null, BlindAppFailure? lastFailure = null) =>
        new(Guid.Parse("11111111-2222-3333-4444-555555555555"), name, paired, loaded, null, null, schedule, keyLost, activeJob, progress,
            backups ?? [], log ?? [], paired ? "https://127.0.0.1:5610" : null, awaiting, startError,
            keyStoreUnavailable, lastFailure);

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public BlindAppStatus GetStatus() => Status;
    public string? PairingCode() => PairingCodeValue;

    public string? AcceptCallCode(string text)
    {
        Accepted.Add(text);
        return AcceptError;
    }

    public void StartRePair() => RePairCalls++;
    public void SetSchedule(BlindBackupSchedule schedule) => LastSchedule = schedule;

    public async Task<string> RunHeavyAsync(bool forceBackup, CancellationToken ct = default)
    {
        HeavyForce.Add(forceBackup);
        Record("heavy" + (forceBackup ? "-force" : ""));
        return await Job(HeavyBody, HeavyResult, ct);
    }

    public async Task<string> RequestSyncAsync(CancellationToken ct = default)
    {
        SyncCalls++;
        Record("sync");
        return await Job(SyncBody, SyncResult, ct);
    }

    public Task<long> ExportBackupAsync(string backupName, CancellationToken ct = default)
    {
        Exported.Add(backupName);
        return ExportBody is null ? Task.FromResult(2048L) : ExportBody(backupName);
    }

    public Task DisconnectAndWipeAsync(CancellationToken ct = default)
    {
        WipeCalls++;
        return WipeBody is null ? Task.CompletedTask : WipeBody();
    }

    private async Task<string> Job(Func<CancellationToken, Task<string>>? body, string result, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _running);
        InterlockedMax(ref _maxRunning, now);
        try
        {
            return body is null ? result : await body(ct);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private void Record(string what)
    {
        lock (_calls) _calls.Add(what);
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int current;
        while ((current = Volatile.Read(ref location)) < value && Interlocked.CompareExchange(ref location, value, current) != current) { }
    }
}

internal sealed class FakeWorkRequests : IBlindWorkRequests
{
    public int Syncs;
    public int Backups;
    public int Heavies;
    public FirstLoadRetryInfo? FirstLoadRetry { get; set; }

    /// <summary>When set, every request throws it.</summary>
    public Exception? Failure { get; set; }

    public event Action<string>? UserJobReported;

    public void RequestSync() { if (Failure is { } f) throw f; Syncs++; }
    public void RequestBackup() { if (Failure is { } f) throw f; Backups++; }
    public void RequestHeavy() { if (Failure is { } f) throw f; Heavies++; }
    public void Report(string sentence) => UserJobReported?.Invoke(sentence);
}

internal sealed class FakeAutostart : IBlindAutostart
{
    public bool? Enabled { get; set; } = false;
    public Exception? Failure { get; set; }
    public bool? IsEnabled => Enabled;

    public void SetEnabled(bool enabled)
    {
        if (Failure is not null) throw Failure;
        Enabled = enabled;
    }
}
