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
/// Removal rotations apply by themselves (release 2.12, plan WS-A5): a commit whose reason is <c>offboarding:&lt;id&gt;</c>, signed by a node this
/// node sees as a superadmin, is applied whatever <c>auto_accept_dek_rotation</c> says - by an unlocked node at once, by a locked one at its next
/// unlock - with a warning and an audit entry. Without the reason, or from a node that is not a superadmin, the setting decides as before.
/// </summary>
public class RemovalRotationAutoApplyTests : SyncTestFixture
{
    private Guid _peer;
    private byte[] _peerSeed = null!;

    private async Task<int> EpochAsync()
    {
        using var conn = Factory.CreateConnection();
        return await conn.ExecuteScalarAsync<int>("SELECT dek_epoch FROM tbl_node_identity");
    }

    private async Task AddPeerAsync(bool superadmin, bool autoAccept)
    {
        var (pub, seed) = Ed25519Signer.GenerateKeyPair();
        _peer = Guid.NewGuid();
        _peerSeed = seed;
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = _peer, DisplayName = "Initiator", Ed25519PublicKey = pub, Status = "A", IsSuperadmin = superadmin,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await WhitelistRepo.SetAutoAcceptDekRotationAsync(_peer.ToString(), autoAccept);
        using var conn = Factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE tbl_whitelist SET is_superadmin = @s WHERE node_id = @n COLLATE NOCASE",
            new { s = superadmin ? 1 : 0, n = _peer.ToString() });
        (await WhitelistRepo.GetByNodeIdAsync(_peer))!.IsSuperadmin.Should().Be(superadmin);
    }

    private SyncEvent Commit(string? reason, byte[] newDek)
    {
        var oldDek = Session.GetMasterDek();
        var (enc, iv) = MasterKeyManager.WrapMasterDek(newDek, oldDek);
        Array.Clear(oldDek);
        var payload = new DekRotationCommitPayload(
            ProposedEventId: Guid.NewGuid().ToString(), NewDekEpoch: 2, RotationTs: DateTime.UtcNow.ToString("O"),
            OriginatorNodeId: _peer.ToString(), EncryptedNewDek: Convert.ToBase64String(enc), Iv: Convert.ToBase64String(iv), Reason: reason);
        var commit = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = _peer, LamportTs = 1_000_000, EventType = EventTypes.DekRotationCommit,
            Payload = JsonSerializer.Serialize(payload), ProtocolVersion = 1, CreatedAt = DateTime.UtcNow,
        };
        commit.Signature = Ed25519Signer.Sign(_peerSeed, EventSignature.BuildPayload(commit));
        return commit;
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("offboarding:7f3c2b1a-0000-4000-8000-000000000001", true)]
    [InlineData("offboarding:x", true)]
    [InlineData("offboarding:", false)]
    [InlineData("offboarding:../etc", false)]
    [InlineData("because", false)]
    [InlineData("OFFBOARDING:abc", false)]
    public void OnlyNoReasonOrAWellFormedRemovalIsAccepted(string? reason, bool valid)
    {
        DekRotationReasons.IsValid(reason).Should().Be(valid);
        DekRotationReasons.IsRemoval(reason).Should().Be(valid && reason is not null);
    }

    [Theory]
    [InlineData("offboarding:p1", true, false, true, true)]
    [InlineData("offboarding:p1", false, false, false, false)]
    [InlineData(null, true, false, false, false)]
    [InlineData(null, true, true, true, false)]
    [InlineData("offboarding:p1", false, true, true, false)]
    public async Task TheDecision(string? reason, bool superadmin, bool autoAccept, bool apply, bool removal)
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);
        await AddPeerAsync(superadmin, autoAccept);

        var decision = await DekRotationReasons.DecideAutoApplyAsync(Commit(reason, RandomNumberGenerator.GetBytes(32)), WhitelistRepo);

        decision.Should().Be((apply, removal));
    }

    [Fact]
    public async Task ALockedNode_AppliesARemovalRotationAtItsNextUnlock_WithTheSettingOff_AndWritesTheAuditEntry()
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);
        await AddPeerAsync(superadmin: true, autoAccept: false);
        var stateRepo = new DekRotationStateRepository(Factory);
        var services = new ServiceCollection()
            .AddSingleton<IWhitelistRepository>(WhitelistRepo)
            .AddSingleton<IDekRotationStateRepository>(stateRepo)
            .AddSingleton<INodeIdentityRepository>(NodeRepo)
            .AddSingleton<IEventLogRepository>(EventLogRepo)
            .AddSingleton<IAuditLogRepository>(new AuditLogRepository(Factory))
            .BuildServiceProvider();
        var applier = new PeerDekRotationApplier(services.GetRequiredService<IServiceScopeFactory>(), Session, Factory,
            new MaintenanceModeService(), NullLogger<PeerDekRotationApplier>.Instance);
        var newDek = RandomNumberGenerator.GetBytes(32);
        var removal = Commit("offboarding:p1", newDek);
        var ordinary = Commit(null, RandomNumberGenerator.GetBytes(32));
        await EventLogRepo.AppendAsync(removal);
        foreach (var c in new[] { removal })
            await stateRepo.UpsertAsync(new DekRotationStateRow(c.EventId.ToString(), DekRotationState.Committing, Guid.NewGuid().ToString(),
                DateTime.UtcNow.ToString("O"), null, null, null, null, null, null, null, DateTime.UtcNow.ToString("O"), DateTime.UtcNow.ToString("O")));
        Session.Lock();

        await applier.RetryPendingAutoAcceptsAsync();
        (await stateRepo.GetAsync(removal.EventId.ToString()))!.State.Should().Be(DekRotationState.Committing, "a locked node cannot apply it");

        (await Session.UnlockAsync(Password)).Should().BeTrue();
        await applier.RetryPendingAutoAcceptsAsync();

        (await stateRepo.GetAsync(removal.EventId.ToString()))!.State.Should().Be(DekRotationState.Applied,
            "a removal rotation of a superadmin node is applied at the next unlock although auto-accept is off");
        Session.GetMasterDek().Should().Equal(newDek);
        using var conn = Factory.CreateConnection();
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_audit_log WHERE action = @a AND entity_id = @id",
            new { a = RemovalRotationRecord.AuditAction, id = removal.EventId.ToString() })).Should().Be(1);
        ordinary.Should().NotBeNull();
    }

    [Fact]
    public async Task AnOrdinaryRotation_WithTheSettingOff_StaysPendingAfterTheUnlock()
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);
        await AddPeerAsync(superadmin: true, autoAccept: false);
        var stateRepo = new DekRotationStateRepository(Factory);
        var services = new ServiceCollection()
            .AddSingleton<IWhitelistRepository>(WhitelistRepo)
            .AddSingleton<IDekRotationStateRepository>(stateRepo)
            .AddSingleton<INodeIdentityRepository>(NodeRepo)
            .AddSingleton<IEventLogRepository>(EventLogRepo)
            .BuildServiceProvider();
        var applier = new PeerDekRotationApplier(services.GetRequiredService<IServiceScopeFactory>(), Session, Factory,
            new MaintenanceModeService(), NullLogger<PeerDekRotationApplier>.Instance);
        var ordinary = Commit(null, RandomNumberGenerator.GetBytes(32));
        await EventLogRepo.AppendAsync(ordinary);
        await stateRepo.UpsertAsync(new DekRotationStateRow(ordinary.EventId.ToString(), DekRotationState.Committing, Guid.NewGuid().ToString(),
            DateTime.UtcNow.ToString("O"), null, null, null, null, null, null, null, DateTime.UtcNow.ToString("O"), DateTime.UtcNow.ToString("O")));
        var epoch = await EpochAsync();

        await applier.RetryPendingAutoAcceptsAsync();

        (await stateRepo.GetAsync(ordinary.EventId.ToString()))!.State.Should().Be(DekRotationState.Committing, "the administrator decides, as before");
        (await EpochAsync()).Should().Be(epoch);
    }
}
