using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Metadata;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The node identity re-key on a receiving node (release 2.12, plan WS-D D1-D3): the rotation event, the key history, the one verification rule
/// (current key, or a retired key for a listed straggler), conflicts and their confirm.
/// </summary>
public class NodeKeyRotationTests : SyncTestFixture
{
    private Guid _peer;
    private byte[] _peerPub = null!;
    private byte[] _peerSeed = null!;
    private Guid _admin2;
    private byte[] _admin2Pub = null!;
    private byte[] _admin2Seed = null!;
    private long _lamport = 100;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        (_peerPub, _peerSeed) = Ed25519Signer.GenerateKeyPair();
        _peer = Guid.NewGuid();
        await AddPeerAsync(_peer, _peerPub, superadmin: true, "Office");
        (_admin2Pub, _admin2Seed) = Ed25519Signer.GenerateKeyPair();
        _admin2 = Guid.NewGuid();
        await AddPeerAsync(_admin2, _admin2Pub, superadmin: true, "Laptop");
    }

    private Task AddPeerAsync(Guid id, byte[] pub, bool superadmin, string name) => WhitelistRepo.CreateAsync(new WhitelistEntry
    {
        NodeId = id, DisplayName = name, Ed25519PublicKey = pub, Status = "A", IsSuperadmin = superadmin, Ownership = OwnershipKinds.Shared,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    });

    private SyncEvent Signed(Guid author, byte[] seed, string type, object payload, long? lamport = null, Guid? id = null)
    {
        var evt = new SyncEvent
        {
            EventId = id ?? Guid.NewGuid(), NodeId = author, LamportTs = lamport ?? ++_lamport, EventType = type,
            Payload = JsonSerializer.Serialize(payload), ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow, Signature = [],
        };
        evt.EntityId = EventEntityId.Derive(evt);
        evt.Signature = Ed25519Signer.Sign(seed, EventSignature.BuildPayload(evt));
        return evt;
    }

    /// <summary>A content-free event any peer may author: a snapshot checkpoint marker.</summary>
    private SyncEvent Marker(Guid author, byte[] seed, long? lamport = null, Guid? id = null) =>
        Signed(author, seed, EventTypes.SnapshotCheckpoint, new { cp = 1 }, lamport, id);

    private (SyncEvent Event, NodeKeyHistoryEntry Entry, byte[] NewSeed) Rotate(byte[] oldPub, byte[] oldSeed, long counter = 1, long checkpoint = 0,
        IEnumerable<Guid>? listed = null, long? lamport = null)
    {
        var (_, newSeed) = Ed25519Signer.GenerateKeyPair();
        var (evt, entry) = NodeKeyRotationEvents.Rotation(_peer, oldPub, bytes => Ed25519Signer.Sign(oldSeed, bytes), newSeed, counter,
            lamport ?? ++_lamport, checkpoint, listed ?? [], "test", SyncProtocolVersion.Current, "Office");
        return (evt, entry, newSeed);
    }

    private async Task<byte[]> StoredKeyAsync(Guid id) => (await WhitelistRepo.GetByNodeIdAsync(id))!.Ed25519PublicKey;

    [Fact]
    public async Task A_rotation_stores_the_new_key_appends_the_history_and_makes_the_peer_authenticate_again()
    {
        var (rotation, entry, newSeed) = Rotate(_peerPub, _peerSeed);

        (await EventApplier.ApplyAsync(rotation)).Should().Be(EventApplyResult.Applied);

        (await StoredKeyAsync(_peer)).Should().Equal(entry.NewPublicKey);
        var history = await new NodeKeyHistoryStore(Factory).ListAsync(_peer);
        history.Should().ContainSingle().Which.Should().BeEquivalentTo(entry with { });
        history[0].ValidUntilLamport.Should().Be(rotation.LamportTs);
        PeerState.Changed.Should().Contain(_peer, "its cached contact state is forgotten");

        (await EventApplier.ApplyAsync(Marker(_peer, newSeed))).Should().Be(EventApplyResult.Applied, "events signed after the re-key verify");
        var forged = () => EventApplier.ApplyAsync(Marker(_peer, _peerSeed));
        await forged.Should().ThrowAsync<InvalidDataException>("a new event signed by the retired key is refused");
        (await EventApplier.ApplyAsync(rotation)).Should().Be(EventApplyResult.Applied, "a re-delivery is a no-op");
        (await new NodeKeyHistoryStore(Factory).CounterAsync(_peer)).Should().Be(1);
    }

    [Fact]
    public async Task The_stale_peer_attack_is_refused_and_a_listed_straggler_is_accepted()
    {
        // The node checkpointed at 50; its own events above that are 60 (listed). A thief with the old seed back-dates an event to 70: not listed.
        var straggler = Marker(_peer, _peerSeed, lamport: 60);
        var backDated = Marker(_peer, _peerSeed, lamport: 70);
        var (rotation, _, _) = Rotate(_peerPub, _peerSeed, checkpoint: 50, listed: [straggler.EventId]);
        await EventApplier.ApplyAsync(rotation);

        (await EventApplier.ApplyAsync(straggler)).Should().Be(EventApplyResult.Applied, "an own event in flight at the re-key is on the list");
        var attack = () => EventApplier.ApplyAsync(backDated);
        await attack.Should().ThrowAsync<InvalidDataException>("a back-dated event of the retired key is not on the list");
        var belowCheckpoint = () => EventApplier.ApplyAsync(Marker(_peer, _peerSeed, lamport: 40));
        await belowCheckpoint.Should().ThrowAsync<InvalidDataException>("below the checkpoint live sync takes only the current key");
    }

    [Fact]
    public async Task A_package_replay_takes_a_retired_key_below_the_rotation_but_not_after_it()
    {
        var old = Marker(_peer, _peerSeed, lamport: 40);
        var (rotation, entry, _) = Rotate(_peerPub, _peerSeed, checkpoint: 50);
        await EventApplier.ApplyAsync(rotation);
        var context = new ApplyContext(KeyMode: NodeKeyMode.Package);

        (await EventApplier.ApplyAsync(old, context)).Should().Be(EventApplyResult.Applied);
        var after = () => EventApplier.ApplyAsync(Marker(_peer, _peerSeed, lamport: rotation.LamportTs + 5), context);
        await after.Should().ThrowAsync<InvalidDataException>();
        NodeEventVerifier.Verify(Marker(_peer, _peerSeed, lamport: 10), entry.NewPublicKey, [entry], NodeKeyMode.Package).Should().BeTrue();
        NodeEventVerifier.Verify(Marker(_peer, _peerSeed, lamport: 10), entry.NewPublicKey, [entry], NodeKeyMode.Live).Should().BeFalse();
    }

    [Fact]
    public async Task A_rotation_is_refused_when_its_rules_do_not_hold()
    {
        // About another node.
        var (_, otherSeed) = Ed25519Signer.GenerateKeyPair();
        var (foreign, _) = NodeKeyRotationEvents.Rotation(_admin2, _admin2Pub, b => Ed25519Signer.Sign(_peerSeed, b), otherSeed, 1, ++_lamport, 0, [], null,
            SyncProtocolVersion.Current, "x");
        foreign.NodeId = _peer;
        foreign.Signature = Ed25519Signer.Sign(_peerSeed, EventSignature.BuildPayload(foreign));
        await ((Func<Task>)(() => EventApplier.ApplyAsync(foreign))).Should().ThrowAsync<InvalidDataException>();

        // A proof that does not verify.
        var (bad, _, _) = Rotate(_peerPub, _peerSeed);
        var p = JsonSerializer.Deserialize<NodeKeyRotatedPayload>(bad.Payload)!;
        bad.Payload = JsonSerializer.Serialize(p with { NewKeyProof = Convert.ToBase64String(new byte[64]) });
        bad.Signature = Ed25519Signer.Sign(_peerSeed, EventSignature.BuildPayload(bad));
        await ((Func<Task>)(() => EventApplier.ApplyAsync(bad))).Should().ThrowAsync<InvalidDataException>();

        // A gap: counter 2 while this node holds 0 waits for the rotation in between.
        var (gap, _, _) = Rotate(_peerPub, _peerSeed, counter: 2);
        var deferred = await ((Func<Task>)(() => EventApplier.ApplyAsync(gap))).Should().ThrowAsync<NodeKeyRotationGapException>();
        SyncFailureClassifier.Classify(deferred.Which).Should().Be(SyncFailureKind.Deferred);

        // From a peer that is not a superadmin.
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync("UPDATE tbl_whitelist SET is_superadmin = 0 WHERE node_id = @id COLLATE NOCASE", new { id = _peer.ToString("D") });
        var (plain, _, _) = Rotate(_peerPub, _peerSeed);
        await ((Func<Task>)(() => EventApplier.ApplyAsync(plain))).Should().ThrowAsync<OriginatorNotSuperadminException>();
        (await StoredKeyAsync(_peer)).Should().Equal(_peerPub, "nothing changed the key");
    }

    [Fact]
    public async Task A_revoked_node_cannot_rotate()
    {
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync("UPDATE tbl_whitelist SET status = 'R' WHERE node_id = @id COLLATE NOCASE", new { id = _peer.ToString("D") });
        var (rotation, _, _) = Rotate(_peerPub, _peerSeed);
        await ((Func<Task>)(() => EventApplier.ApplyAsync(rotation))).Should().ThrowAsync<UnauthorizedAccessException>();
        (await new NodeKeyHistoryStore(Factory).CounterAsync(_peer)).Should().Be(0);
    }

    [Fact]
    public async Task Events_of_the_retired_key_this_node_already_holds_and_the_rotation_does_not_list_raise_the_attention_flag()
    {
        // This node is the not-yet-updated peer B: it took a back-dated event of the old key before the rotation arrived.
        var stolen = Marker(_peer, _peerSeed, lamport: 70);
        (await EventApplier.ApplyAsync(stolen)).Should().Be(EventApplyResult.Applied);
        var listed = Marker(_peer, _peerSeed, lamport: 65);
        (await EventApplier.ApplyAsync(listed)).Should().Be(EventApplyResult.Applied);

        var (rotation, _, _) = Rotate(_peerPub, _peerSeed, checkpoint: 50, listed: [listed.EventId]);
        await EventApplier.ApplyAsync(rotation);

        var draft = new NodeStateDraft();
        await new RetiredKeyAttentionContributor(Factory).ContributeAsync(draft);
        draft.AttentionFlags.Should().Contain(EventApplier.RetiredKeyAttentionFlag);
        using var c = Factory.CreateConnection();
        (await c.ExecuteScalarAsync<string>("SELECT value FROM tbl_migration_marker WHERE key = @k",
            new { k = EventApplier.RetiredKeyAttentionMarkerPrefix + _peer.ToString("D") })).Should().Be("1");
    }

    [Fact]
    public async Task Competing_rotations_mark_a_conflict_refuse_the_node_and_a_confirm_from_another_superadmin_settles_it()
    {
        var (first, firstEntry, firstSeed) = Rotate(_peerPub, _peerSeed);
        var (second, secondEntry, secondSeed) = Rotate(_peerPub, _peerSeed);
        await EventApplier.ApplyAsync(first);
        (await EventApplier.ApplyAsync(second)).Should().Be(EventApplyResult.Applied, "the second rotation is logged so that a confirm can name it");

        var conflict = await new NodeKeyHistoryStore(Factory).GetConflictAsync(_peer);
        conflict.Should().NotBeNull();
        conflict!.IsOpen.Should().BeTrue();
        conflict.FirstNewKey.Should().Equal(firstEntry.NewPublicKey);
        conflict.SecondNewKey.Should().Equal(secondEntry.NewPublicKey);
        var refused = await ((Func<Task>)(() => EventApplier.ApplyAsync(Marker(_peer, firstSeed)))).Should().ThrowAsync<NodeIdentityConflictException>();
        SyncFailureClassifier.Classify(refused.Which).Should().Be(SyncFailureKind.Deferred, "its events apply once the conflict is settled");

        // Refused confirms: about itself, naming an unknown rotation, signed by a historical key.
        var aboutItself = NodeKeyRotationEvents.Confirm(_admin2, b => Ed25519Signer.Sign(_admin2Seed, b), _admin2, second.EventId, 1,
            secondEntry.NewPublicKey, ++_lamport, SyncProtocolVersion.Current, "Laptop");
        await ((Func<Task>)(() => EventApplier.ApplyAsync(aboutItself))).Should().ThrowAsync<UnauthorizedAccessException>();
        var unknown = NodeKeyRotationEvents.Confirm(_admin2, b => Ed25519Signer.Sign(_admin2Seed, b), _peer, Guid.NewGuid(), 1,
            secondEntry.NewPublicKey, ++_lamport, SyncProtocolVersion.Current, "Laptop");
        // Not refused for good: the rotation it names may still be on its way, so it is deferred.
        await ((Func<Task>)(() => EventApplier.ApplyAsync(unknown))).Should().ThrowAsync<NodeKeyConfirmEarlyException>();
        var wrongKey = NodeKeyRotationEvents.Confirm(_admin2, b => Ed25519Signer.Sign(_admin2Seed, b), _peer, second.EventId, 1,
            firstEntry.NewPublicKey, ++_lamport, SyncProtocolVersion.Current, "Laptop");
        await ((Func<Task>)(() => EventApplier.ApplyAsync(wrongKey))).Should().ThrowAsync<InvalidDataException>();

        // The confirming node re-keyed itself; a confirm signed by its retired key is refused, by its current key accepted.
        var (_, admin2NewSeed) = Ed25519Signer.GenerateKeyPair();
        var (admin2Rotation, admin2Entry) = NodeKeyRotationEvents.Rotation(_admin2, _admin2Pub, b => Ed25519Signer.Sign(_admin2Seed, b), admin2NewSeed,
            1, ++_lamport, 0, [], null, SyncProtocolVersion.Current, "Laptop");
        await EventApplier.ApplyAsync(admin2Rotation);
        var historical = NodeKeyRotationEvents.Confirm(_admin2, b => Ed25519Signer.Sign(_admin2Seed, b), _peer, second.EventId, 1,
            secondEntry.NewPublicKey, admin2Rotation.LamportTs - 1, SyncProtocolVersion.Current, "Laptop");
        await ((Func<Task>)(() => EventApplier.ApplyAsync(historical))).Should().ThrowAsync<InvalidDataException>();

        var confirm = NodeKeyRotationEvents.Confirm(_admin2, b => Ed25519Signer.Sign(admin2NewSeed, b), _peer, second.EventId, 1,
            secondEntry.NewPublicKey, ++_lamport, SyncProtocolVersion.Current, "Laptop");
        (await EventApplier.ApplyAsync(confirm)).Should().Be(EventApplyResult.Applied);

        (await StoredKeyAsync(_peer)).Should().Equal(secondEntry.NewPublicKey);
        (await new NodeKeyHistoryStore(Factory).GetConflictAsync(_peer))!.IsOpen.Should().BeFalse();
        (await new NodeKeyHistoryStore(Factory).ListAsync(_peer)).Should().ContainSingle().Which.RotationEventId.Should().Be(second.EventId);
        (await EventApplier.ApplyAsync(Marker(_peer, secondSeed))).Should().Be(EventApplyResult.Applied);
        await ((Func<Task>)(() => EventApplier.ApplyAsync(Marker(_peer, firstSeed)))).Should().ThrowAsync<InvalidDataException>();
        admin2Entry.NewPublicKey.Should().NotBeEmpty();
    }

    [Fact]
    public void The_transition_chain_walks_from_a_pinned_key_only_through_links_both_keys_signed()
    {
        var node = Guid.NewGuid();
        var (k0, s0) = Ed25519Signer.GenerateKeyPair();
        var (k1, s1) = Ed25519Signer.GenerateKeyPair();
        var (k2, s2) = Ed25519Signer.GenerateKeyPair();
        NodeKeyLink Link(byte[] o, byte[] os, byte[] n, byte[] ns, long c)
        {
            var st = NodeKeyTransition.Statement(node, o, n, c);
            return new NodeKeyLink(o, n, c, Ed25519Signer.Sign(os, st), Ed25519Signer.Sign(ns, st));
        }
        var chain = new[] { Link(k1, s1, k2, s2, 2), Link(k0, s0, k1, s1, 1) };

        NodeKeyTransition.Advance(node, k0, chain).Should().Equal(k2);
        NodeKeyTransition.Advance(node, k1, chain).Should().Equal(k2);
        NodeKeyTransition.Advance(node, k2, chain).Should().Equal(k2);
        NodeKeyTransition.Advance(Guid.NewGuid(), k0, chain).Should().Equal(k0, "the statement names the node");

        var (thief, thiefSeed) = Ed25519Signer.GenerateKeyPair();
        var forged = Link(k0, thiefSeed, thief, thiefSeed, 1);   // signed by the thief, not by k0
        NodeKeyTransition.Advance(node, k0, [forged]).Should().Equal(k0);

        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
        EventIdSet.Unpack(EventIdSet.Pack(ids.Concat(ids))).Should().BeEquivalentTo(ids);
        EventIdSet.Pack([]).Should().BeEmpty();
    }

    /// <summary>Compaction keeps the newest 16 history rows of each node (plan D1): the 16 links a returning device walks, and the newest row's counter.</summary>
    [Fact]
    public async Task Compaction_keeps_the_newest_sixteen_history_rows_of_each_node_and_nothing_else_is_dropped()
    {
        var busy = Guid.NewGuid();
        var quiet = Guid.NewGuid();
        using var conn = Factory.CreateConnection();
        async Task Row(Guid node, long counter) => (await NodeKeyHistoryStore.InsertIfAbsentAsync(conn, null, new NodeKeyHistoryEntry(node, counter,
            Ed25519Signer.GenerateKeyPair().publicKey, Ed25519Signer.GenerateKeyPair().publicKey, counter, Guid.NewGuid(), 0, "", new byte[64], new byte[64])))
            .Should().BeTrue();
        for (var c = 1; c <= 20; c++) await Row(busy, c);
        for (var c = 1; c <= 3; c++) await Row(quiet, c);

        var removed = await NodeKeyHistoryStore.PruneToCapAsync(conn, null);

        removed.Should().Be(4);
        (await NodeKeyHistoryStore.ListAsync(conn, null, busy)).Select(r => r.Counter).Should().Equal(Enumerable.Range(5, 16).Select(i => (long)i));
        (await NodeKeyHistoryStore.ListAsync(conn, null, quiet)).Should().HaveCount(3, "a node under the cap keeps every row");
        (await NodeKeyHistoryStore.CounterAsync(conn, null, busy)).Should().Be(20, "the newest row carries the counter");
    }

    /// <summary>
    /// A node recognises its own echoed events by a key it retired (plan D3), also below its checkpoint: the package rule, up to the rotation that
    /// retired the key. After the rotation the retired key is not this node's, and a stranger's key never is.
    /// </summary>
    [Fact]
    public async Task Own_echoed_events_below_the_checkpoint_are_recognised_by_the_retired_key_up_to_its_rotation_only()
    {
        var self = Guid.NewGuid();
        var (oldPub, oldSeed) = Ed25519Signer.GenerateKeyPair();
        var (newPub, newSeed) = Ed25519Signer.GenerateKeyPair();
        var (_, strangerSeed) = Ed25519Signer.GenerateKeyPair();
        var (rotation, entry) = NodeKeyRotationEvents.Rotation(self, oldPub, b => Ed25519Signer.Sign(oldSeed, b), newSeed, 1, 100, 50, [], null,
            SyncProtocolVersion.Current, "Self");
        using (var conn = Factory.CreateConnection())
            (await NodeKeyHistoryStore.InsertIfAbsentAsync(conn, null, entry with { ValidUntilLamport = rotation.LamportTs })).Should().BeTrue();

        (await EventApplier.IsOwnEchoAsync(Marker(self, oldSeed, lamport: 10), newPub)).Should().BeTrue("below the checkpoint, not on any list: still its own");
        (await EventApplier.IsOwnEchoAsync(Marker(self, oldSeed, lamport: 100), newPub)).Should().BeTrue("the rotation itself is the last one");
        (await EventApplier.IsOwnEchoAsync(Marker(self, oldSeed, lamport: 101), newPub)).Should().BeFalse("after the rotation the retired key signs nothing of this node");
        (await EventApplier.IsOwnEchoAsync(Marker(self, newSeed, lamport: 101), newPub)).Should().BeTrue();
        (await EventApplier.IsOwnEchoAsync(Marker(self, strangerSeed, lamport: 10), newPub)).Should().BeFalse();
    }

    // ---- Rotations while an identity conflict is open, and what the confirm keeps ----------------

    private Func<Task> Applying(SyncEvent evt) => () => EventApplier.ApplyAsync(evt);

    private SyncEvent ConfirmBy2(SyncEvent rotation, NodeKeyHistoryEntry entry) => NodeKeyRotationEvents.Confirm(_admin2,
        b => Ed25519Signer.Sign(_admin2Seed, b), _peer, rotation.EventId, entry.Counter, entry.NewPublicKey, ++_lamport, SyncProtocolVersion.Current, "Laptop");

    private async Task<List<long>> CountersAsync() => (await new NodeKeyHistoryStore(Factory).ListAsync(_peer)).Select(h => h.Counter).ToList();

    [Fact]
    public async Task While_a_conflict_is_open_the_thiefs_next_rotation_is_deferred_and_the_confirm_of_the_genuine_key_leaves_it_nothing()
    {
        var (thief1, thiefEntry, thiefSeed) = Rotate(_peerPub, _peerSeed);
        var (genuine1, genuineEntry, genuineSeed) = Rotate(_peerPub, _peerSeed);
        await EventApplier.ApplyAsync(thief1);       // the thief's rotation reached this node first
        await EventApplier.ApplyAsync(genuine1);     // the genuine one records the conflict
        var (thief2, _, _) = Rotate(thiefEntry.NewPublicKey, thiefSeed, counter: 2);

        var deferred = (await Applying(thief2).Should().ThrowAsync<NodeIdentityConflictException>()).Which;
        SyncFailureClassifier.Classify(deferred).Should().Be(SyncFailureKind.Deferred, "it is retried once the conflict is settled");
        (await CountersAsync()).Should().Equal(1L);

        (await EventApplier.ApplyAsync(ConfirmBy2(genuine1, genuineEntry))).Should().Be(EventApplyResult.Applied);
        (await StoredKeyAsync(_peer)).Should().Equal(genuineEntry.NewPublicKey);
        (await new NodeKeyHistoryStore(Factory).ListAsync(_peer)).Should().ContainSingle().Which.RotationEventId.Should().Be(genuine1.EventId);

        await Applying(thief2).Should().ThrowAsync<InvalidDataException>("the retried thief rotation retires a key this node no longer holds");
        var (genuine2, genuine2Entry, _) = Rotate(genuineEntry.NewPublicKey, genuineSeed, counter: 2);
        (await EventApplier.ApplyAsync(genuine2)).Should().Be(EventApplyResult.Applied);
        (await StoredKeyAsync(_peer)).Should().Equal(genuine2Entry.NewPublicKey);
    }

    /// <summary>a confirm that overtakes the rotation it names waits for it instead of being refused for good.</summary>
    [Fact]
    public async Task A_confirm_that_arrives_before_the_rotation_it_names_is_deferred_and_settles_the_conflict_once_it_is_there()
    {
        var (thief1, _, _) = Rotate(_peerPub, _peerSeed);
        var (genuine1, genuineEntry, _) = Rotate(_peerPub, _peerSeed);
        await EventApplier.ApplyAsync(thief1);
        var confirm = ConfirmBy2(genuine1, genuineEntry);

        var early = (await Applying(confirm).Should().ThrowAsync<Exception>()).Which;
        SyncFailureClassifier.Classify(early).Should().Be(SyncFailureKind.Deferred, "the rotation it names is still on its way");

        await EventApplier.ApplyAsync(genuine1);     // the conflict
        (await EventApplier.ApplyAsync(confirm)).Should().Be(EventApplyResult.Applied, "the retry");
        (await new NodeKeyHistoryStore(Factory).GetConflictAsync(_peer))!.IsOpen.Should().BeFalse();
        (await StoredKeyAsync(_peer)).Should().Equal(genuineEntry.NewPublicKey);
    }

    [Fact]
    public async Task A_confirm_of_the_genuine_key_drops_the_later_rows_this_node_took_from_the_wrong_side_before_the_conflict()
    {
        var (thief1, thiefEntry, thiefSeed) = Rotate(_peerPub, _peerSeed);
        var (genuine1, genuineEntry, _) = Rotate(_peerPub, _peerSeed);
        await EventApplier.ApplyAsync(thief1);
        var (thief2, _, thief2Seed) = Rotate(thiefEntry.NewPublicKey, thiefSeed, counter: 2);
        (await EventApplier.ApplyAsync(thief2)).Should().Be(EventApplyResult.Applied, "no conflict is known yet");
        await EventApplier.ApplyAsync(genuine1);     // now the conflict on rotation 1

        (await EventApplier.ApplyAsync(ConfirmBy2(genuine1, genuineEntry))).Should().Be(EventApplyResult.Applied);

        (await CountersAsync()).Should().Equal([1L], "the thief's rotation 2 chained from the wrong key of rotation 1");
        (await StoredKeyAsync(_peer)).Should().Equal(genuineEntry.NewPublicKey);
        await Applying(Marker(_peer, thief2Seed)).Should().ThrowAsync<InvalidDataException>("the thief's later key signs nothing here");
    }

    [Fact]
    public async Task While_a_conflict_is_open_the_genuine_nodes_next_rotation_waits_and_applies_after_the_confirm()
    {
        var (genuine1, genuineEntry, genuineSeed) = Rotate(_peerPub, _peerSeed);
        var (thief1, _, _) = Rotate(_peerPub, _peerSeed);
        await EventApplier.ApplyAsync(genuine1);
        await EventApplier.ApplyAsync(thief1);       // conflict
        var (genuine2, genuine2Entry, genuine2Seed) = Rotate(genuineEntry.NewPublicKey, genuineSeed, counter: 2);

        await Applying(genuine2).Should().ThrowAsync<NodeIdentityConflictException>();
        (await EventApplier.ApplyAsync(ConfirmBy2(genuine1, genuineEntry))).Should().Be(EventApplyResult.Applied);
        (await StoredKeyAsync(_peer)).Should().Equal(genuineEntry.NewPublicKey);

        (await EventApplier.ApplyAsync(genuine2)).Should().Be(EventApplyResult.Applied, "the retry after the confirm");
        (await StoredKeyAsync(_peer)).Should().Equal(genuine2Entry.NewPublicKey);
        (await EventApplier.ApplyAsync(Marker(_peer, genuine2Seed))).Should().Be(EventApplyResult.Applied);
    }

    [Fact]
    public async Task A_confirm_of_the_side_this_node_already_follows_never_rolls_its_key_back_below_a_later_rotation()
    {
        var (genuine1, genuineEntry, genuineSeed) = Rotate(_peerPub, _peerSeed);
        await EventApplier.ApplyAsync(genuine1);
        var (genuine2, genuine2Entry, genuine2Seed) = Rotate(genuineEntry.NewPublicKey, genuineSeed, counter: 2);
        await EventApplier.ApplyAsync(genuine2);
        var (thief1, _, _) = Rotate(_peerPub, _peerSeed);
        await EventApplier.ApplyAsync(thief1);       // a late thief rotation of counter 1: conflict

        (await EventApplier.ApplyAsync(ConfirmBy2(genuine1, genuineEntry))).Should().Be(EventApplyResult.Applied);

        (await StoredKeyAsync(_peer)).Should().Equal(genuine2Entry.NewPublicKey, "the node's newest key stays");
        (await CountersAsync()).Should().Equal(1L, 2L);
        (await EventApplier.ApplyAsync(Marker(_peer, genuine2Seed))).Should().Be(EventApplyResult.Applied);
    }

    // ---- This node's own confirm is logged and settles the conflict in one transaction -------------------------

    [Fact]
    public async Task An_own_confirm_is_logged_in_the_transaction_that_settles_the_conflict_and_a_failed_log_settles_nothing()
    {
        var (genuine1, genuineEntry, _) = Rotate(_peerPub, _peerSeed);
        var (thief1, thiefEntry, _) = Rotate(_peerPub, _peerSeed);
        await EventApplier.ApplyAsync(thief1);
        await EventApplier.ApplyAsync(genuine1);

        // The log refuses the confirm (here: a row with its id is there already): nothing is settled, nothing moves.
        var blocked = ConfirmBy2(genuine1, genuineEntry);
        await EventLogRepo.AppendAsync(new SyncEvent
        {
            EventId = blocked.EventId, NodeId = _admin2, LamportTs = 1, EventType = EventTypes.SnapshotCheckpoint, Payload = "{}",
            ProtocolVersion = 1, CreatedAt = DateTime.UtcNow, Signature = new byte[64],
        });
        await ((Func<Task>)(() => EventApplier.ApplyOwnNodeKeyConfirmAsync(blocked))).Should().ThrowAsync<Exception>();
        (await new NodeKeyHistoryStore(Factory).GetConflictAsync(_peer))!.IsOpen.Should().BeTrue("the confirm was not logged, so it settled nothing");
        (await StoredKeyAsync(_peer)).Should().Equal(thiefEntry.NewPublicKey);

        var confirm = ConfirmBy2(genuine1, genuineEntry);
        await EventApplier.ApplyOwnNodeKeyConfirmAsync(confirm);
        (await EventLogRepo.GetByIdAsync(confirm.EventId.ToString("D"))).Should().NotBeNull("the confirm is in the log, to be sent");
        (await new NodeKeyHistoryStore(Factory).GetConflictAsync(_peer))!.IsOpen.Should().BeFalse();
        (await StoredKeyAsync(_peer)).Should().Equal(genuineEntry.NewPublicKey);
    }

    // ---- The retired-key attention flag ends - revoke, re-join, or acknowledge ---------------------------------

    private async Task RaiseAttentionAsync()
    {
        (await EventApplier.ApplyAsync(Marker(_peer, _peerSeed, lamport: 70))).Should().Be(EventApplyResult.Applied);
        var (rotation, _, _) = Rotate(_peerPub, _peerSeed, checkpoint: 50, listed: []);
        await EventApplier.ApplyAsync(rotation);
        (await RetiredKeyAttention.ListAsync(Factory)).Should().ContainSingle().Which.NodeId.Should().Be(_peer);
    }

    private async Task<bool> FlagRaisedAsync()
    {
        var draft = new NodeStateDraft();
        await new RetiredKeyAttentionContributor(Factory).ContributeAsync(draft);
        return draft.AttentionFlags.Contains(EventApplier.RetiredKeyAttentionFlag);
    }

    [Fact]
    public async Task The_attention_flag_of_a_node_ends_when_that_node_is_revoked()
    {
        await RaiseAttentionAsync();

        (await EventApplier.ApplyAsync(Signed(_admin2, _admin2Seed, EventTypes.WhitelistRevoke, new WhitelistRevokePayload(_peer))))
            .Should().Be(EventApplyResult.Applied);

        (await RetiredKeyAttention.ListAsync(Factory)).Should().BeEmpty();
        (await FlagRaisedAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task The_attention_flag_of_a_node_is_acknowledged_by_an_administrator()
    {
        await RaiseAttentionAsync();

        (await RetiredKeyAttention.ClearAsync(Factory, _peer)).Should().BeTrue();
        (await RetiredKeyAttention.ClearAsync(Factory, _peer)).Should().BeFalse("nothing left to acknowledge");
        (await FlagRaisedAsync()).Should().BeFalse();
    }
}
