using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BeeMemoryBank.Api.Startup;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Node;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Channels;
using BeeMemoryBank.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Direct sync between paired nodes on the LAN (docs/design/lan-peer-sync.md section 17): node A is a real Api on Kestrel behind the real
/// <see cref="LanListener"/> on loopback (TLS with a certificate nobody's CA issued), node B joins it with the code of "Connect a device" and then syncs
/// with it DIRECTLY on the same port, with the door closed: no "Devices on my network", no web UI. Then A moves: an announcement it made earlier
/// (<c>node_endpoints</c>) or an mDNS sighting heals B's way to it. And a peer of an older version (no <c>peer-endpoints-v1</c>) is never served an
/// announcement, whatever it asks for.
/// </summary>
[Collection(ProcessWideRateLimiterCollection.Name)]
public sealed class LanDirectSyncTests : IAsyncLifetime
{
    private const string Password = "lanDirectPassword123";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _dataDirA = Path.Combine(Path.GetTempPath(), "bmb_lansync_" + Guid.NewGuid().ToString("N"));
    private readonly List<BmbWebApplicationFactory> _joiners = new();
    private WebApplication _nodeA = null!;
    private X509Certificate2 _cert = null!;
    private LanListener _lan = null!;
    private LanJoinListener _door = null!;
    private Guid _nodeAId;
    private string _apiA = null!;

    public async Task InitializeAsync()
    {
        NodeEndpointsRules.AllowLoopbackForTests = true;
        Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", BmbWebApplicationFactory.InternalKeyForTests);
        Directory.CreateDirectory(_dataDirA);

        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseSetting("BeeMemoryBank:DataPath", _dataDirA);
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.AddBeeApiServices(_dataDirA);
        _nodeA = builder.Build();
        await _nodeA.RunBeeApiStartupTasksAsync(_dataDirA);
        _nodeA.UseBeeApiPipeline();
        _nodeA.MapBeeApiEndpoints();
        await _nodeA.StartAsync();
        _apiA = _nodeA.Urls.First(u => u.StartsWith("http://127.0.0.1"));

        using (var scope = _nodeA.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync("admin", "NodeA", Password);
            _nodeAId = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        }
        using (var http = ApiClientOfA())
            (await http.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        await StepANode.ActivateAsync(_nodeA.Services);

        _cert = CreateServerCertificate();
        // The node's one LAN listener: the sync surface on, on loopback (the "trusted network" of this test), on a port the system picks.
        _lan = new LanListener(_apiA, () => null, () => _cert, new FixedBindPlanner(IPAddress.Loopback), 0);
        await _lan.SetSyncAsync(true);
        _door = new LanJoinListener(_lan, () => _cert, TimeProvider.System);
    }

    public async Task DisposeAsync()
    {
        foreach (var j in _joiners) j.Dispose();
        await _door.DisposeAsync();
        await _lan.DisposeAsync();
        await _nodeA.StopAsync();
        await _nodeA.DisposeAsync();
        _cert.Dispose();
        try { Directory.Delete(_dataDirA, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task AfterTheJoin_TheJoinerSyncsDirectlyOnTheSamePort_WithTheDoorClosed_AndTheHostCanCallItBack()
    {
        var b = await JoinAsync();
        var lanAddress = $"https://127.0.0.1:{_lan.Port}";

        // The door is spent once the joiner has its snapshot: nothing of the join is served any more, the sync surface is.
        (await EventuallyAsync(() => _door.Current == null)).Should().BeTrue();
        using (var probe = new HttpClient(SpkiPin.CreatePinnedHandler(SpkiPin.Of(_cert))) { BaseAddress = new Uri(lanAddress) })
        {
            using var join = new HttpRequestMessage(HttpMethod.Post, "/api/join") { Content = JsonContent.Create(new { }) };
            (await probe.SendAsync(join)).StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
            (await probe.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.NotFound, "the web UI is never on the sync surface");
            (await probe.GetAsync("/api/sync/identity")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // A writes after the join; B gets it from A directly, at the address it stored at the join (the code's), on the same port.
        await CreateArticleOnAAsync("Written after the join");
        var check = await CheckAsync(b, _nodeAId);
        check.Answered.Should().BeTrue(check.Reason);
        check.Applied.Should().BeGreaterThan(0, "the article written on A after the join came over the LAN listener");

        using (var scope = b.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(_nodeAId);
            row!.ApiAddress.Should().Be(lanAddress);
        }

        // The host stored what the joiner said about its own endpoints at the join (it has a LAN listener only on a desktop node; here it has none,
        // so its row stays "calls us" until it announces something), and the joiner holds the host's CA key from the join's answer (a desktop host).
        using var hostScope = _nodeA.Services.CreateScope();
        var joinerRow = (await hostScope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync()).Single(r => r.DisplayName == "NodeB");
        joinerRow.ApiAddress.Should().BeNull("a joiner sends no address of its own");
    }

    [Fact]
    public async Task WhenTheHostMoves_ItsEarlierAnnouncement_HealsTheJoinersWayToIt()
    {
        var b = await JoinAsync();
        (await CheckAsync(b, _nodeAId)).Answered.Should().BeTrue();

        // A gets a second address (its DHCP lease changed, a second interface came up): it announces where it will listen.
        var newPort = PortBinding.PickFreePort();
        await PublishOnAAsync([new NodeEndpointEntry($"https://127.0.0.1:{newPort}", ChannelScopes.Lan)]);
        (await CheckAsync(b, _nodeAId)).Answered.Should().BeTrue("B pulls the announcement");
        using (var scope = b.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<PeerEndpointStore>().GetAsync(_nodeAId))!.Endpoints.Single().Url
                .Should().Be($"https://127.0.0.1:{newPort}");

        // A moves: its old address answers nothing now.
        await _lan.SetPortAsync(newPort);
        await CreateArticleOnAAsync("Written after the move");

        var check = await CheckAsync(b, _nodeAId);

        check.Answered.Should().BeTrue("the announced address is tried when the stored one fails: " + check.Reason);
        check.Applied.Should().BeGreaterThan(0);
        using var after = b.Services.CreateScope();
        var rows = await after.ServiceProvider.GetRequiredService<PeerChannelStore>().GetAsync(_nodeAId);
        rows.Should().Contain(r => r.Address == $"https://127.0.0.1:{newPort}" && r.LastOkAt != null, "it is the one tried first next time");
    }

    [Fact]
    public async Task WhenTheHostMoves_AnMdnsSightingAlone_HealsTheJoinersWayToIt()
    {
        var discovery = new PeerDiscoveryCache();
        var b = await JoinAsync(discovery);
        (await CheckAsync(b, _nodeAId)).Answered.Should().BeTrue();

        var newPort = PortBinding.PickFreePort();
        await _lan.SetPortAsync(newPort);
        (await CheckAsync(b, _nodeAId)).Answered.Should().BeFalse("nothing listens at the stored address and nothing else is known");

        // The host's mDNS record, as the desktop node's browser notes it.
        discovery.Note(_nodeAId, $"https://127.0.0.1:{newPort}", DateTime.UtcNow);
        await CreateArticleOnAAsync("Found again by mDNS");

        var check = await CheckAsync(b, _nodeAId);

        check.Answered.Should().BeTrue(check.Reason);
        check.Applied.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task APeerOfAnOlderVersion_IsNeverServedAnAnnouncement_ANewOneIs()
    {
        await CreateArticleOnAAsync("Before the announcement");
        await PublishOnAAsync([new NodeEndpointEntry("https://127.0.0.1:5999", ChannelScopes.Lan)]);
        await CreateArticleOnAAsync("After the announcement");
        var (peerId, peerKey) = await AddPeerToAAsync();

        var old = await PullAllAsync(peerId, peerKey, [BeeMemoryBank.Sync.Metadata.MetadataCapabilities.EnvelopeV1]);
        var updated = await PullAllAsync(peerId, peerKey, BeeMemoryBank.Sync.Metadata.MetadataCapabilities.Advertised.ToArray());

        old.Should().NotContain(EventTypes.NodeEndpoints, "a 2.7.5 node would refuse it while locked, and its pushes would stall behind it");
        old.Should().Contain(EventTypes.Sealed, "everything else is served as before, the events after the announcement too");
        old.Count.Should().Be(updated.Count - 1, "only the announcement is left out");
        updated.Should().Contain(EventTypes.NodeEndpoints);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private HttpClient ApiClientOfA()
    {
        var http = new HttpClient { BaseAddress = new Uri(_apiA) };
        http.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        http.DefaultRequestHeaders.Add("X-User-Role", "superadmin");
        return http;
    }

    private async Task<BmbWebApplicationFactory> JoinAsync(PeerDiscoveryCache? discovery = null)
    {
        var session = await _door.EnableAsync();
        var b = discovery is null ? new BmbWebApplicationFactory() : new DiscoveringFactory(discovery);
        _joiners.Add(b);
        BeeMemoryBank.Api.Middleware.RateLimitMiddleware.ResetForTests();
        using (var client = b.CreateClient())
        {
            var code = new JoinCode($"https://127.0.0.1:{_lan.Port}", session.Token, session.SpkiPin).ToString();
            var response = await client.PostAsJsonAsync("/api/init/join", new
            {
                adminUsername = "admin", displayName = "NodeB", remoteUrl = "", password = Password, joinCode = code, passwordHint = "A test hint",
            }, JsonOpts);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
        (await b.Services.GetRequiredService<SessionService>().UnlockAsync(Password)).Should().BeTrue();
        var scheduler = b.Services.GetRequiredService<SyncScheduler>();
        scheduler.CheckMinGap = TimeSpan.Zero;
        return b;
    }

    private static async Task<SyncScheduler.PeerCheck> CheckAsync(BmbWebApplicationFactory node, Guid peer)
    {
        for (var attempt = 0; ; attempt++)
        {
            var check = await node.Services.GetRequiredService<SyncScheduler>().CheckPeerNowAsync(peer);
            if (!check.Busy || attempt > 20) return check;
            await Task.Delay(250);
        }
    }

    private async Task CreateArticleOnAAsync(string title)
    {
        using var http = ApiClientOfA();
        (await http.PostAsJsonAsync("/api/articles", new { title, treePath = "/Lan", content = "over the LAN" })).EnsureSuccessStatusCode();
    }

    private async Task PublishOnAAsync(IReadOnlyList<NodeEndpointEntry> endpoints)
    {
        using var scope = _nodeA.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var publisher = new BeeMemoryBank.Api.Services.Lan.NodeEndpointsPublisher(
            sp.GetRequiredService<INodeIdentityRepository>(), sp.GetRequiredService<IEventLogRepository>(), sp.GetRequiredService<ILamportClock>(),
            sp.GetRequiredService<SessionService>(), sp.GetRequiredService<EventApplier>(), sp.GetRequiredService<ISyncTrigger>(),
            sp.GetRequiredService<BeeMemoryBank.Sync.Metadata.ISyncProtocolPolicy>());
        await publisher.PublishAsync(endpoints, SpkiPin.Of(_cert), null, _lan.Port, 1);
    }

    private async Task<(Guid, byte[])> AddPeerToAAsync()
    {
        var (pub, key) = Ed25519Signer.GenerateKeyPair();
        var id = Guid.NewGuid();
        using var scope = _nodeA.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = "Old laptop", Ed25519PublicKey = pub, Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return (id, key);
    }

    /// <summary>A peer that authenticates with <paramref name="capabilities"/> and pulls A's whole log: the event types it was given.</summary>
    private async Task<List<string>> PullAllAsync(Guid peer, byte[] key, string[] capabilities)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_apiA) };
        var challenge = await (await http.PostAsync("/api/sync/challenge", null)).Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var challengeB64 = challenge.GetProperty("challenge").GetString()!;
        var server = challenge.GetProperty("serverNodeId").GetGuid();
        var payload = "BMB-CHALLENGE-V2\0"u8.ToArray().Concat(server.ToByteArray()).Concat(Convert.FromBase64String(challengeB64)).ToArray();
        var auth = await http.PostAsJsonAsync("/api/sync/authenticate", new
        {
            nodeId = peer, challengeB64, signatureB64 = Convert.ToBase64String(Ed25519Signer.Sign(key, payload)),
            protocolVersion = BeeMemoryBank.Sync.Metadata.EventClasses.SealedProtocol, capabilities,
        }, JsonOpts);
        auth.EnsureSuccessStatusCode();
        var token = (await auth.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("token").GetString()!;
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/sync/events?afterSequence=0");
        req.Headers.Authorization = new("Bearer", token);
        var events = await (await http.SendAsync(req)).Content.ReadFromJsonAsync<List<JsonElement>>(JsonOpts);
        return events!.Select(e => e.GetProperty("eventType").GetString()!).ToList();
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        return condition();
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=lan-node", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    /// <summary>A joiner whose LAN discovery is the test's: what an mDNS browse would have noted.</summary>
    private sealed class DiscoveringFactory(PeerDiscoveryCache discovery) : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IPeerDiscovery>(discovery));
        }
    }
}
