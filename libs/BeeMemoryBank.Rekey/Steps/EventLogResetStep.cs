using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Dapper;

namespace BeeMemoryBank.Rekey.Steps;

/// <summary>
/// The event log of the copy starts again (rekey-offline.md §2 step 3, §8.2; L). Every old event carries material of
/// the old vault (signed under the old identity chain, some sealed under old keys), and everything positioned in it
/// goes too: quarantine, pull and push positions, restore replay state, state anchors.
/// <para>What stays is a fresh checkpoint, written as compaction writes one:</para>
/// <list type="bullet">
/// <item>a <c>tbl_compaction_log</c> row whose <c>cp_after</c> is the old log's last sequence number. A peer that asks
///   for events from before it gets 410 and takes a snapshot, instead of reading an empty log as an empty vault;</item>
/// <item>a <c>snapshot_checkpoint</c> event, signed with the node's identity (sealed under D_c by the key-material step,
///   which runs first). Its Lamport timestamp is above every row's, and the node seeds its clock from the log at
///   start-up, so writes after the re-key still win over the rows the node already holds.</item>
/// </list>
/// The <c>AUTOINCREMENT</c> counter of <c>tbl_event</c> is kept, so new events number above the checkpoint.
/// </summary>
public sealed class EventLogResetStep : IRekeyStep
{
    public string Name => "EventLogReset";

    private static readonly string[] ClearedTables =
    [
        "tbl_event", "tbl_sync_quarantine", "tbl_sync_position", "tbl_sync_push_position",
        "tbl_restore_event_state", "tbl_restore_replay_shield", "tbl_state_anchor",
    ];

    public const string Reason = "rekey";

    public async Task<RekeyStepResult> RunAsync(RekeyContext ctx)
    {
        var db = ctx.Main;
        var counts = new Dictionary<string, long>();
        using var tx = db.BeginTransaction();

        var lastSeq = await db.ExecuteScalarAsync<long?>("SELECT seq FROM sqlite_sequence WHERE name = 'tbl_event'", transaction: tx)
                      ?? await db.ExecuteScalarAsync<long?>("SELECT MAX(sequence_num) FROM tbl_event", transaction: tx) ?? 0;
        var lamport = await RekeyLamport.MaxAsync(db, tx) + 1;

        foreach (var table in ClearedTables)
            counts[table] = await db.ExecuteAsync($"DELETE FROM {table}", transaction: tx);

        var now = DateTime.UtcNow;
        await db.ExecuteAsync(
            @"INSERT INTO tbl_compaction_log (compacted_at, cp_before, cp_after, events_removed, snapshot_file_name, reason)
              VALUES (@at, NULL, @cp, @removed, NULL, @reason)",
            new { at = now.ToString("o"), cp = lastSeq, removed = counts["tbl_event"], reason = Reason }, tx);

        var id = await db.QuerySingleAsync<(string NodeId, string Name, byte[] Pk, byte[]? Iv, long V)>(
            "SELECT node_id, display_name, ed25519_private_key, ed25519_private_key_iv, ed25519_private_key_v FROM tbl_node_identity LIMIT 1",
            transaction: tx);
        var payload = JsonSerializer.Serialize(new SnapshotCheckpointPayload(
            CpSeq: lastSeq, EventsRemoved: (int)counts["tbl_event"], SnapshotFileName: "", SnapshotSha256: "",
            PrevCheckpointSha256: null, ProducedAt: now));
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = Guid.Parse(id.NodeId),
            LamportTs = lamport,
            EventType = EventTypes.SnapshotCheckpoint,
            Payload = payload,
            Signature = [],
            ProtocolVersion = SyncProtocolVersion.Current,
            CreatedAt = now,
            ActorType = "system",
            ActorName = id.Name,
        };
        evt.Signature = NodeIdentityCrypto.SignWithIdentity(id.Pk, id.Iv, (int)id.V, evt.NodeId, ctx.Keys.CampaignDek,
            EventSignature.BuildPayload(evt));
        // Bound exactly as EventLogRepository.AppendAsync binds an event, so the row reads back the same way.
        await db.ExecuteAsync(
            @"INSERT INTO tbl_event (event_id, node_id, lamport_ts, event_type, article_id, entity_id, payload, signature,
                                     protocol_version, created_at, actor_type, actor_name, via_agent_name)
              VALUES (@EventId, @NodeId, @LamportTs, @EventType, @ArticleId, @EntityId, @Payload, @Signature,
                      @ProtocolVersion, @CreatedAt, @ActorType, @ActorName, @ViaAgentName)",
            evt, tx);

        tx.Commit();
        ctx.Progress.Report(Name, 1, 1);
        return new RekeyStepResult(Name, counts, [$"checkpoint:{lastSeq} lamport {lamport}"]);
    }

    public async Task<IReadOnlyList<RekeyProblem>> VerifyAsync(RekeyContext ctx)
    {
        var db = ctx.Main;
        var problems = new List<RekeyProblem>();
        foreach (var table in ClearedTables.Where(t => t != "tbl_event"))
            if (await db.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}") is var n and > 0)
                problems.Add(new(table, "*", $"{n} row(s) left"));

        var events = (await db.QueryAsync<(long Seq, string EventId, string NodeId, long Lamport, string Type, string Payload,
                byte[] Signature, long Protocol, DateTime CreatedAt)>(
            @"SELECT sequence_num, event_id, node_id, lamport_ts, event_type, payload, signature, protocol_version, created_at
              FROM tbl_event"))
            .Select(e => new SyncEvent
            {
                SequenceNum = e.Seq, EventId = Guid.Parse(e.EventId), NodeId = Guid.Parse(e.NodeId), LamportTs = e.Lamport,
                EventType = e.Type, Payload = e.Payload, Signature = e.Signature, ProtocolVersion = (int)e.Protocol,
                CreatedAt = e.CreatedAt,
            }).ToList();
        if (events.Count != 1 || events[0].EventType != EventTypes.SnapshotCheckpoint)
        {
            problems.Add(new("tbl_event", "*", $"{events.Count} event(s); only the fresh checkpoint may remain"));
            return problems;
        }

        var cp = events[0];
        var cpAfter = await db.ExecuteScalarAsync<long?>("SELECT MAX(cp_after) FROM tbl_compaction_log");
        if (cpAfter == null || cp.SequenceNum <= cpAfter)
            problems.Add(new("tbl_event", cp.EventId.ToString(), "the checkpoint does not number above the old log"));
        // The checkpoint itself is the log's highest timestamp; every row must sort below it.
        var highest = await RekeyLamport.MaxAsync(db);
        if (highest > cp.LamportTs)
            problems.Add(new("tbl_event", cp.EventId.ToString(), $"a row's Lamport timestamp {highest} is above the checkpoint's {cp.LamportTs}"));

        var pub = await db.ExecuteScalarAsync<byte[]>("SELECT ed25519_public_key FROM tbl_node_identity LIMIT 1");
        if (!Ed25519Signer.Verify(pub, EventSignature.BuildPayload(cp), cp.Signature))
            problems.Add(new("tbl_event", cp.EventId.ToString(), "the checkpoint's signature does not verify"));
        return problems;
    }
}
