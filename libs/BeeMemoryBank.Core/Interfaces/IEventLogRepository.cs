using System.Data;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IEventLogRepository
{
    /// <param name="local">The local columns (step-a-spec 6.1): an author's own sealed event keeps its opened type and article.</param>
    Task AppendAsync(SyncEvent evt, IDbTransaction? transaction = null, EventRowLocal? local = null);
    /// <summary>Atomically inserts the event if not already present. Returns true if inserted, false if duplicate.</summary>
    /// <param name="local">How this node holds it (applied, held, relayed) and who delivered it; null = applied, origin unknown.</param>
    Task<bool> AppendIfNotExistsAsync(SyncEvent evt, EventRowLocal? local = null);
    Task<bool> ExistsAsync(Guid eventId);
    /// <summary>
    /// Where this node's Lamport clock starts: the newest event's time, or the durable floor a snapshot join or a
    /// restore recorded, whichever is higher.
    /// </summary>
    Task<long> GetMaxLamportTimestampAsync();
    /// <summary>Events from this node after the specified position (for serving to other nodes).</summary>
    Task<List<SyncEvent>> GetAfterSequenceAsync(long afterSequenceNum, int limit = 1000);
    /// <summary>All events (all nodes) after the specified position (for internal synchronization).</summary>
    Task<List<SyncEvent>> GetAllAfterSequenceAsync(long afterSequenceNum, int limit = 1000);
    /// <summary>
    /// Recent events, newest first. Pass <paramref name="eventType"/> to filter at SQL level —
    /// filtering in memory after LIMIT silently drops matches when the window is smaller than the
    /// event stream.
    /// </summary>
    Task<List<SyncEvent>> GetRecentAsync(int limit = 50, int offset = 0, string? eventType = null);
    Task<int> GetTotalCountAsync();
    Task<List<SyncEvent>> GetByArticleAsync(Guid articleId, int limit = 50);
    Task<List<SyncEvent>> GetLocalEventsAfterSequenceAsync(Guid nodeId, long afterSequenceNum, int limit = 1000);
    /// <summary>All events except those originating from the specified node (for relay/gossip push).</summary>
    Task<List<SyncEvent>> GetEventsToRelayAsync(Guid excludeNodeId, long afterSequenceNum, int limit = 1000);
    Task<bool> IsHardDeletedAsync(string entityId, long lamportTs);
    Task<long?> GetMinSequenceAsync();
    Task<long> GetMaxSequenceAsync();
    Task<int> DeleteUpToAsync(long cpSequenceNum);

    /// <summary>
    /// Deletes comment_create events that carry the comment text in clear (encrypted = false with a
    /// non-empty text). Every peer refuses such an event, so it is dead weight whose only effect is
    /// keeping the text readable in the log. Returns how many were removed.
    /// </summary>
    Task<int> DeletePlaintextCommentEventsAsync();
    Task<long?> GetLastCompactionCpAsync();
    /// <summary>
    /// Returns the sequence_num of the Nth-oldest event (1-indexed). Returns null if the log has fewer than N events.
    /// Used by count-based compaction: "give me the seq of the event I'd delete up to, to leave exactly (total - N) events".
    /// </summary>
    Task<long?> GetSequenceAtRankAsync(int rank);
    /// <summary>
    /// Counts events with sequence_num greater than the given value — i.e., how many events a peer
    /// at that position would be "behind" in our log.
    /// </summary>
    Task<int> CountEventsAfterSequenceAsync(long seqNum);
    /// <summary>
    /// How many events a push to the given peer still has to carry after <paramref name="afterSequenceNum"/>: the same
    /// rows <see cref="GetEventsToRelayAsync"/> returns (everything but what the peer wrote itself, which is never sent back).
    /// The delivery status counts with this, not with <see cref="CountEventsAfterSequenceAsync"/>: a peer's own events are
    /// not "undelivered" to it.
    /// </summary>
    Task<int> CountEventsToRelayAfterAsync(Guid excludeNodeId, long afterSequenceNum);
    Task<SyncEvent?> GetByIdAsync(string eventId);

    // ─── Held events (step-a-spec 6) ────────────────────────────────────────

    /// <summary>True when any row is held (apply_state 1).</summary>
    Task<bool> AnyHeldAsync();
    /// <summary>Sequence numbers of held rows in log order, after <paramref name="afterSequenceNum"/>.</summary>
    Task<List<long>> GetHeldSequencesAsync(long afterSequenceNum, int limit);
    /// <summary>The row at <paramref name="sequenceNum"/> if it is held, with its receipt; null otherwise.</summary>
    Task<HeldEventRow?> GetHeldAsync(long sequenceNum);
    /// <summary>A held row applied: state 0, the opened view's type and article, the derived entity id and the actor columns.</summary>
    Task MarkHeldAppliedAsync(long sequenceNum, string? localType, Guid? localArticleId, string? entityId, string? actorType, string? actorName);
    /// <summary>A held row that still cannot apply now: the reason it waits for.</summary>
    Task SetHoldReasonAsync(long sequenceNum, string reason);
    /// <summary>A held row settled without applying: relayed (2) or quarantined after receipt (3).</summary>
    Task SetApplyStateAsync(long sequenceNum, int applyState);
}
