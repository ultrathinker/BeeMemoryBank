using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// events a node stored while it ran an older build, which did not know their type, are applied after its update -
/// once, in log order, through the normal verifier and applier. "Upgraded" here: the events are put in the log as an older build left them
/// (stored, never applied), then the replay of a start of this build runs.
/// </summary>
public class LateEventReplayTests : SyncTestFixture
{
    private Guid _peer;
    private byte[] _peerPub = null!;
    private byte[] _peerSeed = null!;
    private Guid _admin2;
    private byte[] _admin2Seed = null!;
    private long _lamport = 100;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "Receiver", Password);
        (_peerPub, _peerSeed) = Ed25519Signer.GenerateKeyPair();
        _peer = Guid.NewGuid();
        await AddPeerAsync(_peer, _peerPub, "Office");
        (var admin2Pub, _admin2Seed) = Ed25519Signer.GenerateKeyPair();
        _admin2 = Guid.NewGuid();
        await AddPeerAsync(_admin2, admin2Pub, "Laptop");
    }

    private Task AddPeerAsync(Guid id, byte[] pub, string name) => WhitelistRepo.CreateAsync(new WhitelistEntry
    {
        NodeId = id, DisplayName = name, Ed25519PublicKey = pub, Status = "A", IsSuperadmin = true, Ownership = OwnershipKinds.Shared,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    });

    private SyncEvent Signed(Guid author, byte[] seed, string type, object payload, long? at = null)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = author, LamportTs = at ?? ++_lamport, EventType = type, Payload = JsonSerializer.Serialize(payload),
            ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow, Signature = [],
        };
        evt.EntityId = EventEntityId.Derive(evt);
        evt.Signature = Ed25519Signer.Sign(seed, EventSignature.BuildPayload(evt));
        return evt;
    }

    private SyncEvent NodeState(Guid author, byte[] seed, byte[] pub, string version, long? at = null) => Signed(author, seed, EventTypes.NodeState,
        new NodeStatePayload(author.ToString("D"), version, "linux", NodeRuntimeKinds.Server, NodeStateDraft.IdentityFingerprint(pub), null), at);

    /// <param name="own">The peer's own events the rotation lists (what it still holds above <paramref name="checkpoint"/>).</param>
    private (SyncEvent Event, NodeKeyHistoryEntry Entry, byte[] NewSeed) Rotate(byte[] oldPub, byte[] oldSeed, long counter = 1, long checkpoint = 0,
        IEnumerable<Guid>? own = null)
    {
        var (_, newSeed) = Ed25519Signer.GenerateKeyPair();
        var (evt, entry) = NodeKeyRotationEvents.Rotation(_peer, oldPub, b => Ed25519Signer.Sign(oldSeed, b), newSeed, counter, ++_lamport, checkpoint,
            own ?? [], "test", SyncProtocolVersion.Current, "Office");
        return (evt, entry, newSeed);
    }

    /// <summary>What a 2.10 or 2.11 build did with an event of a type it did not know: it stored the row (applied, as it saw it) and nothing else.</summary>
    private Task StoredByAnOlderBuildAsync(SyncEvent evt, int applyState = EventApplyStates.Applied) =>
        EventLogRepo.AppendIfNotExistsAsync(evt, new EventRowLocal(ApplyState: applyState));

    private Task<int> StartAsync() => LateEventReplay.RunAsync(Factory, NodeRepo, EventApplier, EventLogRepo, NullLogger.Instance);

    private async Task<(long Tombstones, long Processes)> RemovalRowsAsync(Guid person, Guid process)
    {
        using var c = Factory.CreateConnection();
        return (await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_removed_person WHERE person_id = @p COLLATE NOCASE AND outcome = 'deleted'",
                    new { p = person.ToString("D") }),
                await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_offboarding WHERE process_id = @p COLLATE NOCASE AND origin = 'replicated'",
                    new { p = process.ToString("D") }));
    }

    private async Task<long> RecordsAsync()
    {
        using var c = Factory.CreateConnection();
        return await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration_marker WHERE key LIKE @p", new { p = LateEventReplay.UnknownMarkerPrefix + "%" });
    }

    [Fact]
    public async Task Every_2_12_type_an_older_build_stored_is_applied_once_in_log_order_and_a_second_start_changes_nothing()
    {
        // While this node ran 2.10: the second superadmin's node removed a person (S0: the tombstone and the process state, then S1's request for
        // new identity keys), the peer announced its state, gave itself a new key, somebody with its old seed announced another one (a conflict),
        // and the second superadmin confirmed the genuine one. Every row is stored, nothing applied.
        var person = Guid.NewGuid();
        var process = Guid.NewGuid();
        var removedAt = DateTime.UtcNow.AddMinutes(-5);
        var tombstone = Signed(_admin2, _admin2Seed, EventTypes.PersonRemoved, new PersonRemovedPayload(person.ToString("D"), "deleted", removedAt));
        var processState = Signed(_admin2, _admin2Seed, EventTypes.OffboardingState, new OffboardingStatePayload(process.ToString("D"),
            person.ToString("D"), _admin2.ToString("D"), _admin2.ToString("D"), "delete", 1, removedAt));
        var rekeyRequest = Signed(_admin2, _admin2Seed, EventTypes.NodeRekeyRequest,
            new NodeRekeyRequestPayload(process.ToString("D"), EventApplier.RemovalRekeyReason(process), [_peer.ToString("D")]));
        var state = NodeState(_peer, _peerSeed, _peerPub, "2.12.0");
        var (thief, _, _) = Rotate(_peerPub, _peerSeed);
        var (genuine, genuineEntry, genuineSeed) = Rotate(_peerPub, _peerSeed, own: [state.EventId]);
        var confirm = NodeKeyRotationEvents.Confirm(_admin2, b => Ed25519Signer.Sign(_admin2Seed, b), _peer, genuine.EventId, genuineEntry.Counter,
            genuineEntry.NewPublicKey, ++_lamport, SyncProtocolVersion.Current, "Laptop");
        foreach (var evt in new[] { tombstone, processState, rekeyRequest, state, thief, genuine, confirm })
            await StoredByAnOlderBuildAsync(evt);
        (await new NodeStateStore(Factory).GetAsync(_peer)).Should().BeNull("precondition: an older build applied none of it");
        (await WhitelistRepo.GetByNodeIdAsync(_peer))!.Ed25519PublicKey.Should().Equal(_peerPub);
        (await RemovalRowsAsync(person, process)).Should().Be((0L, 0L), "precondition: no tombstone, no process");

        (await StartAsync()).Should().Be(7, "the first start of this build applies each of them");

        // The removal that began while this node was old: its tombstone is kept (and enforced on this node's accounts where a full node registers
        // the enforcer), its process is known, and the request for new keys was accepted from the node that drives it (it names that node).
        (await RemovalRowsAsync(person, process)).Should().Be((1L, 1L), "a node that was old at the removal's S0 applies the tombstone at its first start");

        (await new NodeStateStore(Factory).GetAsync(_peer))!.AppVersion.Should().Be("2.12.0");
        // In log order: the thief's rotation, then the genuine one (a conflict), then the confirm that settles it - the genuine key holds. Out of
        // order (the confirm first) there would be no conflict to settle, and the thief's key would stay. The key events go first (security review
        // N1), so the peer's state, signed by its old key, is judged by the Live rule against the genuine rotation, which lists it.
        (await WhitelistRepo.GetByNodeIdAsync(_peer))!.Ed25519PublicKey.Should().Equal(genuineEntry.NewPublicKey);
        (await new NodeKeyHistoryStore(Factory).GetConflictAsync(_peer))!.IsOpen.Should().BeFalse();
        (await RecordsAsync()).Should().Be(0);

        (await StartAsync()).Should().Be(0, "the replay runs once; a second start finds nothing to do");
        (await WhitelistRepo.GetByNodeIdAsync(_peer))!.Ed25519PublicKey.Should().Equal(genuineEntry.NewPublicKey);
        (await RemovalRowsAsync(person, process)).Should().Be((1L, 1L));
        (await EventApplier.ApplyAsync(NodeState(_peer, genuineSeed, genuineEntry.NewPublicKey, "2.12.1"))).Should().Be(EventApplyResult.Applied,
            "the peer's events under its new key apply as on a node that was up to date");
    }

    [Fact]
    public async Task A_crash_after_the_replay_applied_its_events_and_before_their_records_were_removed_replays_them_again_and_changes_nothing()
    {
        // The replay applies an event, then removes its record, one event at a time: a crash in between leaves the record of an event that is
        // already applied, and the next start replays it once more. Worst case: every record of the first start survives it.
        var person = Guid.NewGuid();
        var process = Guid.NewGuid();
        var removedAt = DateTime.UtcNow.AddMinutes(-5);
        var tombstone = Signed(_admin2, _admin2Seed, EventTypes.PersonRemoved, new PersonRemovedPayload(person.ToString("D"), "deleted", removedAt));
        var processState = Signed(_admin2, _admin2Seed, EventTypes.OffboardingState, new OffboardingStatePayload(process.ToString("D"),
            person.ToString("D"), _admin2.ToString("D"), _admin2.ToString("D"), "delete", 1, removedAt));
        var rekeyRequest = Signed(_admin2, _admin2Seed, EventTypes.NodeRekeyRequest,
            new NodeRekeyRequestPayload(process.ToString("D"), EventApplier.RemovalRekeyReason(process), [_peer.ToString("D")]));
        var state = NodeState(_peer, _peerSeed, _peerPub, "2.12.0");
        var (thief, _, _) = Rotate(_peerPub, _peerSeed);
        var (genuine, genuineEntry, _) = Rotate(_peerPub, _peerSeed, own: [state.EventId]);
        var confirm = NodeKeyRotationEvents.Confirm(_admin2, b => Ed25519Signer.Sign(_admin2Seed, b), _peer, genuine.EventId, genuineEntry.Counter,
            genuineEntry.NewPublicKey, ++_lamport, SyncProtocolVersion.Current, "Laptop");
        var stored = new[] { tombstone, processState, rekeyRequest, state, thief, genuine, confirm };
        foreach (var evt in stored)
            await StoredByAnOlderBuildAsync(evt);
        await StartAsync();
        (await RecordsAsync()).Should().Be(0, "precondition: the first start replayed everything");

        // The crash: the records are there again, as the one-time population wrote them.
        using (var c = Factory.CreateConnection())
            foreach (var evt in stored)
                await c.ExecuteAsync("INSERT INTO tbl_migration_marker (key, value, set_at) VALUES (@k, @v, @now)", new
                {
                    k = LateEventReplay.UnknownMarkerPrefix + evt.EventId.ToString("D"),
                    v = evt.EventType + LateEventReplay.LiveCheckSuffix,
                    now = DateTime.UtcNow.ToString("O"),
                });

        await FluentActions.Awaiting(StartAsync).Should().NotThrowAsync("the next start replays what the crash left");

        (await WhitelistRepo.GetByNodeIdAsync(_peer))!.Ed25519PublicKey.Should().Equal(genuineEntry.NewPublicKey,
            "a rotation and its confirm replayed again neither move the key nor open a conflict");
        (await new NodeKeyHistoryStore(Factory).GetConflictAsync(_peer))!.IsOpen.Should().BeFalse();
        (await RemovalRowsAsync(person, process)).Should().Be((1L, 1L), "one tombstone and one process, as after the first start");
        (await new NodeStateStore(Factory).GetAsync(_peer))!.AppVersion.Should().Be("2.12.0");
        using (var c = Factory.CreateConnection())
        {
            (await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_node_key_history WHERE node_id = @p COLLATE NOCASE", new { p = _peer.ToString("D") }))
                .Should().Be(1, "the genuine rotation's history row, once");
            (await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_event WHERE apply_state <> @a", new { a = EventApplyStates.Applied }))
                .Should().Be(0, "nothing replayed again was held or quarantined");
        }
        (await RecordsAsync()).Should().Be(0, "every record the crash left is settled at that start");
    }

    [Fact]
    public async Task A_type_this_build_does_not_know_is_recorded_kept_until_a_build_knows_it_and_then_replayed()
    {
        // This build meets a type from the future: stored, skipped, and recorded.
        var future = Signed(_peer, _peerSeed, "made_up_by_a_later_release", new { x = 1 });
        (await EventApplier.ApplyAsync(future)).Should().Be(EventApplyResult.Applied);
        (await RecordsAsync()).Should().Be(1, "the skip is written down");

        (await StartAsync()).Should().Be(0);
        (await RecordsAsync()).Should().Be(1, "a type still unknown here waits for a build that knows it");

        // A later build knows a recorded type (here: a 2.12 type recorded by the generic mechanism, not the one-time upgrade pass).
        var state = NodeState(_peer, _peerSeed, _peerPub, "2.13.0");
        await StoredByAnOlderBuildAsync(state);
        await LateEventReplay.RecordUnknownAsync(Factory, state.EventId, state.EventType);
        (await StartAsync()).Should().Be(1);
        (await new NodeStateStore(Factory).GetAsync(_peer))!.AppVersion.Should().Be("2.13.0");
        (await RecordsAsync()).Should().Be(1, "only the unknown type's record is left");
    }

    [Fact]
    public async Task A_held_or_quarantined_row_keeps_its_verdict_and_a_compacted_one_is_forgotten()
    {
        var held = NodeState(_peer, _peerSeed, _peerPub, "2.12.0");
        await StoredByAnOlderBuildAsync(held, EventApplyStates.QuarantinedAfterReceipt);
        await LateEventReplay.RecordUnknownAsync(Factory, Guid.NewGuid(), EventTypes.NodeState);   // its row was compacted away

        (await StartAsync()).Should().Be(0);

        (await new NodeStateStore(Factory).GetAsync(_peer)).Should().BeNull("a quarantined row is not applied by a replay");
        (await RecordsAsync()).Should().Be(0, "both records are settled");
    }

    [Fact]
    public async Task An_event_that_cannot_apply_yet_waits_for_the_next_start_and_applies_then()
    {
        // Stored by the older build: the peer's state. Since the update, two rotations of the peer competed (an open identity conflict), so every
        // other event of the peer waits for a superadmin's confirm - the replay included.
        var state = NodeState(_peer, _peerSeed, _peerPub, "2.12.0");
        await StoredByAnOlderBuildAsync(state);
        var (genuine, genuineEntry, _) = Rotate(_peerPub, _peerSeed, own: [state.EventId]);
        var (thief, _, _) = Rotate(_peerPub, _peerSeed);
        await EventApplier.ApplyAsync(genuine);
        await EventApplier.ApplyAsync(thief);

        await StartAsync();   // (the rotations this build applied are replayed too, as no-ops: the one-time pass takes every 2.12 row)
        (await new NodeStateStore(Factory).GetAsync(_peer)).Should().BeNull("the node is in conflict: deferred");
        (await RecordsAsync()).Should().Be(1, "kept for the next start");

        await EventApplier.ApplyAsync(NodeKeyRotationEvents.Confirm(_admin2, b => Ed25519Signer.Sign(_admin2Seed, b), _peer, genuine.EventId,
            genuineEntry.Counter, genuineEntry.NewPublicKey, ++_lamport, SyncProtocolVersion.Current, "Laptop"));
        (await StartAsync()).Should().Be(1, "settled: the stored state verifies by the retired key it was signed with (on the rotation's list) and applies");
        (await new NodeStateStore(Factory).GetAsync(_peer))!.AppVersion.Should().Be("2.12.0");
        (await RecordsAsync()).Should().Be(0);
    }

    private async Task<(int State, string? Reason)> RowAsync(Guid eventId)
    {
        using var c = Factory.CreateConnection();
        return await c.QueryFirstAsync<(int, string?)>("SELECT apply_state, hold_reason FROM tbl_event WHERE event_id = @id COLLATE NOCASE",
            new { id = eventId.ToString("D") });
    }

    /// <summary>
    /// somebody with a stolen seed of the accounts node signs, back-dated below the checkpoint of the node's later re-key, a
    /// tombstone of a staying superadmin and a designation of another node as the accounts node, and pushes both to a node that still runs 2.10;
    /// it stores them. Then the genuine re-key arrives (stored too). At the first start of this build those events are judged by the Live rule -
    /// the 2.10 build never ran one: signed by the retired key, not on the rotation's list, below its checkpoint. Neither applies; both rows are
    /// quarantined with a plain reason, and the retired-key attention flag is up (a removal's S5 does not close over it). The node's genuine event,
    /// on the list, applies. (Before: the replay used the package rule - no checkpoint, no list - and applied both.)
    /// </summary>
    [Fact]
    public async Task A_back_dated_tombstone_and_designation_signed_by_a_retired_accounts_key_are_not_applied_at_the_first_start()
    {
        var staying = Guid.NewGuid();
        var tombstone = Signed(_peer, _peerSeed, EventTypes.PersonRemoved, new PersonRemovedPayload(staying.ToString("D"), "deleted", DateTime.UtcNow), at: 5);
        var designation = Signed(_peer, _peerSeed, EventTypes.AccountsHomeSet, new AccountsHomeSetPayload(_admin2), at: 6);
        var genuineState = NodeState(_peer, _peerSeed, _peerPub, "2.12.0");
        var (rotation, entry, _) = Rotate(_peerPub, _peerSeed, checkpoint: 50, own: [genuineState.EventId]);
        foreach (var evt in new[] { tombstone, designation, genuineState, rotation })
            await StoredByAnOlderBuildAsync(evt);

        (await StartAsync()).Should().Be(2, "the re-key and the node's own state apply; the two forgeries do not");

        (await RemovalRowsAsync(staying, Guid.NewGuid())).Tombstones.Should().Be(0, "the forged tombstone is not applied");
        using (var c = Factory.CreateConnection())
            (await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_accounts_home WHERE node_id = @n COLLATE NOCASE", new { n = _admin2.ToString("D") }))
                .Should().Be(0, "the forged designation is not applied");
        (await RowAsync(tombstone.EventId)).Should().Be((EventApplyStates.QuarantinedAfterReceipt, HoldReasons.RetiredKey));
        (await RowAsync(designation.EventId)).Should().Be((EventApplyStates.QuarantinedAfterReceipt, HoldReasons.RetiredKey));
        (await RetiredKeyAttention.ListAsync(Factory)).Should().ContainSingle(i => i.NodeId == _peer, "the flag is up");
        (await WhitelistRepo.GetByNodeIdAsync(_peer))!.Ed25519PublicKey.Should().Equal(entry.NewPublicKey);
        (await new NodeStateStore(Factory).GetAsync(_peer))!.AppVersion.Should().Be("2.12.0");
        (await RecordsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_crash_after_a_retired_key_forgery_was_quarantined_and_before_its_flag_was_raised_raises_the_flag_at_the_next_start()
    {
        // The replay quarantines a back-dated forgery, raises the retired-key flag and removes the record - three writes. A crash after the
        // first left a quarantined row, its record and no flag; the next start forgot the record of a row that is no longer applied, and the
        // flag never came: the forgery stayed silent and a removal's S5 did not wait for anybody to look at it.
        var staying = Guid.NewGuid();
        var tombstone = Signed(_peer, _peerSeed, EventTypes.PersonRemoved, new PersonRemovedPayload(staying.ToString("D"), "deleted", DateTime.UtcNow), at: 5);
        var designation = Signed(_peer, _peerSeed, EventTypes.AccountsHomeSet, new AccountsHomeSetPayload(_admin2), at: 6);
        var genuineState = NodeState(_peer, _peerSeed, _peerPub, "2.12.0");
        var (rotation, _, _) = Rotate(_peerPub, _peerSeed, checkpoint: 50, own: [genuineState.EventId]);
        foreach (var evt in new[] { tombstone, designation, genuineState, rotation })
            await StoredByAnOlderBuildAsync(evt);
        await StartAsync();
        (await RowAsync(tombstone.EventId)).Should().Be((EventApplyStates.QuarantinedAfterReceipt, HoldReasons.RetiredKey), "precondition");

        // The crash: the rows are quarantined, the flag is not there, the records are.
        using (var c = Factory.CreateConnection())
        {
            await c.ExecuteAsync("DELETE FROM tbl_migration_marker WHERE key LIKE @p", new { p = EventApplier.RetiredKeyAttentionMarkerPrefix + "%" });
            foreach (var evt in new[] { tombstone, designation })
                await c.ExecuteAsync("INSERT INTO tbl_migration_marker (key, value, set_at) VALUES (@k, @v, @now)", new
                {
                    k = LateEventReplay.UnknownMarkerPrefix + evt.EventId.ToString("D"),
                    v = evt.EventType + LateEventReplay.LiveCheckSuffix,
                    now = DateTime.UtcNow.ToString("O"),
                });
        }
        (await RetiredKeyAttention.ListAsync(Factory)).Should().BeEmpty("precondition: the crash left no flag");

        await StartAsync();

        (await RetiredKeyAttention.ListAsync(Factory)).Should().ContainSingle(i => i.NodeId == _peer, "the next start raises what the crash lost");
        var draft = new NodeStateDraft();
        await new RetiredKeyAttentionContributor(Factory).ContributeAsync(draft);
        draft.AttentionFlags.Should().Contain(EventApplier.RetiredKeyAttentionFlag, "a removal's S5 waits while this flag is not acknowledged");
        (await RowAsync(tombstone.EventId)).Should().Be((EventApplyStates.QuarantinedAfterReceipt, HoldReasons.RetiredKey), "still not applied");
        (await RowAsync(designation.EventId)).Should().Be((EventApplyStates.QuarantinedAfterReceipt, HoldReasons.RetiredKey));
        (await RemovalRowsAsync(staying, Guid.NewGuid())).Tombstones.Should().Be(0);
        (await RecordsAsync()).Should().Be(0);
    }

    /// <summary>
    /// N1, the other way: an event this build (or a later one) recorded as unknown passed a Live check at receipt and keeps that rule - the package
    /// rule for a row the log holds - so an event of a node signed before it re-keyed, and not on the rotation's list, still applies.
    /// </summary>
    [Fact]
    public async Task An_event_recorded_as_unknown_after_its_live_check_keeps_the_receipt_rule()
    {
        var state = NodeState(_peer, _peerSeed, _peerPub, "2.13.0");
        await StoredByAnOlderBuildAsync(state);
        (await StartAsync()).Should().Be(1, "the one-time pass is done before this build receives anything");
        var (rotation, _, _) = Rotate(_peerPub, _peerSeed);   // lists nothing
        (await EventApplier.ApplyAsync(rotation)).Should().Be(EventApplyResult.Applied);

        var later = NodeState(_peer, _peerSeed, _peerPub, "2.14.0", at: rotation.LamportTs - 1);
        await StoredByAnOlderBuildAsync(later);
        await LateEventReplay.RecordUnknownAsync(Factory, later.EventId, later.EventType);

        (await StartAsync()).Should().Be(1, "recorded after its live check: the receipt rule");
        (await RowAsync(later.EventId)).State.Should().Be(EventApplyStates.Applied);
    }

    /// <summary>
    /// "Known" for the replay means "dispatched by the applier": every type of <see cref="EventTypes"/> but the sealed wrapper has a case in the
    /// dispatch, so an event recorded as unknown by an older build is never replayed into the skip branch of this one. A type added without a case
    /// (the merge brings three) fails here.
    /// </summary>
    [Fact]
    public void Every_type_the_replay_calls_known_is_a_case_of_the_applier_dispatch()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) dir = dir.Parent;
        var source = File.ReadAllText(Path.Combine(dir!.FullName, "libs", "BeeMemoryBank.Sync", "EventApplier.cs"));
        var cases = System.Text.RegularExpressions.Regex.Matches(source, @"case EventTypes\.(\w+)\s*:")
            .Select(m => (string)typeof(EventTypes).GetField(m.Groups[1].Value)!.GetRawConstantValue()!)
            .ToHashSet();
        LateEventReplay.KnownTypes.Should().BeSubsetOf(cases);
        LateEventReplay.IntroducedIn212.Should().BeSubsetOf(LateEventReplay.KnownTypes);
    }
}
