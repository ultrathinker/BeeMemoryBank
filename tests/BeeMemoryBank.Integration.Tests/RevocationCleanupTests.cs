using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Channels;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

public class RevocationCleanupTests : IAsyncLifetime
{
    private const string Password = "revocationCleanupPw1";
    private RecoveryTestFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new RecoveryTestFactory(allowOpenCopies: false);
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync("RevokeHub", Password);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> AdmitPeerAsync(string name, bool superadmin = false)
    {
        var nodeId = Guid.NewGuid();
        var (pub, _) = Ed25519Signer.GenerateKeyPair();
        using var scope = _factory.Services.CreateScope();
        var wl = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var logger = scope.ServiceProvider.GetRequiredService<IEventLogger>();
        var entry = new WhitelistEntry
        {
            NodeId = nodeId,
            Ed25519PublicKey = pub,
            DisplayName = name,
            IsSuperadmin = superadmin,
            Status = WhitelistStatuses.Active,
            Ownership = OwnershipKinds.Shared,
        };
        var version = await logger.LogWhitelistAddAsync(entry);
        entry.StampLocal(version, WhitelistEventConcerns.OfAddFor(entry));
        await wl.CreateAsync(entry);
        return nodeId;
    }

    [Fact]
    public async Task LocalRevoke_DropsTokenChannelsContactReachabilityAndRelayQueue()
    {
        var revokedId = await AdmitPeerAsync("ToRevoke");
        var keptId = await AdmitPeerAsync("Kept");

        var tokens = _factory.Services.GetRequiredService<SyncTokenStore>();
        var revokedToken = tokens.IssueToken(revokedId, SyncProtocolVersion.Current, MetadataCapabilities.Advertised);
        var keptToken = tokens.IssueToken(keptId, SyncProtocolVersion.Current, MetadataCapabilities.Advertised);

        var contacts = _factory.Services.GetRequiredService<PeerContactLog>();
        contacts.NoteInbound(revokedId, DateTime.UtcNow, "10.0.0.5");
        contacts.NoteOutboundOk(revokedId, DateTime.UtcNow, "https://10.0.0.5:5300");
        contacts.NoteInbound(keptId, DateTime.UtcNow, "10.0.0.6");

        var reachability = _factory.Services.GetRequiredService<PeerReachability>();
        reachability.NoteDirect(revokedId, ChannelScopes.Lan, DateTime.UtcNow);
        reachability.NoteDirect(keptId, ChannelScopes.Lan, DateTime.UtcNow);

        using (var scope = _factory.Services.CreateScope())
        {
            var channels = scope.ServiceProvider.GetRequiredService<PeerChannelStore>();
            await channels.AddManualAsync(revokedId, "https://10.0.0.5:5300", DateTime.UtcNow);
            await channels.AddManualAsync(keptId, "https://10.0.0.6:5300", DateTime.UtcNow);
        }

        // Token works before revoke
        tokens.TryValidateToken(revokedToken, out _).Should().BeTrue();

        var del = await _client.DeleteAsync($"/api/whitelist/{revokedId}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Token rejected immediately (in-memory store dropped AND endpoint returns 401)
        tokens.TryValidateToken(revokedToken, out _).Should().BeFalse();
        tokens.TryValidateToken(keptToken, out _).Should().BeTrue();
        using (var req = new HttpRequestMessage(HttpMethod.Get, "/api/sync/events?afterSequence=0"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", revokedToken);
            (await _client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // Contact & reachability cleared for revoked peer only
        var revokedContact = contacts.Get(revokedId);
        revokedContact.InboundAt.Should().BeNull();
        revokedContact.OutboundOkAt.Should().BeNull();
        contacts.Get(keptId).InboundAt.Should().NotBeNull();

        reachability.DirectOf(revokedId).Should().BeNull();
        reachability.DirectOf(keptId).Should().NotBeNull();

        using (var scope = _factory.Services.CreateScope())
        {
            var channels = scope.ServiceProvider.GetRequiredService<PeerChannelStore>();
            (await channels.GetAsync(revokedId)).Should().BeEmpty();
            (await channels.GetAsync(keptId)).Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task ReplicatedRevoke_ViaEventApplier_DropsTokenChannelsAndRelayQueue()
    {
        var adminId = Guid.NewGuid();
        var (adminPub, adminSeed) = Ed25519Signer.GenerateKeyPair();
        var targetId = Guid.NewGuid();
        var (targetPub, _) = Ed25519Signer.GenerateKeyPair();

        using (var scope = _factory.Services.CreateScope())
        {
            var wl = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            var logger = scope.ServiceProvider.GetRequiredService<IEventLogger>();
            var adminEntry = new WhitelistEntry
            {
                NodeId = adminId, Ed25519PublicKey = adminPub, DisplayName = "RemoteAdmin",
                IsSuperadmin = true, Status = WhitelistStatuses.Active, Ownership = OwnershipKinds.Shared
            };
            adminEntry.StampLocal(await logger.LogWhitelistAddAsync(adminEntry), WhitelistEventConcerns.OfAddFor(adminEntry));
            await wl.CreateAsync(adminEntry);

            var targetEntry = new WhitelistEntry
            {
                NodeId = targetId, Ed25519PublicKey = targetPub, DisplayName = "RemoteTarget",
                IsSuperadmin = false, Status = WhitelistStatuses.Active, Ownership = OwnershipKinds.Shared
            };
            targetEntry.StampLocal(await logger.LogWhitelistAddAsync(targetEntry), WhitelistEventConcerns.OfAddFor(targetEntry));
            await wl.CreateAsync(targetEntry);
        }

        var tokens = _factory.Services.GetRequiredService<SyncTokenStore>();
        var targetToken = tokens.IssueToken(targetId, SyncProtocolVersion.Current);
        var contacts = _factory.Services.GetRequiredService<PeerContactLog>();
        contacts.NoteInbound(targetId, DateTime.UtcNow, "10.0.0.9");
        var reachability = _factory.Services.GetRequiredService<PeerReachability>();
        reachability.NoteDirect(targetId, ChannelScopes.Internet, DateTime.UtcNow);

        using (var scope = _factory.Services.CreateScope())
        {
            var channels = scope.ServiceProvider.GetRequiredService<PeerChannelStore>();
            await channels.AddManualAsync(targetId, "https://10.0.0.9:5300", DateTime.UtcNow);

            var applier = scope.ServiceProvider.GetRequiredService<EventApplier>();
            var evt = new SyncEvent
            {
                EventId = Guid.NewGuid(),
                NodeId = adminId,
                LamportTs = 999,
                EventType = EventTypes.WhitelistRevoke,
                Payload = JsonSerializer.Serialize(new WhitelistRevokePayload(targetId)),
                ProtocolVersion = SyncProtocolVersion.Current,
                CreatedAt = DateTime.UtcNow,
                Signature = []
            };
            evt.Signature = Ed25519Signer.Sign(adminSeed, EventSignature.BuildPayload(evt));
            await applier.ApplyAsync(evt);

            (await channels.GetAsync(targetId)).Should().BeEmpty();
        }

        tokens.TryValidateToken(targetToken, out _).Should().BeFalse();
        contacts.Get(targetId).InboundAt.Should().BeNull();
        reachability.DirectOf(targetId).Should().BeNull();
    }

    [Fact]
    public async Task SealedRelayRevoke_DropsTargetTokensAndQueuedInboxItems()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var admin = await h.AddPeerAsync(superadmin: true);
        var target = await h.AddPeerAsync();
        var bystander = await h.AddPeerAsync();
        var adminToken = await h.TokenAsync(admin);
        var targetToken = await h.TokenAsync(target);
        var bystanderToken = await h.TokenAsync(bystander);

        await h.PushAsync(targetToken, SealedRelayHost.SealedEvent(target, originSequence: 1));
        await h.PushAsync(bystanderToken, SealedRelayHost.SealedEvent(bystander, originSequence: 1));
        await h.SendAsync(HttpMethod.Post, "/api/sync/report-position?sequence=12", targetToken);
        await h.SendAsync(HttpMethod.Post, "/api/sync/report-position?sequence=5", bystanderToken);

        var revoke = SealedRelayHost.ControlEvent(admin, EventTypes.WhitelistRevoke, new WhitelistRevokePayload(target.Id));
        (await h.PushAsync(adminToken, revoke)).StatusCode.Should().Be(HttpStatusCode.OK);

        var tokens = h.App.Services.GetRequiredService<SyncTokenStore>();
        tokens.TryValidateToken(targetToken, out _).Should().BeFalse("cached bearer token is purged immediately");
        tokens.TryValidateToken(bystanderToken, out _).Should().BeTrue();

        var events = await h.Inbox.ReadAfterAsync(0, 20);
        events.Should().NotContain(r => r.Event.NodeId == target.Id);
        events.Should().Contain(r => r.Event.EventId == revoke.EventId);
        events.Should().Contain(r => r.Event.NodeId == bystander.Id);

        var reports = await h.Inbox.ListReportsAsync();
        reports.Should().NotContain(r => r.Peer == target.Id);
        reports.Should().Contain(r => r.Peer == bystander.Id);
    }

    [Fact]
    public async Task SealedRelayRevoke_StaleRevokeOlderThanMirrorMembership_DoesNotSuspendOrPurgeTokensOrInbox()
    {
        await using var h = await SealedRelayHost.StartAsync();
        var admin = await h.AddPeerAsync(superadmin: true);
        var target = await h.AddPeerAsync(memberLamportTs: 5000, memberSourceNodeId: admin.Id);
        var adminToken = await h.TokenAsync(admin);
        var targetToken = await h.TokenAsync(target);

        var targetEvt = SealedRelayHost.SealedEvent(target, originSequence: 1);
        (await h.PushAsync(targetToken, targetEvt)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await h.SendAsync(HttpMethod.Post, "/api/sync/report-position?sequence=12", targetToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Stale superadmin-signed whitelist_revoke older than the mirror row's membership version (5000)
        var staleRevoke = SealedRelayHost.ControlEvent(
            admin, EventTypes.WhitelistRevoke, new WhitelistRevokePayload(target.Id), lamportTs: 4000);
        (await h.PushAsync(adminToken, staleRevoke)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await h.MirrorStatusAsync(target.Id)).Should().Be("A", "a stale revoke must not suspend a newer membership row");
        var tokens = h.App.Services.GetRequiredService<SyncTokenStore>();
        tokens.TryValidateToken(targetToken, out _).Should().BeTrue("a stale revoke must not drop an active peer's token");

        var events = await h.Inbox.ReadAfterAsync(0, 20);
        events.Should().Contain(r => r.Event.EventId == targetEvt.EventId, "a stale revoke must not delete queued inbox events");
        events.Should().Contain(r => r.Event.EventId == staleRevoke.EventId, "the event itself stays queued for normal LWW handling at open");

        var reports = await h.Inbox.ListReportsAsync();
        reports.Should().Contain(r => r.Peer == target.Id && r.Sequence == 12);
    }
}
