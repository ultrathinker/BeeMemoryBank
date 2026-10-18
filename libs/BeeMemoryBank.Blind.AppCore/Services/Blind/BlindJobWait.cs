namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>Where a job the person asked for stands (see <see cref="BlindJobWait.Judge"/>).</summary>
public enum BlindJobPhase
{
    /// <summary>It is running now.</summary>
    Running,

    /// <summary>It was asked for and has not started yet (the system queues it); it may still.</summary>
    Queued,

    /// <summary>It has ended well: what it makes (the first load, a sync, a backup) is in the state.</summary>
    Finished,

    /// <summary>It ended with an error (<see cref="BlindJobOutcome.Message"/> says which).</summary>
    Failed,

    /// <summary>It did not start within the queue's grace and left no mark: nothing is known about it any more.</summary>
    Unreported,
}

/// <summary>What <see cref="BlindJobWait.Judge"/> found.</summary>
public sealed record BlindJobOutcome(BlindJobPhase Phase, string? Message = null)
{
    /// <summary>The phases in which the job is still on its way: the waiting goes on, and so does the lock.</summary>
    public bool IsOpen => Phase is BlindJobPhase.Running or BlindJobPhase.Queued;
}

/// <summary>One job the person asked for: what kind, when, and the marks it leaves that were there before.</summary>
public sealed record BlindJobAsk(string Kind, DateTimeOffset At, DateTimeOffset? SyncAtAsk, DateTimeOffset? BackupAtAsk);

/// <summary>
/// How a screen knows that the job it started has ended (BMB-311 fix round): from the STATE of the phone, not from a clock. The first version of the
/// screens waited for twenty seconds to see the job begin and for an hour to see it end, and let the button go when a limit passed - with the job possibly
/// still running, and a second tap free to ask for another. A job is open until the state says otherwise: it is running, or it has been asked for and the
/// system has not started it yet (a queue grace), and it ends when its mark appears (the first load is done, the time of the last sync or backup moved) or
/// when its failure is recorded. Only a job that was asked for, never started and left nothing - for longer than the grace - is "unreported", and the screen
/// says exactly that.
/// </summary>
public static class BlindJobWait
{
    public const string FirstLoad = "First load";
    public const string Sync = "Sync";
    public const string Backup = "Backup";

    /// <summary>How long a job that was asked for may stay unseen before it counts as unreported (WorkManager and the system queue can take a moment).</summary>
    public static readonly TimeSpan QueuedGrace = TimeSpan.FromMinutes(2);

    /// <summary>After this long the pop-up says that the job is still running and not to start it again. The wait itself has no end.</summary>
    public static readonly TimeSpan StillRunningAfter = TimeSpan.FromMinutes(30);

    public const string StillRunningText =
        "Still running. This is taking longer than usual: do not start it again. You can leave the app; the job goes on, and the screen shows when it has ended.";

    /// <summary>The sentence for a job that did not report: it is not said to have finished.</summary>
    public static string UnreportedText(string kind) =>
        $"The phone did not report that the {kind.ToLowerInvariant()} started or ended. Look at the log below; it will try again by itself.";

    /// <summary>Notes the state at the moment the job is asked for.</summary>
    public static BlindJobAsk Ask(string kind, BlindAppStatus status, DateTimeOffset now) => new(kind, now, status.LastSyncAt, status.LastBackupAt);

    /// <param name="running">The page's own knowledge that the job runs (the activity registry of the worker or the service).</param>
    public static BlindJobOutcome Judge(BlindJobAsk ask, BlindAppStatus status, bool running, DateTimeOffset now)
    {
        if (running || status.ActiveJob == ask.Kind) return new BlindJobOutcome(BlindJobPhase.Running);

        // A failure recorded after the ask is the job's own end (the first load and backups record theirs; a sync's is in the log).
        if (status.LastFailure is { } failure && failure.Title == ask.Kind && failure.At >= ask.At)
            return new BlindJobOutcome(BlindJobPhase.Failed, failure.Message);

        var finished = ask.Kind switch
        {
            FirstLoad => status.InitialLoadDone,
            Sync => status.LastSyncAt is { } sync && (ask.SyncAtAsk is not { } before || sync > before),
            Backup => status.LastBackupAt is { } backup && (ask.BackupAtAsk is not { } before || backup > before),
            _ => false,
        };
        if (finished) return new BlindJobOutcome(BlindJobPhase.Finished);

        return now - ask.At < QueuedGrace ? new BlindJobOutcome(BlindJobPhase.Queued) : new BlindJobOutcome(BlindJobPhase.Unreported, UnreportedText(ask.Kind));
    }
}
