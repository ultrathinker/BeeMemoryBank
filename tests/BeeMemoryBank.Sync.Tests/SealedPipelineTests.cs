using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.TestSupport;
using SealedEnvelope = BeeMemoryBank.Sync.Metadata.SealedEnvelope;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Step A (docs/design/step-a-spec.md 6): the receipt and apply pipeline of a full node. A locked node holds sealed events,
/// serves them onward byte for byte and applies them in order, exactly once, after unlock; control events never wait; the
/// receipt verdict decides the superadmin gate; a key hold is released by a late key; a sealed event that does not open is
/// refused at receipt or quarantined in the drain, and never read as protocol 3. Drained here by
/// <see cref="EventApplier.ApplyHeldAsync"/> directly (the drain service is tested on its own).
/// </summary>
public class SealedPipelineTests : IAsyncLifetime
{
    private readonly ConcreteFixture _node = new();
    private readonly StepAAuthor _author = new();
    private readonly byte[] _dek = RandomNumberGenerator.GetBytes(32);
    private string _blob = "";

    public async Task InitializeAsync()
    {
        await _node.InitializeAsync();
        var (pub, seed) = Ed25519Signer.GenerateKeyPair();
        await _node.NodeRepo.CreateAsync(new NodeIdentity
        {
            NodeId = Guid.NewGuid(), DisplayName = "Receiver", Ed25519PublicKey = pub, Ed25519PrivateKey = seed,
            Ed25519PrivateKeyV = 0, CreatedAt = DateTime.UtcNow
        });
        await _node.WhitelistRepo.CreateAsync(_author.Entry("Author", superadmin: true));
        // The body ciphertext an article event names: the transport delivers it before the event (BlobTransport).
        _blob = await new BlobRepository(_node.Factory).StoreAsync(RandomNumberGenerator.GetBytes(80));
    }

    public Task DisposeAsync() => _node.DisposeAsync();

    [Fact]
    public async Task ALockedNode_HoldsSealedContent_ServesItByteIdentical_AndAppliesItInOrderOnceUnlocked()
    {
        (await _node.EventApplier.ApplyAsync(_author.NewKey(_dek))).Should().Be(EventApplyResult.Applied, "a control event applies locked");
        var folderId = Guid.NewGuid();
        var title = MetadataCanaries.Ascii("Title");
        var article = Guid.NewGuid();
        var sealedEvents = new[]
        {
            _author.Sealed(EventTypes.FolderCreate, null, StepAAuthor.FolderCreate(folderId, "/Alpha", "Alpha", null!)),
            _author.Sealed(EventTypes.ArticleCreate, article, StepAAuthor.ArticleCreate(title, "/Alpha", _blob)),
        };
        foreach (var e in sealedEvents)
            (await _node.EventApplier.ApplyAsync(e, new ApplyContext(ReceivedFrom: _author.NodeId, ReceivedSeq: 7)))
                .Should().Be(EventApplyResult.Held);
        // A control event does not wait behind them (rule 1).
        var promote = _author.Clear(EventTypes.WhitelistUpdate, null,
            JsonSerializer.Serialize(new WhitelistUpdatePayload(_author.NodeId, "https://a.example", null)));
        (await _node.EventApplier.ApplyAsync(promote)).Should().Be(EventApplyResult.Applied);

        (await _node.ArticleRepo.GetByIdAsync(article)).Should().BeNull("nothing sealed is applied while locked");
        var status = await new HeldEventStatus(_node.Factory).GetSummaryAsync();
        status.Held.Should().Be(2);
        status.HeldLocked.Should().Be(2);
        var held = await _node.EventLogRepo.GetHeldSequencesAsync(0, 10);
        status.AppliedThroughSequence.Should().Be(held[0] - 1, "the applied position stops before the first held row");
        status.HeadSequence.Should().Be(await _node.EventLogRepo.GetMaxSequenceAsync(), "the received position covers held rows");
        _node.Drain.Requests.Should().BeGreaterThan(0);

        // Served onward byte for byte: the same signed fields, no entity id, no actor fields, no local columns.
        var served = (await _node.EventLogRepo.GetAfterSequenceAsync(0)).Where(e => e.EventType == EventTypes.Sealed).ToList();
        served.Should().HaveCount(2);
        foreach (var (sent, original) in served.Zip(sealedEvents))
        {
            sent.EventId.Should().Be(original.EventId);
            sent.Payload.Should().Be(original.Payload);
            sent.Signature.Should().Equal(original.Signature);
            sent.ArticleId.Should().BeNull();
            sent.EntityId.Should().BeNull();
            sent.ActorName.Should().BeNull();
            sent.ActorType.Should().BeNull();
            Ed25519Signer.Verify(_author.PublicKey, EventSignature.BuildPayload(sent), sent.Signature).Should().BeTrue();
        }
        using (var conn = _node.Factory.CreateConnection())
        {
            var origin = await conn.QueryAsync<(string Peer, long Seq)>("SELECT origin_peer, origin_seq FROM tbl_event WHERE apply_state = 1");
            origin.Should().OnlyContain(o => Guid.Parse(o.Peer) == _author.NodeId && o.Seq == 7, "the deliverer is stored with the row");
        }

        // Unlock and drain: in order, exactly once.
        _node.Session.UnlockWithDek((byte[])_dek.Clone());
        foreach (var seq in held)
            (await _node.EventApplier.ApplyHeldAsync(seq)).Outcome.Should().Be(HeldApplyOutcome.Applied);
        (await _node.ArticleRepo.GetByIdAsync(article))!.Title.Should().Be(title);
        (await _node.FolderRepoFor().GetByIdAsync(folderId, includeDeleted: true)).Should().NotBeNull();
        foreach (var seq in held)
            (await _node.EventApplier.ApplyHeldAsync(seq)).Outcome.Should().Be(HeldApplyOutcome.NotHeld, "a drained row is not applied twice");
        foreach (var e in sealedEvents)
            (await _node.EventApplier.ApplyAsync(e)).Should().Be(EventApplyResult.Applied, "a redelivery is already here");

        status = await new HeldEventStatus(_node.Factory).GetSummaryAsync();
        status.Held.Should().Be(0);
        status.AppliedThroughSequence.Should().Be(status.HeadSequence);
        (await _node.EventLogRepo.GetTotalCountAsync()).Should().Be(4, "each event is logged once");

        // This node's own activity views show what the sealed rows are.
        var recent = await _node.EventLogRepo.GetRecentAsync(10);
        recent.Select(e => e.EventType).Should().Contain([EventTypes.FolderCreate, EventTypes.ArticleCreate]);
        (await _node.EventLogRepo.GetByArticleAsync(article)).Should().ContainSingle();
    }

    [Fact]
    public async Task AnUnlockedNode_OpensAndApplies_AndLogsTheOriginalWithLocalColumns()
    {
        _node.Session.UnlockWithDek((byte[])_dek.Clone());
        await _node.EventApplier.ApplyAsync(_author.NewKey(_dek));
        var folderId = Guid.NewGuid();
        var evt = _author.Sealed(EventTypes.FolderCreate, null, StepAAuthor.FolderCreate(folderId, "/Beta", "Beta", null!));

        (await _node.EventApplier.ApplyAsync(evt)).Should().Be(EventApplyResult.Applied);

        (await _node.FolderRepoFor().GetByIdAsync(folderId, includeDeleted: true))!.Path.Should().Be("/Beta");
        using var conn = _node.Factory.CreateConnection();
        var row = await conn.QuerySingleAsync<(string Type, string? LocalType, string? EntityId, long State, string Payload)>(
            "SELECT event_type, local_type, entity_id, apply_state, payload FROM tbl_event WHERE event_id = @Id COLLATE NOCASE",
            new { Id = evt.EventId.ToString() });
        row.Type.Should().Be("sealed");
        row.LocalType.Should().Be(EventTypes.FolderCreate);
        row.EntityId.Should().Be("/Beta", "the derived entity id is kept locally for the hard-delete gate");
        row.State.Should().Be(EventApplyStates.Applied);
        row.Payload.Should().Be(evt.Payload);
        (await _node.EventLogRepo.GetAfterSequenceAsync(0)).Single(e => e.EventId == evt.EventId).EntityId
            .Should().BeNull("the entity id of a protocol-4 row is never served");
    }

    [Fact]
    public async Task AKeyHold_IsReleasedByALateKeyRecord()
    {
        _node.Session.UnlockWithDek((byte[])_dek.Clone());
        var keySet = _author.NewKey(_dek);
        var folderId = Guid.NewGuid();
        var evt = _author.Sealed(EventTypes.FolderCreate, null, StepAAuthor.FolderCreate(folderId, "/Gamma", "Gamma", null!));

        (await _node.EventApplier.ApplyAsync(evt)).Should().Be(EventApplyResult.Held);
        var summary = await new HeldEventStatus(_node.Factory).GetSummaryAsync();
        summary.HeldKey.Should().Be(1);
        var drains = _node.Drain.Requests;

        (await _node.EventApplier.ApplyAsync(keySet)).Should().Be(EventApplyResult.Applied);
        _node.Drain.Requests.Should().BeGreaterThan(drains, "a new key asks the drain to run");
        var seq = (await _node.EventLogRepo.GetHeldSequencesAsync(0, 1)).Single();
        (await _node.EventApplier.ApplyHeldAsync(seq)).Outcome.Should().Be(HeldApplyOutcome.Applied);
        (await _node.FolderRepoFor().GetByIdAsync(folderId, includeDeleted: true)).Should().NotBeNull();
    }

    [Fact]
    public async Task ARevocationAppliedWhileLocked_DoesNotRejectContentReceivedBeforeIt()
    {
        // spec 6.5: an unlocked node applied X's content before it applied X's later revocation; a locked node applies the
        // revocation at once (a control event) and must still apply X's earlier content at unlock, or the mesh diverges.
        var admin = new StepAAuthor();
        await _node.WhitelistRepo.CreateAsync(admin.Entry("Admin", superadmin: true));
        await _node.EventApplier.ApplyAsync(_author.NewKey(_dek));
        var folderId = Guid.NewGuid();
        (await _node.EventApplier.ApplyAsync(_author.Sealed(EventTypes.FolderCreate, null,
            StepAAuthor.FolderCreate(folderId, "/Delta", "Delta", null!)))).Should().Be(EventApplyResult.Held);
        (await _node.EventApplier.ApplyAsync(admin.Clear(EventTypes.WhitelistRevoke, null,
            JsonSerializer.Serialize(new WhitelistRevokePayload(_author.NodeId))))).Should().Be(EventApplyResult.Applied);
        (await _node.WhitelistRepo.GetByNodeIdAsync(_author.NodeId)).Should().BeNull("the revocation applied while locked");

        _node.Session.UnlockWithDek((byte[])_dek.Clone());
        var seq = (await _node.EventLogRepo.GetHeldSequencesAsync(0, 1)).Single();
        (await _node.EventApplier.ApplyHeldAsync(seq)).Outcome.Should().Be(HeldApplyOutcome.Applied);
        (await _node.FolderRepoFor().GetByIdAsync(folderId, includeDeleted: true)).Should().NotBeNull();

        // And content the same node sends after the revocation is refused as today.
        await FluentActions.Awaiting(() => _node.EventApplier.ApplyAsync(_author.Sealed(EventTypes.FolderCreate, null,
            StepAAuthor.FolderCreate(Guid.NewGuid(), "/Late", "Late", null!)))).Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task TheStoredVerdict_GatesASealedHardDeleteFromAPlainPeer()
    {
        var plain = new StepAAuthor();
        await _node.WhitelistRepo.CreateAsync(plain.Entry("Plain", superadmin: false));
        var key = _author.NewKey(_dek);
        await _node.EventApplier.ApplyAsync(key);
        // The plain peer seals with the same key (it is a full node of the vault).
        var forged = SealWith(plain, EventTypes.HardDelete, null,
            JsonSerializer.Serialize(new HardDeleteEventPayload("folder", "/Anything", DateTime.UtcNow)));
        (await _node.EventApplier.ApplyAsync(forged)).Should().Be(EventApplyResult.Held);

        _node.Session.UnlockWithDek((byte[])_dek.Clone());
        var seq = (await _node.EventLogRepo.GetHeldSequencesAsync(0, 1)).Single();
        var result = await _node.EventApplier.ApplyHeldAsync(seq);
        result.Outcome.Should().Be(HeldApplyOutcome.Quarantined, "the superadmin gate runs on the opened view with the stored verdict");
        result.Failure.Should().BeOfType<OriginatorNotSuperadminException>();
        await AssertStateAsync(forged.EventId, EventApplyStates.QuarantinedAfterReceipt);
    }

    [Fact]
    public async Task ClearContent_WaitsBehindAHeldRow_ControlDoesNot()
    {
        await _node.EventApplier.ApplyAsync(_author.NewKey(_dek));
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        (await _node.EventApplier.ApplyAsync(_author.Sealed(EventTypes.FolderCreate, null,
            StepAAuthor.FolderCreate(first, "/One", "One", null!)))).Should().Be(EventApplyResult.Held);
        (await _node.EventApplier.ApplyAsync(_author.Clear(EventTypes.FolderCreate, null,
            StepAAuthor.FolderCreate(second, "/Two", "Two", null!)))).Should().Be(EventApplyResult.Held, "content applies in log order");
        var summary = await new HeldEventStatus(_node.Factory).GetSummaryAsync();
        (summary.HeldLocked, summary.HeldOrder).Should().Be((1, 1));

        _node.Session.UnlockWithDek((byte[])_dek.Clone());
        var held = await _node.EventLogRepo.GetHeldSequencesAsync(0, 10);
        (await _node.EventApplier.ApplyHeldAsync(held[1])).Outcome.Should().Be(HeldApplyOutcome.StillHeld, "the earlier row goes first");
        foreach (var seq in held)
            (await _node.EventApplier.ApplyHeldAsync(seq)).Outcome.Should().Be(HeldApplyOutcome.Applied);
        (await _node.FolderRepoFor().GetByIdAsync(second, includeDeleted: true)).Should().NotBeNull();
    }

    [Fact]
    public async Task ASealedEventThatDoesNotOpen_IsRefusedAtReceiptWhenUnlocked_AndQuarantinedInTheDrainWhenLocked()
    {
        await _node.EventApplier.ApplyAsync(_author.NewKey(_dek));
        var payload = StepAAuthor.FolderCreate(Guid.NewGuid(), "/Bad", "Bad", null!);
        SyncEvent Tampered() => _author.Sealed(EventTypes.FolderCreate, null, payload, outer =>
        {
            var e = SealedEnvelope.Parse(outer.Payload);
            var c = (byte[])e.Ciphertext.Clone();
            c[0] ^= 1;
            outer.Payload = new SealedEnvelope(e.KeyId, e.Nonce, e.BlobHashes, c).ToPayload();
        });

        var whileLocked = Tampered();
        (await _node.EventApplier.ApplyAsync(whileLocked)).Should().Be(EventApplyResult.Held, "a locked node cannot tell yet");

        _node.Session.UnlockWithDek((byte[])_dek.Clone());
        var atReceipt = Tampered();
        await FluentActions.Awaiting(() => _node.EventApplier.ApplyAsync(atReceipt)).Should().ThrowAsync<InvalidDataException>();
        (await _node.EventLogRepo.ExistsAsync(atReceipt.EventId)).Should().BeFalse("refused at receipt: not stored, not relayed");

        var seq = (await _node.EventLogRepo.GetHeldSequencesAsync(0, 1)).Single();
        (await _node.EventApplier.ApplyHeldAsync(seq)).Outcome.Should().Be(HeldApplyOutcome.Quarantined);
        await AssertStateAsync(whileLocked.EventId, EventApplyStates.QuarantinedAfterReceipt);
        (await _node.FolderRepoFor().GetAllActiveAsync()).Should().NotContain(f => f.Path == "/Bad");
        (await new HeldEventStatus(_node.Factory).GetSummaryAsync()).QuarantinedAfterReceipt.Should().Be(1);
    }

    [Theory]
    [InlineData("sealed at protocol 3")]
    [InlineData("sealed with an article id")]
    [InlineData("content in clear at protocol 4")]
    [InlineData("not an envelope")]
    public async Task AMalformedProtocol4Event_IsRefused_AndNeverStored(string what)
    {
        await _node.EventApplier.ApplyAsync(_author.NewKey(_dek));
        var folder = StepAAuthor.FolderCreate(Guid.NewGuid(), "/Mal", "Mal", null!);
        var evt = what switch
        {
            "sealed at protocol 3" => _author.Sealed(EventTypes.FolderCreate, null, folder, o => o.ProtocolVersion = 3),
            "sealed with an article id" => _author.Sealed(EventTypes.FolderCreate, null, folder, o => o.ArticleId = Guid.NewGuid()),
            "content in clear at protocol 4" => _author.Clear(EventTypes.FolderCreate, null, folder, protocol: 4),
            _ => _author.Sealed(EventTypes.FolderCreate, null, folder, o => o.Payload = folder),
        };
        await FluentActions.Awaiting(() => _node.EventApplier.ApplyAsync(evt)).Should().ThrowAsync<InvalidDataException>();
        (await _node.EventLogRepo.ExistsAsync(evt.EventId)).Should().BeFalse();
        (await _node.FolderRepoFor().GetAllActiveAsync()).Should().NotContain(f => f.Path == "/Mal", "never read as protocol 3");
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    /// <summary>Another author sealing with this test's MEK (it is a full node of the same vault).</summary>
    private SyncEvent SealWith(StepAAuthor other, string type, Guid? article, string payload)
    {
        var inner = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = other.NodeId, LamportTs = 5000, EventType = type, ArticleId = article,
            Payload = payload, ProtocolVersion = 3, CreatedAt = DateTime.UtcNow,
        };
        var outer = BeeMemoryBank.Vault.Metadata.MetadataEnvelope.Seal(inner, _author.KeyId,
            BeeMemoryBank.Vault.Metadata.MetadataKeyCrypto.DeriveEventKey(_author.Mek));
        return other.Resign(outer);
    }

    private async Task AssertStateAsync(Guid eventId, int state)
    {
        using var conn = _node.Factory.CreateConnection();
        (await conn.ExecuteScalarAsync<long>("SELECT apply_state FROM tbl_event WHERE event_id = @Id COLLATE NOCASE", new { Id = eventId.ToString() }))
            .Should().Be(state);
    }

    private sealed class ConcreteFixture : SyncTestFixture
    {
        public BeeMemoryBank.Core.Interfaces.IFolderRepository FolderRepoFor() =>
            new FolderRepository(Factory, new BeeMemoryBank.Core.Services.CallerScopeHolder());
    }
}
