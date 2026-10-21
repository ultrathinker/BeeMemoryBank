using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// What a background run tells the log on the screen. The scheduled worker has no screen of its own, so the
/// message <see cref="BlindHeavyWork.RunAsync"/> returns would be lost ("waiting for the network's next
/// integrity anchor", "backup failed: no room"). The hourly runs that did nothing are not worth a line in a
/// log that keeps 500, and the same wait twice in a row is one line.
/// </summary>
public static class BlindRunReport
{
    // The messages BlindHeavyWork returns when there was nothing to do.
    private static readonly string[] Quiet = ["Nothing due.", "Not paired yet.", "Already running."];

    public static void Record(BlindPhoneLog log, string kind, string message)
    {
        if (string.IsNullOrWhiteSpace(message) || Quiet.Contains(message)) return;
        if (log.LatestStored(1) is [{ } last] && last.Kind == kind && last.Message == message) return;
        log.Add(kind, message);
    }

    /// <summary>
    /// A run that ended in an error nobody planned for (a damaged database, a missing identity row): the worker must
    /// not die silently in WorkManager's logcat, the screen's log is where the user can read it.
    /// </summary>
    public static void RecordFailure(BlindPhoneLog log, string kind, Exception error)
    {
        // The kind of failure (BlindFailureText), then the type's name when the sentence has not said it: never the message of the exception, which may carry a path, a key alias,
        // a request address or a library's words, and this line is kept on the phone and shown on its screen.
        var said = BlindFailureText.Say(error);
        var type = error.GetType().Name;
        Record(log, kind, $"Stopped by an unexpected error{(said.Contains(type, StringComparison.Ordinal) ? "" : $" ({type})")}: {said}{Where(error)}");
    }

    /// <summary>
    /// What a failed job or sync tells the log and the screen: <see cref="BlindFailureText.Say"/> - the KIND of failure (a refused connection, a certificate that was not accepted, no
    /// answer in time, an answer that cannot be read; a file, a database, a key), or a bounded sentence this code wrote itself. Never the message of the exception and never the message
    /// of anything inside it: the HTTP stack's own words carry the address that was dialled and the answer's reason phrase, and an I/O, database or crypto message carries a path, a
    /// key alias or a library's words. This is where the failure is RECORDED, so what is stored (the log line, <see cref="BlindHeavyFailure.Message"/>) is clean for every head.
    /// </summary>
    public static string Reason(Exception error) => BlindFailureText.Say(error);

    /// <summary>
    /// " [at A.B.M &lt; C.D.N &lt; ...]": the first few methods of the stack, names only (no arguments, no file paths). A Release
    /// build on a phone has no debugger and nothing prints a caught exception, so without this a NullReferenceException
    /// from deep in a backup says nothing about where.
    /// </summary>
    private static string Where(Exception error)
    {
        var frames = (error.StackTrace ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("at ", StringComparison.Ordinal))
            .Select(l => l[3..])
            .Select(l => l.Split(['(', ' '], 2)[0])
            .Where(l => l.Length > 0)
            .Take(MaxFrames)
            .ToList();
        return frames.Count == 0 ? "" : $" [at {string.Join(" < ", frames)}]";
    }

    private const int MaxFrames = 4;
}
