namespace BeeMemoryBank.Api.Services;

/// <summary>
/// What <c>POST /api/join</c> and <c>POST /api/join/abort</c> share on the host. One gate serializes the step where a join looks at and
/// writes the joiner's row and the whole of an abort, so "does the row exist, which key does it hold" and the write that follows are
/// one step (two joins of one new id cannot both create the row, and an abort cannot run between a join's check and its write). And a
/// short-lived list of the joins an abort has cancelled: a joiner that gave up on a slow answer and aborted may still have its join
/// request arrive afterwards, and that request must not write a row nobody will ever take back. In memory and bounded: it is lost
/// when the host restarts (a late join then writes its row, as it always did, and the joiner's fallback message still names it).
/// </summary>
internal static class JoinAttemptGate
{
    /// <summary>How long after its row was written a join can still be taken back: a whole join (the key exchange, then the snapshot's 30-minute budget) fits.</summary>
    public static readonly TimeSpan AbortWindow = TimeSpan.FromHours(1);

    private static readonly TimeSpan CancelledFor = TimeSpan.FromHours(2);
    private const int MaxCancelled = 1000;

    public static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly Dictionary<Guid, DateTime> Cancelled = new();

    /// <summary>Records that the joiner of <paramref name="nodeId"/> aborted: a join of this id that comes later is refused.</summary>
    public static void Cancel(Guid nodeId)
    {
        var now = DateTime.UtcNow;
        lock (Cancelled)
        {
            foreach (var expired in Cancelled.Where(e => now - e.Value > CancelledFor).Select(e => e.Key).ToList())
                Cancelled.Remove(expired);
            if (Cancelled.Count >= MaxCancelled)
                Cancelled.Remove(Cancelled.MinBy(e => e.Value).Key);
            Cancelled[nodeId] = now;
        }
    }

    public static bool IsCancelled(Guid nodeId)
    {
        lock (Cancelled)
            return Cancelled.TryGetValue(nodeId, out var at) && DateTime.UtcNow - at <= CancelledFor;
    }
}
