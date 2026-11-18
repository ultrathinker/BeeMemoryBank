using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// a <c>node_rekey_request</c> that reaches a node before the <c>offboarding_state</c> of its process is deferred (retried),
/// not refused - refused, the node would never serve it and S1 would wait for it until it is revoked. One from a node that does not drive the
/// process stays refused.
/// </summary>
public class RemovalEventOrderTests : SyncTestFixture
{
    private Guid _driver;
    private byte[] _driverSeed = null!;
    private long _lamport = 100;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "Receiver", Password);
        (var pub, _driverSeed) = Ed25519Signer.GenerateKeyPair();
        _driver = Guid.NewGuid();
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = _driver, DisplayName = "Office", Ed25519PublicKey = pub, Status = "A", IsSuperadmin = true, Ownership = OwnershipKinds.Shared,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
    }

    private SyncEvent Signed(string type, object payload)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = _driver, LamportTs = ++_lamport, EventType = type, Payload = JsonSerializer.Serialize(payload),
            ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow, Signature = [],
        };
        evt.EntityId = EventEntityId.Derive(evt);
        evt.Signature = Ed25519Signer.Sign(_driverSeed, EventSignature.BuildPayload(evt));
        return evt;
    }

    [Fact]
    public async Task A_rekey_request_before_its_process_is_deferred_and_applies_once_the_process_is_here()
    {
        var process = Guid.NewGuid();
        var me = (await NodeRepo.GetAsync())!.NodeId;
        var request = Signed(EventTypes.NodeRekeyRequest,
            new NodeRekeyRequestPayload(process.ToString("D"), EventApplier.RemovalRekeyReason(process), [me.ToString("D")]));
        var state = Signed(EventTypes.OffboardingState, new OffboardingStatePayload(process.ToString("D"), Guid.NewGuid().ToString("D"),
            _driver.ToString("D"), _driver.ToString("D"), "delete", 1, DateTime.UtcNow));

        var early = () => EventApplier.ApplyAsync(request);
        var failure = (await early.Should().ThrowAsync<Exception>()).Which;
        SyncFailureClassifier.Classify(failure).Should().Be(SyncFailureKind.Deferred, "the process may still be on its way");

        (await EventApplier.ApplyAsync(state)).Should().Be(EventApplyResult.Applied);
        (await EventApplier.ApplyAsync(request)).Should().Be(EventApplyResult.Applied, "retried once the process is here");
    }

    [Fact]
    public async Task A_rekey_request_from_a_node_that_does_not_drive_the_process_stays_refused()
    {
        var process = Guid.NewGuid();
        var other = Guid.NewGuid();
        using (var c = Factory.CreateConnection())
            await c.ExecuteAsync(
                @"INSERT INTO tbl_offboarding (process_id, person_id, user_id, accounts_node_id, driver_node_id, target, step, state_json, started_by, started_at,
                      updated_at, closed_at, outcome, version, origin)
                  VALUES (@p, @person, NULL, @o, @o, 'delete', 1, '{}', '', @now, @now, NULL, NULL, 1, 'replicated')",
                new { p = process.ToString("D"), person = Guid.NewGuid().ToString("D"), o = other.ToString("D"), now = DateTime.UtcNow.ToString("O") });
        var me = (await NodeRepo.GetAsync())!.NodeId;
        var request = Signed(EventTypes.NodeRekeyRequest,
            new NodeRekeyRequestPayload(process.ToString("D"), EventApplier.RemovalRekeyReason(process), [me.ToString("D")]));

        var refused = () => EventApplier.ApplyAsync(request);
        var failure = (await refused.Should().ThrowAsync<UnauthorizedAccessException>()).Which;
        SyncFailureClassifier.Classify(failure).Should().Be(SyncFailureKind.Permanent);
    }
}
