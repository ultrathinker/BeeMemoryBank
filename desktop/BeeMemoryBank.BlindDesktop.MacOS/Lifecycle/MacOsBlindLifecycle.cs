using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.MacOS;

/// <summary>
/// What happens around "Disconnect and wipe" in a menu-bar app that keeps everything in one process. It stops the in-process scheduler
/// and the running jobs, and after the wipe it reports "restart needed" - the host then returns to its first-run state or relaunches;
/// this class decides nothing about windows or processes. The contract is the one of the Android class: <see cref="StopBackgroundWork"/>
/// and <see cref="StopBackupService"/> come first and may be called any number of times, <see cref="RestartAfterWipe"/> last.
///
/// <para>Jobs that the host starts (the scheduler's sync, a heavy job, a manual backup) should run with <see cref="JobToken"/>: stopping
/// cancels it, so a backup that is writing when the wipe starts stops at its next checkpoint instead of writing into a folder that is
/// being removed.</para>
///
/// <para>After the wipe it also removes the host's own state file and the copies of damaged ones (<see cref="MacOsBlindStateStore.Wipe"/>):
/// the core's wipe does not know them, and "a fresh app" should not keep host settings such as the marked networks. That is
/// presentation-level state without secrets, so a file that cannot be removed does not stop the restart; it is reported in
/// <see cref="WipeWarning"/>.</para>
/// </summary>
public sealed class MacOsBlindLifecycle : IBlindLifecycle
{
    private readonly IBlindScheduler? _scheduler;
    private readonly MacOsBlindStateStore? _state;
    private readonly object _gate = new();
    private CancellationTokenSource _jobs = new();

    /// <param name="scheduler">The host's in-process scheduler, when it has one registered; it is cancelled when the background work stops.</param>
    /// <param name="state">The host's state file, removed after a wipe; null when the host keeps its state elsewhere.</param>
    public MacOsBlindLifecycle(IBlindScheduler? scheduler = null, MacOsBlindStateStore? state = null)
    {
        _scheduler = scheduler;
        _state = state;
    }

    /// <summary>Set when the state file could not be removed after the last wipe (what is left holds no secret); otherwise null.</summary>
    public string? WipeWarning { get; private set; }

    /// <summary>Cancelled when the background work or the backup job is stopped. A new token takes its place after <see cref="RestartAfterWipe"/>.</summary>
    public CancellationToken JobToken
    {
        get { lock (_gate) return _jobs.Token; }
    }

    /// <summary>True from the end of a wipe until the host has acknowledged it.</summary>
    public bool RestartNeeded { get; private set; }

    /// <summary>Raised once per wipe, after the data is gone. The host shows the first-run state or relaunches.</summary>
    public event Action? RestartRequested;

    /// <summary>Stops the scheduler's timers and cancels what is running.</summary>
    public void StopBackgroundWork()
    {
        // The scheduler first, so that it cannot start another job in the gap; the jobs are cancelled even if it throws.
        try { _scheduler?.Cancel(); }
        finally { CancelJobs(); }
    }

    /// <summary>The Android class stops a foreground service here; a desktop has one process, so it is the running jobs.</summary>
    public void StopBackupService() => CancelJobs();

    public void RestartAfterWipe()
    {
        WipeHostState();
        lock (_gate)
        {
            // Back to the first-run state: the cancelled source is replaced, so what the host starts next is not born cancelled.
            // (The old source is not disposed: a concurrent Stop call may still hold it, and it owns no timer or handle.)
            _jobs = new CancellationTokenSource();
            RestartNeeded = true;
        }
        RestartRequested?.Invoke();
    }

    /// <summary>The host has shown the first-run state (or relaunched); the flag goes.</summary>
    public void AcknowledgeRestart()
    {
        WipeHostState();
        lock (_gate) RestartNeeded = false;
    }

    private void WipeHostState()
    {
        try
        {
            _state?.Wipe();
            WipeWarning = null;
        }
        catch (Exception ex) when (ex is AggregateException or IOException or UnauthorizedAccessException)
        {
            WipeWarning = "The host's state file was not removed after the wipe: " + ex.Message;
        }
    }

    private void CancelJobs()
    {
        CancellationTokenSource source;
        WipeHostState();
        lock (_gate) source = _jobs;
        // Not under the lock: a registered callback may run for a while, and may ask for the token.
        source.Cancel();
    }
}
