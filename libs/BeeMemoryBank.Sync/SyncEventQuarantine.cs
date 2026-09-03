using System.Collections.Concurrent;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Tracks events that repeatedly fail to apply during pull, and quarantines one once it has
/// failed too many times in a row (M5c).
///
/// <para>
/// Before this existed, <see cref="SyncClient"/>'s pull loop stopped at the FIRST event that threw
/// (bad signature, whitelist ordering, any other permanent failure) and left the sync cursor
/// exactly where it was: every subsequent cycle re-fetched the same page, hit the same event
/// first, and stopped again — forever. Two problems, not one: the node made zero forward progress
/// even on other, perfectly fine events later in that page, and nothing beyond a repeating log
/// line at WARNING/ERROR ever told an operator this was happening.
/// </para>
///
/// <para>
/// A TRANSIENT failure (a network blip mid-batch, a momentarily-locked local DB) should still stop
/// the loop and retry from the same position next cycle — that's the existing, correct behavior
/// for something that might just work next time. Only once the SAME event has failed
/// <see cref="QuarantineThreshold"/> times in a row does it get skipped: the cursor advances past
/// it and the rest of the page keeps being applied. This is a judgment call the codebase already
/// makes with the same shape for the replay shield and hard-delete gate (skip-and-move-on beats
/// blocking sync indefinitely on one bad event) but with an explicit, visible marker instead of a
/// silent drop.
/// </para>
///
/// <para>
/// Static and in-memory rather than DB-backed or DI-registered, for the same reason
/// <see cref="BeeMemoryBank.Core.Services.ArticleWriteLock"/> is static: a lightweight, process-wide
/// tracker needs neither a schema migration nor a DI registration to be useful here, and
/// <see cref="SyncClient"/> is constructed per sync-scope (<c>AddScoped</c>), so any per-instance
/// state would reset every cycle and never accumulate a streak. The tradeoff is that the streak
/// (and the quarantine itself) resets on process restart — acceptable, since a genuinely broken
/// event re-accumulates failures within a handful of cycles, and a restart is already a reasonable
/// point to give a fix (or an admin's manual event-log edit) another chance.
/// </para>
///
/// <para>
/// Keyed by the event's own EventId (globally unique) rather than (peer, EventId): the same
/// problem event can be pulled from more than one peer in a mesh via gossip relay, and all of
/// those pulls should count toward, and see, the same quarantine record.
/// </para>
/// </summary>
public static class SyncEventQuarantine
{
    /// <summary>Consecutive failures before an event is treated as permanently skipped.</summary>
    public const int QuarantineThreshold = 5;

    public sealed record Entry(
        Guid EventId,
        string EventType,
        Guid OriginNodeId,
        int FailureCount,
        DateTime FirstFailedAtUtc,
        DateTime LastFailedAtUtc,
        string LastError,
        bool Quarantined);

    private sealed class MutableEntry
    {
        public required string EventType;
        public required Guid OriginNodeId;
        public int FailureCount;
        public DateTime FirstFailedAtUtc;
        public DateTime LastFailedAtUtc;
        public required string LastError;
    }

    private static readonly ConcurrentDictionary<Guid, MutableEntry> Failures = new();

    /// <summary>
    /// Records one failed apply attempt for <paramref name="eventId"/>. Returns true once this
    /// event has now failed <see cref="QuarantineThreshold"/> times in a row and should be skipped
    /// rather than retried; false if the caller should keep the existing "stop and retry next
    /// cycle" behavior.
    /// </summary>
    public static bool RecordFailure(Guid eventId, string eventType, Guid originNodeId, string error)
    {
        var now = DateTime.UtcNow;
        var entry = Failures.AddOrUpdate(
            eventId,
            _ => new MutableEntry
            {
                EventType = eventType,
                OriginNodeId = originNodeId,
                FailureCount = 1,
                FirstFailedAtUtc = now,
                LastFailedAtUtc = now,
                LastError = error
            },
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.FailureCount++;
                    existing.LastFailedAtUtc = now;
                    existing.LastError = error;
                    return existing;
                }
            });

        lock (entry)
        {
            return entry.FailureCount >= QuarantineThreshold;
        }
    }

    /// <summary>Clears any tracked failures for an event that just applied successfully.</summary>
    public static void ClearFailure(Guid eventId) => Failures.TryRemove(eventId, out _);

    /// <summary>
    /// Every event with at least one recorded failure, most-failed first — surfaced via
    /// <c>GET /api/sync/quarantine</c> so an operator has somewhere to look beyond log lines.
    /// </summary>
    public static List<Entry> ListAll()
    {
        return Failures
            .Select(kvp =>
            {
                var e = kvp.Value;
                lock (e)
                {
                    return new Entry(
                        kvp.Key, e.EventType, e.OriginNodeId, e.FailureCount,
                        e.FirstFailedAtUtc, e.LastFailedAtUtc, e.LastError,
                        e.FailureCount >= QuarantineThreshold);
                }
            })
            .OrderByDescending(e => e.FailureCount)
            .ToList();
    }
}
