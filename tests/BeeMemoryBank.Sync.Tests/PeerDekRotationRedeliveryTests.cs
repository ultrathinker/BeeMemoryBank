using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.DekRotation;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Sync re-delivers events. A COMMIT for a rotation this node has already applied must be a no-op
/// for the mobile/CLI applier: re-running it would treat the current DEK as the old one.
/// </summary>
public class PeerDekRotationRedeliveryTests : SyncTestFixture
{
    private async Task<int> EpochAsync()
    {
        using var conn = Factory.CreateConnection();
        return await conn.ExecuteScalarAsync<int>("SELECT dek_epoch FROM tbl_node_identity");
    }

    [Fact]
    public async Task RedeliveredCommit_ForAnAppliedRotation_IsANoOp()
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);

        var stateRepo = new DekRotationStateRepository(Factory);
        var services = new ServiceCollection()
            .AddSingleton<IWhitelistRepository>(WhitelistRepo)
            .AddSingleton<IDekRotationStateRepository>(stateRepo)
            .AddSingleton<INodeIdentityRepository>(NodeRepo)
            .AddSingleton<IEventLogRepository>(EventLogRepo)
            .BuildServiceProvider();
        var applier = new PeerDekRotationApplier(
            services.GetRequiredService<IServiceScopeFactory>(), Session, Factory,
            new MaintenanceModeService(), NullLogger<PeerDekRotationApplier>.Instance);

        var (peerPub, peerSeed) = Ed25519Signer.GenerateKeyPair();
        var peerNodeId = Guid.NewGuid();
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = peerNodeId, DisplayName = "Initiator", Ed25519PublicKey = peerPub, Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        var oldDek = Session.GetMasterDek();
        var newDek = RandomNumberGenerator.GetBytes(32);
        var (enc, iv) = MasterKeyManager.WrapMasterDek(newDek, oldDek);
        Array.Clear(oldDek);
        var epoch = await EpochAsync();
        var payload = new DekRotationCommitPayload(
            ProposedEventId: Guid.NewGuid().ToString(), NewDekEpoch: epoch + 1, RotationTs: DateTime.UtcNow.ToString("O"),
            OriginatorNodeId: peerNodeId.ToString(), EncryptedNewDek: Convert.ToBase64String(enc), Iv: Convert.ToBase64String(iv));
        var commit = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = peerNodeId, LamportTs = 1_000_000, EventType = EventTypes.DekRotationCommit,
            Payload = JsonSerializer.Serialize(payload), ProtocolVersion = 1, CreatedAt = DateTime.UtcNow,
        };
        commit.Signature = Ed25519Signer.Sign(peerSeed, EventSignature.BuildPayload(commit));
        await stateRepo.UpsertAsync(new DekRotationStateRow(
            commit.EventId.ToString(), DekRotationState.Committing, payload.ProposedEventId, payload.RotationTs,
            null, null, null, null, null, null, null, DateTime.UtcNow.ToString("O"), DateTime.UtcNow.ToString("O")));

        await applier.AutoAcceptCommitAsync(commit);
        (await stateRepo.GetAsync(commit.EventId.ToString()))!.State.Should().Be(DekRotationState.Applied);
        (await EpochAsync()).Should().Be(epoch + 1);

        // Re-delivered after it was applied.
        var redelivery = () => applier.AutoAcceptCommitAsync(commit);
        await redelivery.Should().NotThrowAsync("a settled commit is skipped, not re-run or failed");

        (await stateRepo.GetAsync(commit.EventId.ToString()))!.State.Should().Be(DekRotationState.Applied);
        (await EpochAsync()).Should().Be(epoch + 1, "the epoch must not move again");
        Session.GetMasterDek().Should().Equal(newDek, "the DEK must not change again");
    }
}
