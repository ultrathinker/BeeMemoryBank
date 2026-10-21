using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// The sentences the Android pages say when something that is not the person's doing fails (the clipboard, the scanner, the QR picture, the state that cannot be read). They are FIXED: an exception of the
/// platform or the framework carries an alias, a path or a class of the implementation, and none of that belongs on a screen, in a notification or in a screenshot. The cause goes where the code can write it
/// without exposing it (the device's log, under its own rules); the page tells the person what to do. Failures of an operation the person asked for (rename, remove, add) are told by
/// <c>BusyAction.Describe</c>, the one rule of the apps for those.
/// </summary>
public static class BlindPageSentences
{
    /// <summary>The state of one copy could not be read (its page).</summary>
    public const string StatusUnreadable = "Could not read the state of this copy just now. Close the app and open it again; if this stays, remove the copy and add it again.";

    /// <summary>The list of copies could not be read (the accounts page and the add page).</summary>
    public const string DeviceUnreadable = "Could not read the copies on this phone just now. Close the app and open it again.";

    public const string PreviewFailed = "The invitation could not be checked just now. Paste it again, or close the app and open it again.";

    public const string QrNotMade = "The QR picture could not be made. Copy the code with the button below instead.";

    public const string CodeNotCopied = "The code could not be copied.";

    public const string RePairNotStarted = "A new phone code could not be made. Close the app and open it again, then try again.";

    public const string ClipboardUnreadable = "The clipboard could not be read.";

    public const string ScannerUnavailable = "The scanner could not be used on this phone.";

    /// <summary>The scheduler of the background work met an error that is not any account's (its detail is in the device log, which is not shown).</summary>
    public const string BackgroundProblem = "The background work met a problem and tries again at its next turn.";

    /// <summary>
    /// A refusal the shared core returned as a sentence (rename, limits, repairs), thrown so that the busy helper says it. The closed failure rule passes an exception's words on only when the exception is marked as this
    /// code's own sentence (<see cref="BlindSentenceException"/>); a plain <see cref="InvalidOperationException"/> would be told as "an unexpected problem" and the person would lose what to change.
    /// </summary>
    public static Exception Refusal(string sentence) => new BlindSentenceException(sentence);

    public const string FirstLoadNotFinished = "The first load did not finish. What happened is in the log below.";

    public const string SyncNotFinished = "The sync did not finish. What happened is in the log below.";

    public const string BackupNotFinished = "The backup did not finish. What happened is in the log below.";

    /// <summary>
    /// What the page says when a job it waited for has ended, by PHASE only. The failed outcome carries the job's own failure text (<see cref="BlindJobOutcome.Message"/>), which comes from the shared failure
    /// record and may name a path, an alias or a library of the phone: it is never shown by the page, which sends the person to the log (whose lines are the shared view's, cleaned in the shared core).
    /// An unreported job is told by the shared fixed sentence (<see cref="BlindJobWait.UnreportedText"/>), which names no failure at all.
    /// </summary>
    public static string JobEnd(BlindJobOutcome outcome, string kind, string finished, string failed, string ended) => outcome.Phase switch
    {
        BlindJobPhase.Finished => finished,
        BlindJobPhase.Failed => failed,
        BlindJobPhase.Unreported => BlindJobWait.UnreportedText(kind),
        _ => ended,
    };
}
