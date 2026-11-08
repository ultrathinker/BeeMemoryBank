using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Sync;

/// <summary>
/// One whitelist event this node holds about a peer: its id, type, version (Lamport time and author), payload, and where it stands in this
/// node's log (<paramref name="Sequence"/>: the order it arrived in here).
/// </summary>
public sealed record WhitelistFact(Guid EventId, string EventType, RowVersion Version, string Payload, long Sequence = 0)
{
    /// <summary>The fact as the event the applier takes (the signature is not needed: the event passed every gate when it was stored).</summary>
    public SyncEvent ToEvent() => new()
    {
        EventId = EventId, NodeId = Version.SourceNodeId, LamportTs = Version.LamportTs, EventType = EventType, Payload = Payload,
    };
}

/// <summary>
/// The <c>whitelist_add</c>, <c>whitelist_revoke</c> and <c>whitelist_update</c> events this node's log holds about one peer, in version order
/// (the order <see cref="ConflictResolver"/> ranks them in). Every such event the node accepted is there, whether it changed the row or lost to it:
/// that is what lets an add that arrives after a revoke learn of it, and an update that arrived before its add be applied after it. The log is
/// evidence of what is in it only: events below a compaction checkpoint are gone, and a row a join or a restore wrote has no event behind it.
/// </summary>
public static class WhitelistHistory
{
    private static readonly string[] Types = [EventTypes.WhitelistAdd, EventTypes.WhitelistRevoke, EventTypes.WhitelistUpdate];

    // The peer is named in the payload (whitelist events carry no entity id); a payload that is not JSON names nobody instead of failing the query.
    private const string PeerOfPayload = "lower(CASE WHEN json_valid(payload) THEN json_extract(payload, '$.node_id') END)";

    /// <summary>The whitelist events about <paramref name="peer"/>, oldest version first.</summary>
    public static async Task<List<WhitelistFact>> ForPeerAsync(IDbConnectionFactory db, Guid peer)
    {
        using var conn = db.CreateConnection();
        var rows = await conn.QueryAsync<(long Sequence, string EventId, string NodeId, long LamportTs, string EventType, string Payload)>(
            $@"SELECT sequence_num, event_id, node_id, lamport_ts, event_type, payload FROM tbl_event
               WHERE event_type IN @Types AND apply_state = {EventApplyStates.Applied} AND {PeerOfPayload} = @Peer",
            new { Types, Peer = peer.ToString("D") });
        var facts = new List<WhitelistFact>();
        foreach (var r in rows)
        {
            if (!Guid.TryParse(r.EventId, out var eventId) || !Guid.TryParse(r.NodeId, out var author)) continue;
            facts.Add(new WhitelistFact(eventId, r.EventType, new RowVersion(r.LamportTs, author), r.Payload, r.Sequence));
        }
        facts.Sort((x, y) => Compare(x.Version, y.Version));
        return facts;
    }

    /// <summary>Every peer the log holds a whitelist event about.</summary>
    public static async Task<List<Guid>> PeersAsync(IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        var ids = await conn.QueryAsync<string?>(
            $@"SELECT DISTINCT {PeerOfPayload} FROM tbl_event WHERE event_type IN @Types AND apply_state = {EventApplyStates.Applied}",
            new { Types });
        return ids.Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().Order().ToList();
    }

    /// <summary>Orders two versions the way <see cref="ConflictResolver.IncomingWins"/> ranks them: the older first.</summary>
    public static int Compare(RowVersion x, RowVersion y) =>
        x == y ? 0 : ConflictResolver.IncomingWins(existing: x, incoming: y) ? -1 : 1;

    /// <summary>The newest fact of <paramref name="eventType"/>, or null.</summary>
    public static WhitelistFact? Newest(IEnumerable<WhitelistFact> facts, string eventType) =>
        facts.Where(f => f.EventType == eventType).Aggregate((WhitelistFact?)null,
            (best, f) => best is null || ConflictResolver.IncomingWins(best.Version, f.Version) ? f : best);
}
