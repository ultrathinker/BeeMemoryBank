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
        if (log.Latest(1) is [{ } last] && last.Kind == kind && last.Message == message) return;
        log.Add(kind, message);
    }

    /// <summary>
    /// A run that ended in an error nobody planned for (a damaged database, a missing identity row): the worker must
    /// not die silently in WorkManager's logcat, the screen's log is where the user can read it.
    /// </summary>
    public static void RecordFailure(BlindPhoneLog log, string kind, Exception error) =>
        Record(log, kind, $"Stopped by an unexpected error ({error.GetType().Name}): {error.Message}{Where(error)}");

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
