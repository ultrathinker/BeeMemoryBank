using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.DekRotation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// a DEK-rotation commit this node received and holds (it passed the receipt check) is checked again on every
/// retry. When its initiator re-keys meanwhile, the commit is below the rotation's checkpoint and not on its list: the Live rule would refuse it
/// for ever. A held commit is re-checked by the package rule (its author's key, or a key it retired after signing); Live only at first delivery.
/// </summary>
public class CommitAcrossInitiatorRekeyTests : SyncTestFixture
{
    [Fact]
    public async Task A_commit_held_by_a_locked_node_applies_at_its_next_unlock_after_the_initiator_rekeyed_past_it()
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);
        var (oldPub, oldSeed) = Ed25519Signer.GenerateKeyPair();
        var initiator = Guid.NewGuid();
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = initiator, DisplayName = "Initiator", Ed25519PublicKey = oldPub, Status = "A", IsSuperadmin = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await WhitelistRepo.SetAutoAcceptDekRotationAsync(initiator.ToString(), true);

        var newDek = RandomNumberGenerator.GetBytes(32);
        var oldDek = Session.GetMasterDek();
        var (enc, iv) = MasterKeyManager.WrapMasterDek(newDek, oldDek);
        Array.Clear(oldDek);
        var commit = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = initiator, LamportTs = 1_000_000, EventType = EventTypes.DekRotationCommit,
            Payload = JsonSerializer.Serialize(new DekRotationCommitPayload(Guid.NewGuid().ToString(), 2, DateTime.UtcNow.ToString("O"),
                initiator.ToString(), Convert.ToBase64String(enc), Convert.ToBase64String(iv))),
            ProtocolVersion = 1, CreatedAt = DateTime.UtcNow,
        };
        commit.Signature = Ed25519Signer.Sign(oldSeed, EventSignature.BuildPayload(commit));
        await EventLogRepo.AppendAsync(commit);   // received and held: it passed the receipt check
        var stateRepo = new DekRotationStateRepository(Factory);
        await stateRepo.UpsertAsync(new DekRotationStateRow(commit.EventId.ToString(), DekRotationState.Committing, Guid.NewGuid().ToString(),
            DateTime.UtcNow.ToString("O"), null, null, null, null, null, null, null, DateTime.UtcNow.ToString("O"), DateTime.UtcNow.ToString("O")));
        Session.Lock();

        // The initiator compacts past the commit and re-keys: the commit is at its checkpoint and not on the rotation's list.
        var (_, newSeed) = Ed25519Signer.GenerateKeyPair();
        var (rotation, _) = NodeKeyRotationEvents.Rotation(initiator, oldPub, b => Ed25519Signer.Sign(oldSeed, b), newSeed, 1, 1_000_005,
            checkpointLamportTs: 1_000_000, [], "test", SyncProtocolVersion.Current, "Initiator");
        (await EventApplier.ApplyAsync(rotation)).Should().Be(EventApplyResult.Applied, "control events apply while the node is locked");

        var services = new ServiceCollection()
            .AddSingleton<IWhitelistRepository>(WhitelistRepo)
            .AddSingleton<IDekRotationStateRepository>(stateRepo)
            .AddSingleton<INodeIdentityRepository>(NodeRepo)
            .AddSingleton<IEventLogRepository>(EventLogRepo)
            .AddSingleton<IAuditLogRepository>(new AuditLogRepository(Factory))
            .BuildServiceProvider();
        var applier = new PeerDekRotationApplier(services.GetRequiredService<IServiceScopeFactory>(), Session, Factory,
            new MaintenanceModeService(), NullLogger<PeerDekRotationApplier>.Instance);
        (await Session.UnlockAsync(Password)).Should().BeTrue();
        await applier.RetryPendingAutoAcceptsAsync();

        (await stateRepo.GetAsync(commit.EventId.ToString()))!.State.Should().Be(DekRotationState.Applied);
        Session.GetMasterDek().Should().Equal(newDek);
    }

    [Fact]
    public async Task The_package_rule_for_a_held_commit_is_taken_only_for_the_same_event_the_log_holds()
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        var (_, seed) = Ed25519Signer.GenerateKeyPair();
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = Guid.NewGuid(), LamportTs = 5, EventType = EventTypes.SnapshotCheckpoint, Payload = "{}",
            ProtocolVersion = 1, CreatedAt = DateTime.UtcNow,
        };
        evt.Signature = Ed25519Signer.Sign(seed, EventSignature.BuildPayload(evt));

        (await NodeEventVerifier.ReceiptModeAsync(EventLogRepo, evt)).Should().Be(NodeKeyMode.Live, "not received yet: first delivery");
        await EventLogRepo.AppendAsync(evt);
        (await NodeEventVerifier.ReceiptModeAsync(EventLogRepo, evt)).Should().Be(NodeKeyMode.Package);
        var altered = new SyncEvent
        {
            EventId = evt.EventId, NodeId = evt.NodeId, LamportTs = evt.LamportTs, EventType = evt.EventType, Payload = "{\"x\":1}",
            ProtocolVersion = 1, CreatedAt = evt.CreatedAt, Signature = evt.Signature,
        };
        (await NodeEventVerifier.ReceiptModeAsync(EventLogRepo, altered)).Should().Be(NodeKeyMode.Live, "another body under the same id is not the held event");
    }
}
