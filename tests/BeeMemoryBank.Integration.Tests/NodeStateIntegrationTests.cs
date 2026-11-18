using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

public class NodeStateIntegrationTests : IAsyncLifetime
{
    private const string Password = "nodeStateIntegrationPw1";
    private RecoveryTestFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new RecoveryTestFactory(allowOpenCopies: false);
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync("NodeStateHub", Password);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Pull_ServesNodeStateToOlderPeerWithoutNodeStateV1Capability()
    {
        var peerId = Guid.NewGuid();
        var (pub, _) = Ed25519Signer.GenerateKeyPair();
        using (var scope = _factory.Services.CreateScope())
        {
            var wl = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            var logger = scope.ServiceProvider.GetRequiredService<IEventLogger>();
            var entry = new WhitelistEntry
            {
                NodeId = peerId,
                Ed25519PublicKey = pub,
                DisplayName = "OldRelay",
                IsSuperadmin = false,
                Status = WhitelistStatuses.Active,
                Ownership = OwnershipKinds.Shared,
            };
            entry.StampLocal(await logger.LogWhitelistAddAsync(entry), WhitelistEventConcerns.OfAddFor(entry));
            await wl.CreateAsync(entry);

            var publisher = scope.ServiceProvider.GetRequiredService<NodeStatePublisher>();
            var draft = (await publisher.BuildDraftAsync())!;
            await publisher.PublishAsync(draft);
        }

        // Old peer authenticates advertising only EnvelopeV1 (no NodeStateV1, no PeerEndpointsV1)
        var tokens = _factory.Services.GetRequiredService<SyncTokenStore>();
        var oldPeerToken = tokens.IssueToken(peerId, SyncProtocolVersion.Current, [MetadataCapabilities.EnvelopeV1]);

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/sync/events?afterSequence=0");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oldPeerToken);
        var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain(EventTypes.NodeState, "older peers must still relay node_state events");
    }

    [Fact]
    public async Task Compaction_RemovesSupersededNodeStateEvents_KeepingLatestEventAndRow()
    {
        SyncEvent firstState;
        SyncEvent secondState;
        Guid selfId;
        long seqBeforeTail;
        using (var scope = _factory.Services.CreateScope())
        {
            var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
            selfId = (await nodeRepo.GetAsync())!.NodeId;
            var publisher = scope.ServiceProvider.GetRequiredService<NodeStatePublisher>();

            var d1 = (await publisher.BuildDraftAsync())!;
            d1.AppVersion = "2.12.0-first";
            firstState = await publisher.PublishAsync(d1);

            var d2 = (await publisher.BuildDraftAsync())!;
            d2.AppVersion = "2.12.0-second";
            secondState = await publisher.PublishAsync(d2);

            // Add a couple of control events afterwards so CP can sit strictly above secondState and below HeadSeq
            var logger = scope.ServiceProvider.GetRequiredService<IEventLogger>();
            for (var i = 0; i < 3; i++)
            {
                var (pk, _) = Ed25519Signer.GenerateKeyPair();
                await logger.LogWhitelistAddAsync(new WhitelistEntry
                {
                    NodeId = Guid.NewGuid(),
                    DisplayName = $"Filler{i}",
                    Ed25519PublicKey = pk,
                    Status = WhitelistStatuses.Active,
                    Ownership = OwnershipKinds.Shared
                });
            }

            seqBeforeTail = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync() - 1;
        }

        // Compact up to seqBeforeTail (which covers both firstState and secondState), accepting no peers at risk
        using (var scope = _factory.Services.CreateScope())
        {
            var compaction = scope.ServiceProvider.GetRequiredService<CompactionService>();
            await compaction.ExecuteAsync(explicitCp: seqBeforeTail, reason: "test-node-state", acceptCuttingOffPeers: true);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var eventLog = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
            (await eventLog.ExistsAsync(firstState.EventId)).Should().BeFalse("superseded node_state is pruned");
            (await eventLog.ExistsAsync(secondState.EventId)).Should().BeTrue("latest node_state per node survives compaction");

            var stored = await scope.ServiceProvider.GetRequiredService<NodeStateStore>().GetAsync(selfId);
            stored.Should().NotBeNull();
            stored!.AppVersion.Should().Be("2.12.0-second");
        }
    }

    [Fact]
    public async Task RealAnnouncer_PublishesNodeState_AndSecondNodeAppliesPulledEvent()
    {
        using var nodeA = new BmbWebApplicationFactory();
        await nodeA.InitializeNodeAsync("NodeA", Password);
        using var clientA = nodeA.CreateClient();
        (await clientA.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();

        using var nodeB = new BmbWebApplicationFactory();
        await nodeB.JoinNodeAsync(clientA, "NodeB", Password);
        using var clientB = nodeB.CreateClient();
        (await clientB.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();

        Guid nodeAId;
        NodeStateRecord stateA;
        using (var scopeA = nodeA.Services.CreateScope())
        {
            nodeAId = (await scopeA.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
            var announcer = nodeA.Services.GetRequiredService<NodeStateAnnouncer>();
            (await announcer.TickAsync(DateTime.UtcNow)).Should().BeTrue();
            stateA = (await scopeA.ServiceProvider.GetRequiredService<NodeStateStore>().GetAsync(nodeAId))!;
            stateA.Should().NotBeNull();
            stateA.IdentityKeyFp.Should().NotBeNullOrWhiteSpace();
            stateA.DekFp.Should().NotBeNullOrWhiteSpace();
        }

        // Authenticate nodeB on nodeA using nodeB's real key and pull over HTTP, then apply through nodeB's real EventApplier
        var challenge = (await (await clientA.PostAsync("/api/sync/challenge", null)).Content.ReadFromJsonAsync<ChallengePayload>())!;
        Guid nodeBId;
        byte[] challengeSig;
        using (var scopeB = nodeB.Services.CreateScope())
        {
            var idB = (await scopeB.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            nodeBId = idB.NodeId;
            var sessionB = scopeB.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>();
            var dekB = sessionB.GetMasterDek();
            try
            {
                var sigPayload = "BMB-CHALLENGE-V2\0"u8.ToArray()
                    .Concat(challenge.ServerNodeId.ToByteArray())
                    .Concat(Convert.FromBase64String(challenge.Challenge))
                    .ToArray();
                challengeSig = NodeIdentityVault.SignWithIdentity(
                    idB.Ed25519PrivateKey, idB.Ed25519PrivateKeyIV, idB.Ed25519PrivateKeyV,
                    idB.NodeId, dekB, sigPayload);
            }
            finally { Array.Clear(dekB); }
        }

        var authResp = await clientA.PostAsJsonAsync("/api/sync/authenticate", new
        {
            NodeId = nodeBId,
            ChallengeB64 = challenge.Challenge,
            SignatureB64 = Convert.ToBase64String(challengeSig),
            ProtocolVersion = SyncProtocolVersion.Current,
            Capabilities = MetadataCapabilities.Advertised
        });
        authResp.EnsureSuccessStatusCode();
        var token = (await authResp.Content.ReadFromJsonAsync<TokenPayload>())!.Token;

        using var pullReq = new HttpRequestMessage(HttpMethod.Get, "/api/sync/events?afterSequence=0");
        pullReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var pullResp = await clientA.SendAsync(pullReq);
        pullResp.EnsureSuccessStatusCode();
        var pulled = (await pullResp.Content.ReadFromJsonAsync<List<SyncEvent>>())!;
        pulled.Should().Contain(e => e.EventType == EventTypes.NodeState && e.NodeId == nodeAId);

        using (var scopeB = nodeB.Services.CreateScope())
        {
            var applierB = scopeB.ServiceProvider.GetRequiredService<EventApplier>();
            foreach (var evt in pulled)
                await applierB.ApplyAsync(evt);

            var stateB = await scopeB.ServiceProvider.GetRequiredService<NodeStateStore>().GetAsync(nodeAId);
            stateB.Should().NotBeNull("nodeB must materialise nodeA's real announced state via EventApplier");
            stateB!.IdentityKeyFp.Should().Be(stateA.IdentityKeyFp);
            stateB.DekFp.Should().Be(stateA.DekFp);
            stateB.AppVersion.Should().Be(stateA.AppVersion);
            stateB.Platform.Should().Be(stateA.Platform);
            stateB.RuntimeKind.Should().Be(stateA.RuntimeKind);
            stateB.EventId.Should().Be(stateA.EventId);
        }
    }

    [Fact]
    public async Task RealAnnouncer_CarriesTheV1DoorCountAndTheBridge_AndASecondNodeStoresThem()
    {
        // 2.12 cross review, finding 2: IDekBridgeStatus reaches node_state through a contributor of the vault, end to end.
        using var nodeA = new BmbWebApplicationFactory();
        await nodeA.InitializeNodeAsync("BridgeA", Password);
        (await nodeA.Services.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>().UnlockAsync(Password)).Should().BeTrue();
        using var clientA = nodeA.CreateClient();
        using var nodeB = new BmbWebApplicationFactory();
        await nodeB.JoinNodeAsync(clientA, "BridgeB", Password);
        (await nodeB.Services.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>().UnlockAsync(Password)).Should().BeTrue();

        Guid nodeAId;
        using (var scopeA = nodeA.Services.CreateScope())
            nodeAId = (await scopeA.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        var announcer = nodeA.Services.GetRequiredService<NodeStateAnnouncer>();
        (await announcer.TickAsync(DateTime.UtcNow)).Should().BeTrue();
        using (var scopeA = nodeA.Services.CreateScope())
        {
            var fresh = (await scopeA.ServiceProvider.GetRequiredService<NodeStateStore>().GetAsync(nodeAId))!;
            fresh.Extra.Should().NotBeNull();
            fresh.Extra![NodeStateExtraKeys.V1Doors].Should().Be("0", "a new node has sealed doors only");
            fresh.Extra[NodeStateExtraKeys.DekBridge].Should().Be("0");
        }

        // An earlier release's door, and the chain material of a rotation that had to keep it reachable.
        await BeeMemoryBank.Integration.Tests.Offboarding.OffboardingStand.MakeDoorV1Async(nodeA, "admin", Password);
        using (var scopeA = nodeA.Services.CreateScope())
        {
            var now = DateTime.UtcNow.ToString("O");
            var rotationId = Guid.NewGuid().ToString();
            await scopeA.ServiceProvider.GetRequiredService<IDekRotationStateRepository>()
                .UpsertAsync(new DekRotationStateRow(rotationId, DekRotationState.Applied, rotationId, now, now, null, null, null, null, null, null, now, now));
            using var conn = scopeA.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE tbl_dek_rotation_state SET chain_encrypted_new_dek = 'AAAA', chain_iv = 'AAAA' WHERE event_id = @rotationId",
                new { rotationId });
        }
        announcer.RequestImmediateCheck();   // as an open or a rotation does; otherwise a changed report waits out the debounce
        (await announcer.TickAsync(DateTime.UtcNow)).Should().BeTrue("the report changed");

        await BeeMemoryBank.Integration.Tests.Offboarding.OffboardingStand.SyncAsync(nodeB, nodeA);

        using var scopeB = nodeB.Services.CreateScope();
        var stateB = await scopeB.ServiceProvider.GetRequiredService<NodeStateStore>().GetAsync(nodeAId);
        stateB.Should().NotBeNull("nodeB applies nodeA's announced state through its real EventApplier");
        stateB!.Extra.Should().NotBeNull();
        stateB.Extra![NodeStateExtraKeys.V1Doors].Should().Be("1");
        stateB.Extra[NodeStateExtraKeys.DekBridge].Should().Be("1");
    }

    [Fact]
    public async Task RealRelayHop_NodeStateReachesThirdNodeThroughRelayWithoutNodeStateCapability()
    {
        // A -> R (authenticates without node-state-v1 capability) -> C (syncs only with R)
        using var nodeA = new BmbWebApplicationFactory();
        await nodeA.InitializeNodeAsync("OriginNodeA", Password);
        using var clientA = nodeA.CreateClient();
        (await clientA.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();

        using var nodeR = new BmbWebApplicationFactory();
        await nodeR.JoinNodeAsync(clientA, "RelayNodeR", Password);
        using var clientR = nodeR.CreateClient();
        (await clientR.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();

        using var nodeC = new BmbWebApplicationFactory();
        await nodeC.JoinNodeAsync(clientR, "ThirdNodeC", Password);
        using var clientC = nodeC.CreateClient();
        (await clientC.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();

        Guid nodeAId;
        Guid nodeRId;
        NodeStateRecord stateA;
        using (var scopeA = nodeA.Services.CreateScope())
        {
            var idA = (await scopeA.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            nodeAId = idA.NodeId;

            // Mirror what a real snapshot-join from relay R gives nodeC: nodeA in C's whitelist (with no node_state yet).
            using (var scopeCInit = nodeC.Services.CreateScope())
            {
                await scopeCInit.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
                {
                    NodeId = idA.NodeId,
                    DisplayName = idA.DisplayName,
                    Ed25519PublicKey = idA.Ed25519PublicKey,
                    Status = WhitelistStatuses.Active,
                    IsSuperadmin = true,
                    Ownership = OwnershipKinds.Shared,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
            }

            var announcerA = nodeA.Services.GetRequiredService<NodeStateAnnouncer>();
            (await announcerA.TickAsync(DateTime.UtcNow)).Should().BeTrue();
            stateA = (await scopeA.ServiceProvider.GetRequiredService<NodeStateStore>().GetAsync(nodeAId))!;
        }

        // Before relay hop, nodeC has no node_state for nodeA
        using (var scopeC0 = nodeC.Services.CreateScope())
            (await scopeC0.ServiceProvider.GetRequiredService<NodeStateStore>().GetAsync(nodeAId)).Should().BeNull();

        // Relay node R authenticates to A advertising only EnvelopeV1 (no NodeStateV1, no PeerEndpointsV1)
        var challenge = (await (await clientA.PostAsync("/api/sync/challenge", null)).Content.ReadFromJsonAsync<ChallengePayload>())!;
        byte[] sigR;
        using (var scopeR = nodeR.Services.CreateScope())
        {
            var idR = (await scopeR.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
            nodeRId = idR.NodeId;
            var sessionR = scopeR.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Services.SessionService>();
            var dekR = sessionR.GetMasterDek();
            try
            {
                var sigPayload = "BMB-CHALLENGE-V2\0"u8.ToArray()
                    .Concat(challenge.ServerNodeId.ToByteArray())
                    .Concat(Convert.FromBase64String(challenge.Challenge))
                    .ToArray();
                sigR = NodeIdentityVault.SignWithIdentity(
                    idR.Ed25519PrivateKey, idR.Ed25519PrivateKeyIV, idR.Ed25519PrivateKeyV,
                    idR.NodeId, dekR, sigPayload);
            }
            finally { Array.Clear(dekR); }
        }

        var authR = await clientA.PostAsJsonAsync("/api/sync/authenticate", new
        {
            NodeId = nodeRId,
            ChallengeB64 = challenge.Challenge,
            SignatureB64 = Convert.ToBase64String(sigR),
            ProtocolVersion = SyncProtocolVersion.Current,
            Capabilities = new[] { MetadataCapabilities.EnvelopeV1 }
        });
        authR.EnsureSuccessStatusCode();
        var tokenR = (await authR.Content.ReadFromJsonAsync<TokenPayload>())!.Token;

        using var pullReq = new HttpRequestMessage(HttpMethod.Get, "/api/sync/events?afterSequence=0");
        pullReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenR);
        var pullResp = await clientA.SendAsync(pullReq);
        pullResp.EnsureSuccessStatusCode();
        var pulled = (await pullResp.Content.ReadFromJsonAsync<List<SyncEvent>>())!;
        var stateEventId = Guid.Parse(stateA.EventId!);
        pulled.Should().Contain(e => e.EventId == stateEventId);

        using (var scopeR = nodeR.Services.CreateScope())
        {
            var applierR = scopeR.ServiceProvider.GetRequiredService<EventApplier>();
            foreach (var evt in pulled)
                await applierR.ApplyAsync(evt);
            (await scopeR.ServiceProvider.GetRequiredService<IEventLogRepository>().ExistsAsync(stateEventId))
                .Should().BeTrue("relay R stores the pulled node_state event in tbl_event");
        }

        // Hop 2: nodeC never talks to nodeA directly; nodeC runs SyncClient against relay node R only
        using (var scopeC = nodeC.Services.CreateScope())
        {
            var syncC = scopeC.ServiceProvider.GetRequiredService<SyncClient>();
            await syncC.SyncWithAsync(clientR, "", nodeRId);

            (await scopeC.ServiceProvider.GetRequiredService<IEventLogRepository>().ExistsAsync(stateEventId))
                .Should().BeTrue("nodeC must receive nodeA's event across relay R");

            var stateC = await scopeC.ServiceProvider.GetRequiredService<NodeStateStore>().GetAsync(nodeAId);
            stateC.Should().NotBeNull("third node C must apply nodeA's node_state relayed through R");
            stateC!.IdentityKeyFp.Should().Be(stateA.IdentityKeyFp);
            stateC.DekFp.Should().Be(stateA.DekFp);
            stateC.AppVersion.Should().Be(stateA.AppVersion);
            stateC.RuntimeKind.Should().Be(stateA.RuntimeKind);
            stateC.EventId.Should().Be(stateA.EventId);
        }
    }

    private sealed record ChallengePayload(string Challenge, Guid ServerNodeId);
    private sealed record TokenPayload(string Token);
}

public class SealedNodeStateTests(SealedNodeTemplate template) : IClassFixture<SealedNodeTemplate>
{
    [Fact]
    public async Task SealedLockedNode_DoesNotEmitBeforeOpen_AndEmitsWithDekFpAfterUnlock()
    {
        using var node = await SealedApiNode.StartAsync(template);
        using var client = node.Client();

        var announcer = node.Services.GetRequiredService<NodeStateAnnouncer>();
        (await announcer.TickAsync(DateTime.UtcNow)).Should().BeFalse("locked sealed vault stays closed and emits nothing");

        // Unlock -> VaultGate opens -> NodeStateOpenStep wakes announcer -> ensure at least one post-open tick completes
        (await client.PostAsJsonAsync("/api/session/unlock", new { password = SealedNodeTemplate.Password }))
            .EnsureSuccessStatusCode();

        await announcer.TickAsync(DateTime.UtcNow);

        using var scope = node.Services.CreateScope();
        var identity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var state = await scope.ServiceProvider.GetRequiredService<NodeStateStore>().GetAsync(identity.NodeId);
        state.Should().NotBeNull();
        state!.DekFp.Should().NotBeNullOrWhiteSpace("unlocked full node contributes DEK fingerprint");
        state.IdentityKeyFp.Should().Be(NodeStateDraft.IdentityFingerprint(identity.Ed25519PublicKey));
    }
}
