using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The 6th whitelist concern (<see cref="WhitelistConcerns.Ownership"/>): LWW per concern regardless of arrival order,
/// payloads without ownership, self-ownership refusal on author and receiver, and late-upgrade replay (<see cref="WhitelistOwnershipRepair"/>).
/// </summary>
public class WhitelistOwnershipConcernTests : IAsyncLifetime
{
    private ConcreteFixture _authorA = null!;
    private ConcreteFixture _authorB = null!;
    private ConcreteFixture _receiver = null!;
    private ConcreteFixture _receiverAlt = null!;

    private static readonly Guid Target = Guid.Parse("eeeeeeee-0000-4000-8000-00000000a001");
    private static readonly Guid AlicePersonId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000001111");
    private static readonly Guid BobPersonId = Guid.Parse("bbbbbbbb-0000-4000-8000-000000002222");
    private readonly byte[] _targetKey = Ed25519Signer.GenerateKeyPair().publicKey;

    public async Task InitializeAsync()
    {
        _authorA = await CreateNodeAsync("AuthorA");
        _authorB = await CreateNodeAsync("AuthorB");
        _receiver = await CreateNodeAsync("Receiver");
        _receiverAlt = await CreateNodeAsync("ReceiverAlt");

        await TrustSuperadminAsync(_receiver, _authorA);
        await TrustSuperadminAsync(_receiver, _authorB);
        await TrustSuperadminAsync(_receiverAlt, _authorA);
        await TrustSuperadminAsync(_receiverAlt, _authorB);
    }

    public async Task DisposeAsync()
    {
        await _authorA.DisposeAsync();
        await _authorB.DisposeAsync();
        await _receiver.DisposeAsync();
        await _receiverAlt.DisposeAsync();
    }

    [Fact]
    public async Task Ownership_and_rename_updates_converge_independently_regardless_of_arrival_order()
    {
        await _authorA.EventLogger.LogWhitelistAddAsync(new WhitelistEntry
        {
            NodeId = Target,
            DisplayName = "Original",
            Ed25519PublicKey = _targetKey,
            IsSuperadmin = false,
            Ownership = OwnershipKinds.Shared,
        });
        var addEvt = (await _authorA.EventLogRepo.GetAfterSequenceAsync(0, 10)).Single();

        // Advance AuthorB's clock past the add so its ownership update is newer than addEvt;
        // then advance AuthorA's clock higher so its rename wins Name while Ownership from B wins Ownership.
        _authorB.Clock.RaiseTo(addEvt.LamportTs);
        var verOwnB = await _authorB.EventLogger.LogWhitelistUpdateAsync(
            Target, apiAddress: null, displayName: null,
            ownership: OwnershipKinds.Personal, ownerPersonId: AlicePersonId, ownerLabel: "Alice");
        var ownEvt = (await _authorB.EventLogRepo.GetAfterSequenceAsync(0, 10)).Single();

        _authorA.Clock.RaiseTo(verOwnB.LamportTs + 5);
        await _authorA.EventLogger.LogWhitelistUpdateAsync(Target, apiAddress: null, displayName: "Renamed Laptop");
        var renameEvt = (await _authorA.EventLogRepo.GetAfterSequenceAsync(0, 10)).Last();

        // Order 1 on Receiver: add -> ownership -> rename (rename does not overwrite ownership).
        await ApplyAllAsync(_receiver, addEvt, ownEvt, renameEvt);
        // Order 2 on ReceiverAlt: add -> rename -> ownership (older ownership still applies because Name version is higher, Ownership is not).
        await ApplyAllAsync(_receiverAlt, addEvt, renameEvt, ownEvt);

        var row1 = (await _receiver.WhitelistRepo.GetByNodeIdAsync(Target))!;
        var row2 = (await _receiverAlt.WhitelistRepo.GetByNodeIdAsync(Target))!;

        row1.DisplayName.Should().Be("Renamed Laptop");
        row1.Ownership.Should().Be(OwnershipKinds.Personal);
        row1.OwnerPersonId.Should().Be(AlicePersonId);
        row1.OwnerLabel.Should().Be("Alice");
        row1.NeedsReview.Should().BeFalse();
        row1.VersionOf(WhitelistConcerns.Ownership).Should().Be(verOwnB);

        (row2.DisplayName, row2.Ownership, row2.OwnerPersonId, row2.OwnerLabel, row2.NeedsReview, row2.VersionOf(WhitelistConcerns.Ownership))
            .Should().Be((row1.DisplayName, row1.Ownership, row1.OwnerPersonId, row1.OwnerLabel, row1.NeedsReview, row1.VersionOf(WhitelistConcerns.Ownership)));
    }

    [Fact]
    public async Task Ownership_lww_beats_older_ownership_update_even_when_delivered_out_of_order()
    {
        var aliceVer = await _authorA.EventLogger.LogWhitelistUpdateAsync(
            Target, null, null, ownership: OwnershipKinds.Personal, ownerPersonId: AlicePersonId, ownerLabel: "Alice");
        var aliceEvt = (await _authorA.EventLogRepo.GetAfterSequenceAsync(0, 10)).Single();

        _authorB.Clock.RaiseTo(aliceVer.LamportTs + 10);
        var bobVer = await _authorB.EventLogger.LogWhitelistUpdateAsync(
            Target, null, null, ownership: OwnershipKinds.Personal, ownerPersonId: BobPersonId, ownerLabel: "Bob");
        var bobEvt = (await _authorB.EventLogRepo.GetAfterSequenceAsync(0, 10)).Single();

        // Add first, then deliver Bob (newer), then Alice (older): Bob must keep ownership.
        await _authorA.EventLogger.LogWhitelistAddAsync(new WhitelistEntry
        {
            NodeId = Target, DisplayName = "Device", Ed25519PublicKey = _targetKey, Ownership = OwnershipKinds.Shared
        });
        var addEvt = (await _authorA.EventLogRepo.GetAfterSequenceAsync(0, 10)).Last();

        await ApplyAllAsync(_receiver, addEvt, bobEvt, aliceEvt);
        var row = (await _receiver.WhitelistRepo.GetByNodeIdAsync(Target))!;
        row.Ownership.Should().Be(OwnershipKinds.Personal);
        row.OwnerPersonId.Should().Be(BobPersonId);
        row.OwnerLabel.Should().Be("Bob");
        row.VersionOf(WhitelistConcerns.Ownership).Should().Be(bobVer);
    }

    [Fact]
    public async Task Older_whitelist_add_without_ownership_leaves_unknown_and_flags_needs_review_and_update_with_ownership_clears_it()
    {
        var legacyAdd = await SignEventAsync(_authorA, ts: 5, EventTypes.WhitelistAdd, new
        {
            node_id = Target,
            display_name = "Old 2.10 Peer",
            public_key = Convert.ToBase64String(_targetKey),
            api_address = "https://old.example.org",
            can_generate_embeddings = false,
            is_superadmin = false
        });

        await ApplyAllAsync(_receiver, legacyAdd);

        var row = (await _receiver.WhitelistRepo.GetByNodeIdAsync(Target))!;
        row.Ownership.Should().Be(OwnershipKinds.Unknown);
        row.OwnerPersonId.Should().BeNull();
        row.OwnerLabel.Should().BeNull();
        row.NeedsReview.Should().BeTrue();
        row.OwnershipLamportTs.Should().Be(0);

        // Ownership update clears needs_review cleanly.
        _authorB.Clock.RaiseTo(10);
        await _authorB.EventLogger.LogWhitelistUpdateAsync(
            Target, null, null, ownership: OwnershipKinds.Shared);
        var sharedEvt = (await _authorB.EventLogRepo.GetAfterSequenceAsync(0, 10)).Single();
        await ApplyAllAsync(_receiver, sharedEvt);

        row = (await _receiver.WhitelistRepo.GetByNodeIdAsync(Target))!;
        row.Ownership.Should().Be(OwnershipKinds.Shared);
        row.NeedsReview.Should().BeFalse();
        row.OwnershipLamportTs.Should().BeGreaterThan(0);

        // Newer pre-2.12 whitelist_add without ownership leaves stored ownership as is and sets needs_review again.
        var newerLegacyAdd = await SignEventAsync(_authorA, ts: 50, EventTypes.WhitelistAdd, new
        {
            node_id = Target,
            display_name = "Re-added by 2.10",
            public_key = Convert.ToBase64String(_targetKey),
            api_address = "https://old.example.org",
            can_generate_embeddings = false,
            is_superadmin = false
        });
        await ApplyAllAsync(_receiver, newerLegacyAdd);

        row = (await _receiver.WhitelistRepo.GetByNodeIdAsync(Target))!;
        row.Ownership.Should().Be(OwnershipKinds.Shared, "older add without ownership leaves stored ownership intact");
        row.NeedsReview.Should().BeTrue();
    }

    [Fact]
    public async Task Self_authored_ownership_change_is_refused_on_author_and_on_receiver()
    {
        var receiverIdentity = (await _receiver.NodeRepo.GetAsync())!;

        // Author refusal: node cannot log an ownership change for itself.
        await FluentActions.Invoking(() => _receiver.EventLogger.LogWhitelistUpdateAsync(
                receiverIdentity.NodeId, null, null, ownership: OwnershipKinds.Shared))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Invoking(() => _receiver.EventLogger.LogWhitelistAddAsync(new WhitelistEntry
            {
                NodeId = receiverIdentity.NodeId,
                DisplayName = "Self",
                Ed25519PublicKey = receiverIdentity.Ed25519PublicKey,
                Ownership = OwnershipKinds.Shared,
            }))
            .Should().ThrowAsync<InvalidOperationException>();

        // Receiver refusal: AuthorA tries to assign its own ownership via whitelist_add and whitelist_update.
        var authorAIdentity = (await _authorA.NodeRepo.GetAsync())!;
        var authorAId = authorAIdentity.NodeId;
        var selfAdd = await SignEventAsync(_authorA, ts: 20, EventTypes.WhitelistAdd, new WhitelistAddPayload(
            authorAId, "AuthorA", Convert.ToBase64String(authorAIdentity.Ed25519PublicKey),
            null, false, true, null, null, OwnershipKinds.Shared, null, null));
        var selfUpdate = await SignEventAsync(_authorA, ts: 21, EventTypes.WhitelistUpdate, new WhitelistUpdatePayload(
            authorAId, null, "AuthorA Renamed", null, null, null, OwnershipKinds.Shared, null, null));

        // Ensure receiver starts with AuthorA as Personal (set by AuthorB at ts 5).
        _authorB.Clock.RaiseTo(5);
        await _authorB.EventLogger.LogWhitelistUpdateAsync(
            authorAId, null, null, ownership: OwnershipKinds.Personal, ownerPersonId: BobPersonId, ownerLabel: "Bob");
        var validEvt = (await _authorB.EventLogRepo.GetAfterSequenceAsync(0, 10)).Single();

        await ApplyAllAsync(_receiver, validEvt, selfAdd, selfUpdate);

        var row = (await _receiver.WhitelistRepo.GetByNodeIdAsync(authorAId))!;
        row.DisplayName.Should().Be("AuthorA Renamed", "name change from self update still applies");
        row.Ownership.Should().Be(OwnershipKinds.Personal, "self-authored ownership changes were refused on receiver");
        row.OwnerPersonId.Should().Be(BobPersonId);
        row.OwnerLabel.Should().Be("Bob");
    }

    [Fact]
    public async Task Late_upgrade_repair_replays_ownership_from_stored_events_and_runs_once()
    {
        await _authorA.EventLogger.LogWhitelistAddAsync(new WhitelistEntry
        {
            NodeId = Target,
            DisplayName = "Late Upgraded Target",
            Ed25519PublicKey = _targetKey,
            Ownership = OwnershipKinds.Personal,
            OwnerPersonId = AlicePersonId,
            OwnerLabel = "Alice",
        });
        var addEvt = (await _authorA.EventLogRepo.GetAfterSequenceAsync(0, 10)).Single();

        // Simulate a 2.10 node before upgrade: add event stored as applied, whitelist row left in pre-047 shape -> 047 backfill state.
        await _receiver.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = Target,
            DisplayName = "Late Upgraded Target",
            Ed25519PublicKey = _targetKey,
            Status = WhitelistStatuses.Active,
            Ownership = OwnershipKinds.Unknown,
            NeedsReview = true,
            OwnershipLamportTs = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        using (var conn = _receiver.Factory.CreateConnection())
        {
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_event (event_id, node_id, event_type, lamport_ts, payload, signature, apply_state, created_at)
                  VALUES (@EventId, @NodeId, @EventType, @LamportTs, @Payload, @Signature, @ApplyState, @CreatedAt)",
                new
                {
                    EventId = addEvt.EventId.ToString("D"),
                    NodeId = addEvt.NodeId.ToString("D"),
                    addEvt.EventType,
                    addEvt.LamportTs,
                    addEvt.Payload,
                    addEvt.Signature,
                    ApplyState = EventApplyStates.Applied,
                    CreatedAt = addEvt.CreatedAt.ToString("O"),
                });
        }

        var scopes = new ServiceCollection()
            .AddSingleton<IDbConnectionFactory>(_receiver.Factory)
            .AddSingleton(_receiver.NodeRepo)
            .AddSingleton(_receiver.EventApplier)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        var changed = await WhitelistOwnershipRepair.RunAsync(scopes, NullLogger.Instance);
        changed.Should().Be(1);

        var row = (await _receiver.WhitelistRepo.GetByNodeIdAsync(Target))!;
        row.Ownership.Should().Be(OwnershipKinds.Personal);
        row.OwnerPersonId.Should().Be(AlicePersonId);
        row.OwnerLabel.Should().Be("Alice");
        row.NeedsReview.Should().BeFalse();
        row.OwnershipLamportTs.Should().Be(addEvt.LamportTs);

        // Second run is a no-op because of the marker.
        (await WhitelistOwnershipRepair.RunAsync(scopes, NullLogger.Instance)).Should().Be(0);
    }

    [Fact]
    public async Task Personal_ownership_without_owner_person_id_becomes_unknown_and_needs_review_in_applier_and_logger()
    {
        // Replicated whitelist_add with personal + empty owner_person_id -> unknown + needs_review
        var addNode = Guid.NewGuid();
        var addKey = Ed25519Signer.GenerateKeyPair().publicKey;
        var badAdd = await SignEventAsync(_authorA, ts: 5, EventTypes.WhitelistAdd, new WhitelistAddPayload(
            addNode, "NoOwnerAdd", Convert.ToBase64String(addKey), null, false, false, null, null,
            OwnershipKinds.Personal, Guid.Empty, "Ghost"));
        await ApplyAllAsync(_receiver, badAdd);

        var addedRow = (await _receiver.WhitelistRepo.GetByNodeIdAsync(addNode))!;
        addedRow.Ownership.Should().Be(OwnershipKinds.Unknown);
        addedRow.OwnerPersonId.Should().BeNull();
        addedRow.OwnerLabel.Should().BeNull();
        addedRow.NeedsReview.Should().BeTrue();

        // Replicated whitelist_update overriding existing Shared row with personal + null owner_person_id -> unknown + needs_review
        await _authorA.EventLogger.LogWhitelistAddAsync(new WhitelistEntry
        {
            NodeId = Target,
            DisplayName = "SharedFirst",
            Ed25519PublicKey = _targetKey,
            Ownership = OwnershipKinds.Shared,
        });
        var sharedAddEvt = (await _authorA.EventLogRepo.GetAfterSequenceAsync(0, 10)).Single();
        await ApplyAllAsync(_receiver, sharedAddEvt);

        var badUpdate = await SignEventAsync(_authorB, ts: 20, EventTypes.WhitelistUpdate, new WhitelistUpdatePayload(
            Target, null, null, null, null, null, OwnershipKinds.Personal, null, "Nobody"));
        await ApplyAllAsync(_receiver, badUpdate);

        var updatedRow = (await _receiver.WhitelistRepo.GetByNodeIdAsync(Target))!;
        updatedRow.Ownership.Should().Be(OwnershipKinds.Unknown);
        updatedRow.OwnerPersonId.Should().BeNull();
        updatedRow.OwnerLabel.Should().BeNull();
        updatedRow.NeedsReview.Should().BeTrue();

        // EventLogger must never emit personal without a non-empty owner_person_id
        _authorB.Clock.RaiseTo(25);
        await _authorB.EventLogger.LogWhitelistUpdateAsync(
            Target, null, null, ownership: OwnershipKinds.Personal, ownerPersonId: null, ownerLabel: "Nobody");
        var loggedUpdEvt = (await _authorB.EventLogRepo.GetAfterSequenceAsync(0, 10)).Last();
        var loggedUpdPayload = JsonSerializer.Deserialize<WhitelistUpdatePayload>(loggedUpdEvt.Payload)!;
        loggedUpdPayload.Ownership.Should().Be(OwnershipKinds.Unknown);
        loggedUpdPayload.OwnerPersonId.Should().BeNull();
        loggedUpdPayload.OwnerLabel.Should().BeNull();

        var thirdId = Guid.NewGuid();
        var thirdKey = Ed25519Signer.GenerateKeyPair().publicKey;
        await _authorB.EventLogger.LogWhitelistAddAsync(new WhitelistEntry
        {
            NodeId = thirdId,
            DisplayName = "NoOwnerLoggerAdd",
            Ed25519PublicKey = thirdKey,
            Ownership = OwnershipKinds.Personal,
            OwnerPersonId = Guid.Empty,
            OwnerLabel = "EmptyOwner",
        });
        var loggedAddEvt = (await _authorB.EventLogRepo.GetAfterSequenceAsync(0, 10)).Last();
        var loggedAddPayload = JsonSerializer.Deserialize<WhitelistAddPayload>(loggedAddEvt.Payload)!;
        loggedAddPayload.Ownership.Should().Be(OwnershipKinds.Unknown);
        loggedAddPayload.OwnerPersonId.Should().BeNull();
        loggedAddPayload.OwnerLabel.Should().BeNull();
    }

    [Fact]
    public async Task BlindRowAlreadyShared_LegacyAddWithoutOwnership_DoesNotSetNeedsReview_InApplierOrRepair()
    {
        // 1. Applier path: blind row settled as shared (needs_review = false, ts = 0) receives a 2.10-style whitelist_add without ownership.
        var blindApplierId = BlindNodeId.NewId();
        var blindApplierKey = Ed25519Signer.GenerateKeyPair().publicKey;
        await _receiver.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = blindApplierId,
            DisplayName = "BlindApplier",
            Ed25519PublicKey = blindApplierKey,
            Status = WhitelistStatuses.Active,
            Ownership = OwnershipKinds.Shared,
            NeedsReview = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var legacyAdd = await SignEventAsync(_authorA, ts: 10, EventTypes.WhitelistAdd, new WhitelistAddPayload(
            blindApplierId, "BlindApplier", Convert.ToBase64String(blindApplierKey), null, false));
        await ApplyAllAsync(_receiver, legacyAdd);

        var afterApply = (await _receiver.WhitelistRepo.GetByNodeIdAsync(blindApplierId))!;
        afterApply.Ownership.Should().Be(OwnershipKinds.Shared);
        afterApply.NeedsReview.Should().BeFalse("an already-shared blind row must not be flagged for review by a legacy add");

        // 2. Late-upgrade repair path: blind row with dated shared ownership encounters a higher-ts legacy add without ownership in tbl_event.
        var authorAId = (await _authorA.NodeRepo.GetAsync())!.NodeId;
        var blindRepairId = BlindNodeId.NewId();
        var blindRepairKey = Ed25519Signer.GenerateKeyPair().publicKey;
        await _receiver.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = blindRepairId,
            DisplayName = "BlindRepair",
            Ed25519PublicKey = blindRepairKey,
            Status = WhitelistStatuses.Active,
            Ownership = OwnershipKinds.Shared,
            OwnershipLamportTs = 5,
            OwnershipSourceNodeId = authorAId,
            NeedsReview = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        var legacyRepairAdd = await SignEventAsync(_authorA, ts: 20, EventTypes.WhitelistAdd, new WhitelistAddPayload(
            blindRepairId, "BlindRepair", Convert.ToBase64String(blindRepairKey), null, false));
        using (var conn = _receiver.Factory.CreateConnection())
        {
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_event (event_id, node_id, event_type, lamport_ts, payload, signature, apply_state, created_at)
                  VALUES (@EventId, @NodeId, @EventType, @LamportTs, @Payload, @Signature, @ApplyState, @CreatedAt)",
                new
                {
                    EventId = legacyRepairAdd.EventId.ToString("D"),
                    NodeId = legacyRepairAdd.NodeId.ToString("D"),
                    legacyRepairAdd.EventType,
                    legacyRepairAdd.LamportTs,
                    legacyRepairAdd.Payload,
                    legacyRepairAdd.Signature,
                    ApplyState = EventApplyStates.Applied,
                    CreatedAt = legacyRepairAdd.CreatedAt.ToString("O"),
                });
        }

        await _receiver.EventApplier.RepairWhitelistOwnershipAsync();
        var afterRepair = (await _receiver.WhitelistRepo.GetByNodeIdAsync(blindRepairId))!;
        afterRepair.Ownership.Should().Be(OwnershipKinds.Shared);
        afterRepair.NeedsReview.Should().BeFalse("repair must not set needs_review on an already-shared blind row");
    }

    private static async Task ApplyAllAsync(ConcreteFixture receiver, params SyncEvent[] events)
    {
        foreach (var e in events)
        {
            var result = await receiver.EventApplier.ApplyAsync(e);
            result.Should().Be(EventApplyResult.Applied);
        }
    }

    private static async Task<SyncEvent> SignEventAsync(ConcreteFixture author, long ts, string eventType, object payloadObj)
    {
        var id = (await author.NodeRepo.GetAsync())!;
        var eventId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(payloadObj);
        var createdAt = DateTime.UtcNow;
        var evt = new SyncEvent
        {
            EventId = eventId,
            NodeId = id.NodeId,
            EventType = eventType,
            LamportTs = ts,
            Payload = payload,
            CreatedAt = createdAt,
            ProtocolVersion = SyncProtocolVersion.Current,
        };
        var sigPayload = EventSignature.BuildPayload(evt);
        var dek = author.Session.GetMasterDek();
        try
        {
            evt.Signature = NodeIdentityVault.SignWithIdentity(
                id.Ed25519PrivateKey, id.Ed25519PrivateKeyIV, id.Ed25519PrivateKeyV, id.NodeId, dek, sigPayload);
        }
        finally
        {
            Array.Clear(dek);
        }
        return evt;
    }

    private static async Task<ConcreteFixture> CreateNodeAsync(string name)
    {
        var f = new ConcreteFixture();
        await f.InitializeAsync();
        await f.InitService.InitializeAsync("admin", name, "pw-" + name);
        await f.Session.UnlockAsync("pw-" + name);
        return f;
    }

    private static async Task TrustSuperadminAsync(ConcreteFixture target, ConcreteFixture peer)
    {
        var identity = (await peer.NodeRepo.GetAsync())!;
        await target.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identity.NodeId,
            DisplayName = identity.DisplayName,
            Ed25519PublicKey = identity.Ed25519PublicKey,
            Status = WhitelistStatuses.Active,
            IsSuperadmin = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private sealed class ConcreteFixture : SyncTestFixture { }
}
