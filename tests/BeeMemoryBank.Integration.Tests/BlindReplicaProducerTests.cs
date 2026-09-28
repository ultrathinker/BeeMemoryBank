using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Review L-merge #2 and #3: how the producer describes itself in a blind package. It is a reseed
/// authority only where the network verifiably says so, and it can be dialled and pinned where it
/// said it can be reached.
/// </summary>
public class BlindReplicaProducerTests : IAsyncLifetime
{
    private const string Password = "blindProducerPw1!";
    private const string PcAddress = "https://pc.test:5301";
    private static readonly string PcSpki = Base64Url.EncodeToString(SHA256.HashData("the PC's key"u8));

    private readonly BlindNodeFactory _blind = new();
    private readonly ReachablePcFactory _pc = new();
    private HttpClient _pcClient = null!;

    public async Task InitializeAsync()
    {
        _ = _blind.Services;
        _pc.RouteOutboundHttpThrough(_blind.Server.CreateHandler());
        await _pc.InitializeNodeAsync("PC", Password);
        _pcClient = _pc.CreateClient();
        (await _pcClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password }))
            .EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _pcClient.Dispose();
        _pc.Dispose();
        _blind.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>#3: seeded from its only hub, the blind node has that hub as a dialable, pinned peer.</summary>
    [Fact]
    public async Task ASeedFromTheOnlyHub_LeavesItDialable_AndPinned()
    {
        await AddBlindNodeAsync();

        var pcId = (await _pc.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        var row = await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(pcId);
        row!.ApiAddress.Should().Be(PcAddress, "the scheduler dials only rows with an address");
        var pins = _blind.Services.GetRequiredService<SpkiPinRegistry>();
        pins.Invalidate();
        pins.PinFor(new Uri(PcAddress + "/api/sync/events")).Should().Be(PcSpki);
    }

    /// <summary>
    /// #2: a full node nobody in the network confirms as superadmin (here: no full peer to ask) serves
    /// a replica whose producer row is not superadmin — so it cannot become the receiver's reseed
    /// authority (the authority rule itself: Reseed_ByAnOrdinaryPeer_IsRefused).
    /// </summary>
    [Fact]
    public async Task AReplicaFromAnUnconfirmedFullNode_DoesNotMakeItSuperadmin()
    {
        await AddBlindNodeAsync();
        var blindId = (await _blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        using var http = _pc.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer",
            _pc.Services.GetRequiredService<SyncTokenStore>().IssueToken(blindId, SyncProtocolVersion.Current));

        var resp = await http.GetAsync("/api/blind/replica");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var file = Path.Combine(_pc.DataPath, "replica-received.tar.gz");
        await File.WriteAllBytesAsync(file, await resp.Content.ReadAsByteArrayAsync());
        var package = await _pc.Services.GetRequiredService<SnapshotService>()
            .ExtractVerifiedAsync(file, Path.Combine(_pc.DataPath, "replica-extracted"));
        var manifest = BlindManifest.Parse(await File.ReadAllBytesAsync(Path.Combine(package.Directory, BlindManifest.FileName)));
        var producer = manifest.Whitelist.Single(p => p.NodeId == manifest.ProducerNodeId);
        producer.IsSuperadmin.Should().BeFalse("no full peer confirmed this node as superadmin");
        producer.ApiAddress.Should().Be(PcAddress);
        producer.TlsSpki.Should().Be(PcSpki);
    }

    /// <summary>
    /// Review L-merge round 2 #1: the PC seeds under the pair code with its own manifest row claiming
    /// superadmin (BlindNodeManager says so after its pre-flight) — but nothing the network signed says
    /// so. The blind node imports it as an ordinary peer, and its later reseed is refused.
    /// </summary>
    [Fact]
    public async Task AProducersOwnSuperadminClaim_GrantsNothing_AndItsReseedIsRefused()
    {
        await AddBlindNodeAsync();
        var pc = (await _pc.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;

        (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(pc.NodeId))!
            .IsSuperadmin.Should().BeFalse("a producer's claim about itself is not the network's word");
        var resp = await ReseedAsync(pc);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "only a peer the blind node already holds as superadmin may reseed");
    }

    /// <summary>A promotion the PC signed for itself is in its log, but no applier accepts it.</summary>
    [Fact]
    public async Task ASelfSignedPromotion_IsNoStanding()
    {
        var pc = (await _pc.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var self = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = pc.NodeId, LamportTs = 10, EventType = EventTypes.WhitelistUpdate,
            Payload = JsonSerializer.Serialize(new WhitelistUpdatePayload(pc.NodeId, null, null, IsSuperadmin: true)),
            ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow
        };
        using (var scope = _pc.Services.CreateScope())
        {
            self.Signature = scope.ServiceProvider.GetRequiredService<INodeAuthSigner>().SignChallenge(pc, EventSignature.BuildPayload(self));
            await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().AppendAsync(self);
        }

        await AddBlindNodeAsync();

        (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(pc.NodeId))!
            .IsSuperadmin.Should().BeFalse();
    }

    /// <summary>
    /// Review L-merge round 3 #1: the producer's only promotion is its signed admission — a root-signed
    /// whitelist_add with is_superadmin — and that is enough: the blind node holds it as superadmin, and
    /// its reseed is accepted.
    /// </summary>
    [Fact]
    public async Task ASignedAdmissionAsSuperadmin_LetsTheProducerReseed()
    {
        var pc = (await _pc.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var (rootId, rootKey) = await TrustRootOnPcAsync();
        await ApplyOnPcAsync(Signed(rootId, rootKey, 10, EventTypes.WhitelistAdd,
            new WhitelistAddPayload(pc.NodeId, "PC", Convert.ToBase64String(pc.Ed25519PublicKey), null, false, IsSuperadmin: true)));

        await AddBlindNodeAsync();

        (await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(pc.NodeId))!
            .IsSuperadmin.Should().BeTrue("the root's signed admission made it superadmin");
        (await ReseedAsync(pc)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Review L-merge round 3 #2: the producer was admitted as superadmin and later revoked, both signed
    /// by the root and both in its log. Its first seed carries the revoke with the promotion, so the
    /// blind node does not hold it as an active superadmin, and it cannot reseed.
    /// </summary>
    [Fact]
    public async Task ARevokedProducer_GetsNothingFromItsOldPromotion()
    {
        var pc = (await _pc.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var (rootId, rootKey) = await TrustRootOnPcAsync();
        await ApplyOnPcAsync(Signed(rootId, rootKey, 10, EventTypes.WhitelistAdd,
            new WhitelistAddPayload(pc.NodeId, "PC", Convert.ToBase64String(pc.Ed25519PublicKey), null, false, IsSuperadmin: true)));
        await ApplyOnPcAsync(Signed(rootId, rootKey, 20, EventTypes.WhitelistRevoke, new WhitelistRevokePayload(pc.NodeId)));

        await AddBlindNodeAsync();

        var row = await _blind.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(pc.NodeId);
        (row is null || !row.IsSuperadmin).Should().BeTrue("the revoke travelled with the promotion it ends");
    }

    private async Task<(Guid Id, byte[] Key)> TrustRootOnPcAsync()
    {
        var (pub, key) = Ed25519Signer.GenerateKeyPair();
        var id = Guid.NewGuid();
        await _pc.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = "Root", Ed25519PublicKey = pub, Status = "A", IsSuperadmin = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        return (id, key);
    }

    private static SyncEvent Signed<T>(Guid author, byte[] key, long lamport, string type, T payload)
    {
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = author, LamportTs = lamport, EventType = type,
            Payload = JsonSerializer.Serialize(payload), ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow
        };
        evt.Signature = Ed25519Signer.Sign(key, EventSignature.BuildPayload(evt));
        return evt;
    }

    private async Task ApplyOnPcAsync(SyncEvent evt)
    {
        using var scope = _pc.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(evt);
    }

    private async Task<HttpResponseMessage> ReseedAsync(NodeIdentity pc)
    {
        var blindId = (await _blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        BlindPackage package;
        using (var scope = _pc.Services.CreateScope())
            package = await scope.ServiceProvider.GetRequiredService<BlindPackageBuilder>()
                .BuildAsync(Guid.NewGuid(), includesUpTo: 0, producerIsSuperadmin: true);
        using var http = _blind.Server.CreateClient();
        string token;
        using (var scope = _pc.Services.CreateScope())
            token = await PeerAuthenticator.AuthenticateAsync(scope.ServiceProvider.GetRequiredService<INodeAuthSigner>(),
                http, http.BaseAddress!.ToString(), pc, blindId);
        var bytes = await File.ReadAllBytesAsync(package.FilePath);
        var req = new HttpRequestMessage(HttpMethod.Post,
            $"/api/blind/seed?seedId={package.Manifest.SeedId}&offset=0&total={bytes.Length}&sha256={package.Sha256}")
        { Content = new ByteArrayContent(bytes) };
        req.Headers.Authorization = new("Bearer", token);
        return await http.SendAsync(req);
    }

    private async Task AddBlindNodeAsync()
    {
        using var console = _blind.CreateClient();
        var body = await console.GetFromJsonAsync<JsonElement>("/api/blind/pair-code");
        var code = body.GetProperty("code").GetString()!;
        var add = await _pcClient.PostAsJsonAsync("/api/blind-nodes/", new { code });
        add.StatusCode.Should().Be(HttpStatusCode.OK, await add.Content.ReadAsStringAsync());
    }

    /// <summary>A PC that says in its configuration where it can be reached, and its pin there.</summary>
    private sealed class ReachablePcFactory : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("BMB_PUBLIC_ADDRESS", PcAddress);
            builder.UseSetting("BMB_PUBLIC_TLS_SPKI", PcSpki);
        }
    }
}
