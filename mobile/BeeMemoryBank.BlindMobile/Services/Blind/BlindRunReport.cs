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
}
