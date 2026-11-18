using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Sync;

/// <summary>The "somebody signs as this node" mark: how many such events arrived, and when the last one did.</summary>
public sealed record SignedAsThisNodeItem(long Count, string SetAt);

/// <summary>
/// Events that name THIS node as their author, carry a valid signature of one of its keys, and were not written here.
/// Only somebody holding this node's identity seed - a stolen backup - can make one. They are never treated as this node's own: the identity
/// re-key lists only events this node wrote (<c>origin_peer IS NULL</c>), and one arriving from outside raises the attention flag
/// <see cref="Flag"/> until an administrator acknowledges it on the identity page.
/// <para>An own event can also be missing from the log because this node removed it, so not every unknown own-signed event is a forgery. Own events
/// are written in order (each local append takes the next Lamport time and the next sequence number), so every own event dated after the oldest
/// own event still held above the last compaction cut is still in the log - except the types that are pruned on their own
/// (<see cref="PrunedOnTheirOwn"/>). An unknown own-signed event dated after that floor (or, when nothing own is held, after this node's clock) is
/// therefore not one this node wrote. A forgery dated before the floor cannot be told from an own event that compaction removed; and after a
/// restore from an older backup, events this node wrote after that backup come back and are marked too (the page says so).</para>
/// </summary>
public static class SignedAsThisNode
{
    /// <summary>The attention flag in this node's <c>node_state</c>.</summary>
    public const string Flag = "signed_as_this_node";

    /// <summary>
    /// The node-local markers (tbl_migration_marker), one per such event (prefix + event id): value <see cref="Open"/> until acknowledged, then
    /// <see cref="Seen"/>. Per event, so the same forged event retried from a quarantine or pushed again never raises the flag a second time.
    /// </summary>
    public const string MarkerPrefix = "attention-signed-as-this-node:";

    public const string Open = "open";
    public const string Seen = "seen";

    /// <summary>At most this many markers; past it one overflow marker stands for the rest (somebody floods this node with forged events).</summary>
    public const int MaxMarkers = 1000;

    /// <summary>Own event types that leave the log on their own, not only by compaction: their absence proves nothing.</summary>
    public static readonly string[] PrunedOnTheirOwn = [EventTypes.NodeState, EventTypes.RecoveryBoxSet, EventTypes.CommentCreate];

    /// <summary>
    /// True when <paramref name="evt"/> (signed by one of this node's keys, not in the log) cannot be an event this node wrote and later removed.
    /// </summary>
    public static async Task<bool> IsForeignAsync(IDbConnectionFactory db, SyncEvent evt, Guid self)
    {
        if (evt.NodeId != self || PrunedOnTheirOwn.Contains(evt.EventType, StringComparer.Ordinal)) return false;
        using var conn = db.CreateConnection();
        if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_event WHERE event_id = @id COLLATE NOCASE", new { id = evt.EventId.ToString("D") }) > 0)
            return false;
        var floor = await conn.ExecuteScalarAsync<long?>(
            @"SELECT MIN(lamport_ts) FROM tbl_event
              WHERE node_id = @self COLLATE NOCASE AND origin_peer IS NULL AND event_type NOT IN @pruned
                AND sequence_num > COALESCE((SELECT MAX(cp_after) FROM tbl_compaction_log), 0)",
            new { self = self.ToString("D"), pruned = PrunedOnTheirOwn });
        if (floor is { } f) return evt.LamportTs >= f;
        var clock = await conn.ExecuteScalarAsync<long?>("SELECT MAX(lamport_ts) FROM tbl_event") ?? 0;
        return evt.LamportTs > clock;
    }

    /// <summary>Marks <paramref name="evt"/> once; an event already marked (open or acknowledged) is left as it is. True when it is new.</summary>
    public static async Task<bool> RecordAsync(IDbConnectionFactory db, SyncEvent evt)
    {
        using var conn = db.CreateConnection();
        var now = DateTime.UtcNow.ToString("O");
        var key = MarkerPrefix + evt.EventId.ToString("D");
        if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration_marker WHERE key = @key", new { key }) > 0) return false;
        if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration_marker WHERE key LIKE @p", new { p = MarkerPrefix + "%" }) >= MaxMarkers)
            key = MarkerPrefix + "more";
        return await conn.ExecuteAsync(
            @"INSERT INTO tbl_migration_marker (key, value, set_at) VALUES (@key, @open, @now)
              ON CONFLICT(key) DO UPDATE SET value = excluded.value, set_at = excluded.set_at",
            new { key, open = Open, now }) > 0;
    }

    /// <summary>The events not acknowledged yet (how many, the latest), or null when there is none.</summary>
    public static async Task<SignedAsThisNodeItem?> GetAsync(IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        var row = await conn.QueryFirstAsync<(long Count, string? SetAt)>(
            "SELECT COUNT(*), MAX(set_at) FROM tbl_migration_marker WHERE key LIKE @p AND value = @open",
            new { p = MarkerPrefix + "%", open = Open });
        return row.Count > 0 ? new SignedAsThisNodeItem(row.Count, row.SetAt ?? "") : null;
    }

    /// <summary>Acknowledges every open mark (they stay, as seen: the same events never raise it again); true when there was one.</summary>
    public static async Task<bool> ClearAsync(IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        return await conn.ExecuteAsync("UPDATE tbl_migration_marker SET value = @seen WHERE key LIKE @p AND value = @open",
            new { seen = Seen, p = MarkerPrefix + "%", open = Open }) > 0;
    }
}

/// <summary>Raises <see cref="SignedAsThisNode.Flag"/> in this node's <c>node_state</c> while the mark is set.</summary>
public sealed class SignedAsThisNodeContributor(IDbConnectionFactory db) : INodeStateContributor
{
    public int Order => 51;

    public async Task ContributeAsync(NodeStateDraft draft, CancellationToken ct = default)
    {
        if (await SignedAsThisNode.GetAsync(db) is not null && !draft.AttentionFlags.Contains(SignedAsThisNode.Flag))
            draft.AttentionFlags.Add(SignedAsThisNode.Flag);
    }
}
