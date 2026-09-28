using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Review L-stage0 round 2 #2 and #4: on a blind node a restore_network and a rotation commit are
/// closed while the event is being applied, not by a background task. Proven with a failure point:
/// when closing fails, the event must stay out of the log (so it is delivered again) — a background
/// task would have let it in already, with nothing left to close the state afterwards.
/// </summary>
public class BlindInlineApplyTests : IDisposable
{
    private readonly BlindNodeFactory _blind = new();

    public void Dispose() => _blind.Dispose();

    [Fact]
    public async Task RestoreNetwork_WhoseFlagFails_IsNotRecorded()
    {
        var initiator = new FailingRestoreInitiator();
        using var node = _blind.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IRestoreInitiator>(initiator)));
        var (hubId, hubKey) = await WhitelistSuperadminAsync(node.Services);
        var evt = Signed(hubId, hubKey, EventTypes.RestoreNetwork, JsonSerializer.Serialize(new RestoreNetworkEventPayload(
            "00", DateTime.UtcNow.ToString("O"), 1, DateTime.UtcNow.AddHours(1).ToString("O"), "https://hub.example", FilterSecrets: true)));

        using var scope = node.Services.CreateScope();
        var apply = () => scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(evt);

        await apply.Should().ThrowAsync<InvalidOperationException>();
        (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().ExistsAsync(evt.EventId)).Should().BeFalse();
    }

    [Fact]
    public async Task RotationCommit_WhoseCloseFails_IsNotRecorded_AndIsClosedWhenDeliveredAgain()
    {
        var applier = new FailOnceRotationApplier();
        using var node = _blind.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddScoped<IDekRotationApplier>(_ => applier)));
        var (hubId, hubKey) = await WhitelistSuperadminAsync(node.Services);
        var now = DateTime.UtcNow.ToString("O");
        var proposed = Signed(hubId, hubKey, EventTypes.DekRotationProposed, JsonSerializer.Serialize(
            new DekRotationProposedPayload(2, now, DateTime.UtcNow.AddHours(1).ToString("O"), hubId.ToString())));
        var commit = Signed(hubId, hubKey, EventTypes.DekRotationCommit, JsonSerializer.Serialize(
            new DekRotationCommitPayload(proposed.EventId.ToString(), 2, now, hubId.ToString())));

        using var scope = node.Services.CreateScope();
        var applierUnderTest = scope.ServiceProvider.GetRequiredService<EventApplier>();
        var log = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
        await applierUnderTest.ApplyAsync(proposed);
        var first = () => applierUnderTest.ApplyAsync(commit);

        await first.Should().ThrowAsync<InvalidOperationException>();
        (await log.ExistsAsync(commit.EventId)).Should().BeFalse("the commit must come again");
        await applierUnderTest.ApplyAsync(commit);
        applier.Closed.Should().Contain(commit.EventId, "the second delivery closes the rotation it left open");
    }

    private static async Task<(Guid Id, byte[] PrivateKey)> WhitelistSuperadminAsync(IServiceProvider services)
    {
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        var id = Guid.NewGuid();
        await services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = "Hub", Ed25519PublicKey = pub, Status = "A", IsSuperadmin = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        return (id, priv);
    }

    private static SyncEvent Signed(Guid originator, byte[] privateKey, string type, string payload)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = originator, LamportTs = 5, EventType = type, Payload = payload,
            ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow
        };
        evt.Signature = Ed25519Signer.Sign(privateKey, EventSignature.BuildPayload(evt));
        return evt;
    }

    private sealed class FailingRestoreInitiator : IRestoreInitiator
    {
        public bool RequiresApproval => false;
        public Task AcceptRestoreAsync(string eventId, RestoreNetworkEventPayload payload, SyncEvent restoreEvent) =>
            throw new InvalidOperationException("flag write failed");
        public Task RetryPendingRestoresAsync() => Task.CompletedTask;
    }

    private sealed class FailOnceRotationApplier : IDekRotationApplier
    {
        private bool _failed;
        public List<Guid> Closed { get; } = [];
        public bool RequiresApproval => false;

        public Task AutoAcceptCommitAsync(SyncEvent commitEvent)
        {
            if (!_failed)
            {
                _failed = true;
                throw new InvalidOperationException("close failed");
            }
            Closed.Add(commitEvent.EventId);
            return Task.CompletedTask;
        }

        public Task RetryPendingAutoAcceptsAsync() => Task.CompletedTask;
    }
}
