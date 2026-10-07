using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// One shared world for the hub tests (ADR 0007): a full node — the "hub" — on real Kestrel with https, a second full node (the
/// administrator's PC) joined to it whose own checks trust a test CA's root through the same abstraction production uses, and
/// the certificates a test may present. Joining a node is the slow part, so it is done once; each test starts from
/// <see cref="ResetAsync"/>.
/// </summary>
public sealed class BlindHubWorld : IAsyncLifetime
{
    public const string Password = "blindHubPassword1";

    public TestPki Pki { get; } = new();
    public X509Certificate2 CaLeaf { get; private set; } = null!;
    public X509Certificate2 SelfSigned { get; private set; } = null!;
    internal BlindHubHost Hub { get; private set; } = null!;
    internal AdminNodeFactory Pc { get; private set; } = null!;
    public HttpClient PcClient { get; private set; } = null!;
    public Guid HubId { get; private set; }
    public NodeIdentity HubIdentity { get; private set; } = null!;
    public NodeIdentity PcIdentity { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        CaLeaf = Pki.IssueServer("localhost");
        SelfSigned = TestPki.SelfSigned("localhost");
        Hub = await BlindHubHost.StartAsync(CaLeaf, "Hub", Password);

        Pc = new AdminNodeFactory(new TlsTrustAnchors(Pki.Anchors));
        PcClient = Pc.CreateClient();
        await Pc.JoinNodeAsync(Hub.Admin, "PC", Password);
        (await PcClient.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();

        HubIdentity = (await Hub.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        PcIdentity = (await Pc.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        HubId = HubIdentity.NodeId;
    }

    public async Task DisposeAsync()
    {
        PcClient.Dispose();
        Pc.Dispose();
        await Hub.DisposeAsync();
        Pki.Dispose();
    }

    /// <summary>The hub's row on the PC as it was after joining: no address, no trust, the hub's real key.</summary>
    public async Task ResetAsync()
    {
        Hub.Certificate = CaLeaf;
        using var scope = Pc.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var row = (await repo.GetByNodeIdAsync(HubId))!;
        row.ApiAddress = null;
        row.TlsSpki = null;
        row.TlsTrust = null;
        row.Ed25519PublicKey = HubIdentity.Ed25519PublicKey;
        await repo.UpdateAsync(row);
        // Rows an earlier test added: they keep no address, so none of them can be "already using" one.
        using (var conn = Pc.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_whitelist SET api_address = NULL WHERE node_id <> @hub COLLATE NOCASE", new { hub = HubId.ToString() });
        Pc.Services.GetRequiredService<SpkiPinRegistry>().Invalidate();
    }

    public async Task<WhitelistEntry> HubRowOnPcAsync()
    {
        using var scope = Pc.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(HubId))!;
    }

    public async Task<long> PcHeadAsync() => await Pc.Services.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();

    /// <summary>The whitelist_update events about the hub the PC logged after <paramref name="after"/>.</summary>
    public async Task<List<JsonElement>> PcHubUpdatesAfterAsync(long after)
    {
        var events = await Pc.Services.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(after);
        return events.Where(e => e.EventType == EventTypes.WhitelistUpdate)
            .Select(e => JsonDocument.Parse(e.Payload).RootElement.Clone())
            .Where(p => p.GetProperty("node_id").GetGuid() == HubId)
            .ToList();
    }

    public Task<HttpResponseMessage> SetHubAsync(string trust, string? address = null, string? password = null, string? expectedPin = null,
        HttpClient? client = null) =>
        (client ?? PcClient).PutAsJsonAsync($"/api/whitelist/{HubId}/hub", new
        {
            trust, address = address ?? Hub.Origin, password = password ?? Password, expectedPin
        });

    /// <summary>The administrator's PC: its checks of a hub's certificate trust the test CA, as a real node trusts a real one.</summary>
    internal sealed class AdminNodeFactory(TlsTrustAnchors anchors) : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(anchors);
                // The hub of these tests is on the loopback interface, which the shipped address rule refuses (see
                // HubProbeAddressGuardTests, which runs the shipped rule); everything else about the probe is production's.
                services.AddSingleton(new HubTrustProbe(anchors, HubProbeAddressGuardTests.AnyAddress));
            });
        }
    }
}

[CollectionDefinition(Name)]
public sealed class BlindHubCollection : ICollectionFixture<BlindHubWorld>
{
    public const string Name = "Blind hub";
}

/// <summary>
/// "Let blind copies call this node" (ADR 0007), from Admin: the checks before the row is saved, the whitelist event, who may do
/// it — and that a blind copy, told by a hub's call code in either trust mode, downloads the package and syncs over real TLS.
/// </summary>
[Collection(BlindHubCollection.Name)]
public sealed class BlindHubTests(BlindHubWorld world)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static async Task<string> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("error").GetString()!;

    private async Task<List<JsonElement>> ListenersAsync() =>
        (await world.PcClient.GetFromJsonAsync<List<JsonElement>>("/api/blind-nodes/android/listeners", Web))!;

    // ── set it up ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task NormalCertificate_IsVerifiedThroughTheChain_AndPublishedWithoutAPin()
    {
        await world.ResetAsync();
        var head = await world.PcHeadAsync();

        var resp = await world.SetHubAsync("public-ca");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        body.GetProperty("pin").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("entry").GetProperty("tlsTrust").GetString().Should().Be("public-ca");
        body.GetProperty("notice").GetString().Should().Contain("public authority");

        var row = await world.HubRowOnPcAsync();
        (row.TlsTrust, row.TlsSpki, row.ApiAddress).Should().Be(("public-ca", null, world.Hub.Origin));

        var updates = await world.PcHubUpdatesAfterAsync(head);
        var update = updates.Should().ContainSingle().Which;
        update.GetProperty("tls_trust").GetString().Should().Be("public-ca");
        update.GetProperty("tls_spki").GetString().Should().BeEmpty("an older node takes the empty pin as 'no pin'");
        update.GetProperty("api_address").GetString().Should().Be(world.Hub.Origin);

        var listed = (await ListenersAsync()).Should().ContainSingle(l => l.GetProperty("nodeId").GetGuid() == world.HubId).Which;
        listed.GetProperty("trust").GetString().Should().Be("public-ca");
        listed.GetProperty("isBlind").GetBoolean().Should().BeFalse();

        await AssertAuditAsync("hub_enabled");
    }

    [Fact]
    public async Task PinnedCertificate_RecordsTheKeyTheNodePresents_AndSaysItWasTakenOnTrust()
    {
        await world.ResetAsync();
        world.Hub.Certificate = world.SelfSigned;

        var resp = await world.SetHubAsync("pin");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        var pin = SpkiPin.Of(world.SelfSigned);
        body.GetProperty("pin").GetString().Should().Be(pin);
        body.GetProperty("publiclyTrusted").GetBoolean().Should().BeFalse("a self-signed certificate vouches for nobody");
        body.GetProperty("notice").GetString().Should().Contain("nothing else vouched");

        var row = await world.HubRowOnPcAsync();
        (row.TlsTrust, row.TlsSpki, row.ApiAddress).Should().Be(("pin", pin, world.Hub.Origin));
        (await ListenersAsync()).Should().ContainSingle(l => l.GetProperty("nodeId").GetGuid() == world.HubId)
            .Which.GetProperty("trust").GetString().Should().Be("pin");
    }

    [Fact]
    public async Task PinnedCertificate_ThatAPublicAuthorityAlsoVouchesFor_WarnsAboutRenewal()
    {
        await world.ResetAsync();

        var resp = await world.SetHubAsync("pin");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        body.GetProperty("publiclyTrusted").GetBoolean().Should().BeTrue();
        body.GetProperty("notice").GetString().Should().Contain("renewed with a new key");
    }

    [Fact]
    public async Task PinnedCertificate_WithTheExpectedPin_IsAccepted_AndWithAnotherOne_IsRefused()
    {
        await world.ResetAsync();
        world.Hub.Certificate = world.SelfSigned;
        var head = await world.PcHeadAsync();
        var wrong = SpkiPin.Of(world.CaLeaf);

        var refused = await world.SetHubAsync("pin", expectedPin: wrong);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(refused)).Should().Contain("another key than the pin you gave");
        (await world.HubRowOnPcAsync()).TlsTrust.Should().BeNull("nothing is saved on a refusal");
        (await world.PcHubUpdatesAfterAsync(head)).Should().BeEmpty();

        var malformed = await world.SetHubAsync("pin", expectedPin: "not a pin");
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var accepted = await world.SetHubAsync("pin", expectedPin: SpkiPin.Of(world.SelfSigned));
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());
        (await accepted.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("notice").GetString().Should().Contain("matches the pin you gave");
    }

    [Fact]
    public async Task ASelfSignedCertificate_CannotBeSetAsNormal()
    {
        await world.ResetAsync();
        world.Hub.Certificate = world.SelfSigned;
        var head = await world.PcHeadAsync();

        var resp = await world.SetHubAsync("public-ca");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(resp)).Should().Contain("not trusted").And.Contain("Pinned certificate");
        await AssertNothingSavedAsync(head);
    }

    [Fact]
    public async Task AnExpiredCertificate_CannotBeSetAsNormal()
    {
        await world.ResetAsync();
        world.Hub.Certificate = world.Pki.IssueServer("localhost", DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow.AddDays(-1));
        var head = await world.PcHeadAsync();

        var resp = await world.SetHubAsync("public-ca");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(resp)).Should().Contain("not trusted").And.Contain("expired");
        await AssertNothingSavedAsync(head);
    }

    [Fact]
    public async Task ACertificateForAnotherName_CannotBeSetAsNormal()
    {
        await world.ResetAsync();
        world.Hub.Certificate = world.Pki.IssueServer("another-name.test");
        var head = await world.PcHeadAsync();

        var resp = await world.SetHubAsync("public-ca");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(resp)).Should().Contain("another name");
        await AssertNothingSavedAsync(head);
    }

    [Fact]
    public async Task ACertificateFromAnAuthorityNobodyTrusts_CannotBeSetAsNormal()
    {
        await world.ResetAsync();
        using var stranger = new TestPki("CN=Stranger root");
        world.Hub.Certificate = stranger.IssueServer("localhost");
        var head = await world.PcHeadAsync();

        var resp = await world.SetHubAsync("public-ca");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(resp)).Should().Contain("not trusted");
        await AssertNothingSavedAsync(head);
    }

    [Fact]
    public async Task AnotherNodeAtTheAddress_IsRefused()
    {
        await world.ResetAsync();
        // The row names a node that is not the one answering at the address.
        var imposter = Guid.NewGuid();
        using (var scope = world.Pc.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = imposter, DisplayName = "Imposter", Ed25519PublicKey = world.HubIdentity.Ed25519PublicKey,
                Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        var head = await world.PcHeadAsync();

        var resp = await world.PcClient.PutAsJsonAsync($"/api/whitelist/{imposter}/hub",
            new { trust = "public-ca", address = world.Hub.Origin, password = BlindHubWorld.Password });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(resp)).Should().Contain("different node");
        (await world.Pc.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(imposter))!.TlsTrust.Should().BeNull();
        (await world.PcHubUpdatesAfterAsync(head)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheRightIdWithAnotherKey_IsRefused()
    {
        await world.ResetAsync();
        using (var scope = world.Pc.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            var row = (await repo.GetByNodeIdAsync(world.HubId))!;
            row.Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey;
            await repo.UpdateAsync(row);
        }
        var head = await world.PcHeadAsync();

        var resp = await world.SetHubAsync("public-ca");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(resp)).Should().Contain("another key");
        await AssertNothingSavedAsync(head);
    }

    [Theory]
    [InlineData("http://localhost:1")]
    [InlineData("https://localhost:1/api")]
    [InlineData("https://user@localhost:1")]
    public async Task OnlyAnHttpsOrigin_CanBeSet(string address)
    {
        await world.ResetAsync();
        var head = await world.PcHeadAsync();

        var resp = await world.SetHubAsync("public-ca", address);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(resp)).Should().Contain("https");
        await AssertNothingSavedAsync(head);
    }

    [Fact]
    public async Task AnUnreachableAddress_IsSaidSo()
    {
        await world.ResetAsync();
        var head = await world.PcHeadAsync();

        var resp = await world.SetHubAsync("public-ca", "https://localhost:1");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(resp)).Should().Contain("localhost:1 refused the connection");
        await AssertNothingSavedAsync(head);
    }

    [Fact]
    public async Task AnAddressAnotherNodeAlreadyUses_IsRefused()
    {
        await world.ResetAsync();
        var other = Guid.NewGuid();
        using (var scope = world.Pc.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = other, DisplayName = "Other", Ed25519PublicKey = new byte[32], Status = "A", ApiAddress = world.Hub.Origin,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });

        var resp = await world.SetHubAsync("public-ca");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorOf(resp)).Should().Contain("already used");
    }

    [Fact]
    public async Task ABlindNode_AndTheNodesOwnId_AreNotSetUpHere()
    {
        await world.ResetAsync();
        var blind = BlindNodeId.NewId();
        using (var scope = world.Pc.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = blind, DisplayName = "Blind", Ed25519PublicKey = new byte[32], Status = "A",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });

        (await world.PcClient.PutAsJsonAsync($"/api/whitelist/{blind}/hub", new { trust = "public-ca", address = world.Hub.Origin, password = BlindHubWorld.Password }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await world.PcClient.PutAsJsonAsync($"/api/whitelist/{world.PcIdentity.NodeId}/hub", new { trust = "public-ca", address = world.Hub.Origin, password = BlindHubWorld.Password }))
            .StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
        (await world.PcClient.PutAsJsonAsync($"/api/whitelist/{Guid.NewGuid()}/hub", new { trust = "public-ca", address = world.Hub.Origin, password = BlindHubWorld.Password }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnUnknownMode_IsRefused()
    {
        await world.ResetAsync();

        (await world.SetHubAsync("lets-encrypt")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await world.SetHubAsync("")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── who may ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AWrongMasterPassword_IsRefused_AndSavesNothing()
    {
        await world.ResetAsync();
        var head = await world.PcHeadAsync();

        var resp = await world.SetHubAsync("public-ca", password: "not the password");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorOf(resp)).Should().Contain("Invalid master password");
        await AssertNothingSavedAsync(head);
        (await world.SetHubAsync("public-ca", password: "")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnOrdinaryUser_IsRefused()
    {
        await world.ResetAsync();
        using var user = world.Pc.Server.CreateClient();
        user.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        user.DefaultRequestHeaders.Add("X-User-Role", "user");

        var resp = await world.SetHubAsync("public-ca", client: user);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await world.HubRowOnPcAsync()).TlsTrust.Should().BeNull();
    }

    [Fact]
    public async Task AnAgent_EvenOneOwnedByASuperadmin_IsRefused()
    {
        await world.ResetAsync();
        var apiKey = await CreateAgentKeyOnPcAsync();
        using var agent = world.Pc.Server.CreateClient();
        agent.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var resp = await world.SetHubAsync("public-ca", client: agent);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorOf(resp)).Should().Contain("Agents cannot");
        (await world.HubRowOnPcAsync()).TlsTrust.Should().BeNull();
    }

    [Fact]
    public async Task ALockedNode_IsRefused()
    {
        await world.ResetAsync();
        var session = world.Pc.Services.GetRequiredService<SessionService>();
        session.Lock();
        try
        {
            var resp = await world.SetHubAsync("public-ca");

            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ErrorOf(resp)).Should().Contain("locked");
        }
        finally
        {
            (await world.PcClient.PostAsJsonAsync("/api/session/unlock", new { password = BlindHubWorld.Password })).EnsureSuccessStatusCode();
        }
    }

    // ── turn it off ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ASuperadmin_CanTurnItOffAgain()
    {
        await world.ResetAsync();
        (await world.SetHubAsync("public-ca")).EnsureSuccessStatusCode();
        var head = await world.PcHeadAsync();

        var resp = await world.SetHubAsync("off");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var row = await world.HubRowOnPcAsync();
        (row.TlsTrust, row.TlsSpki).Should().Be((null, null));
        row.ApiAddress.Should().Be(world.Hub.Origin, "the address stays: it is how the nodes reach each other");
        var update = (await world.PcHubUpdatesAfterAsync(head)).Should().ContainSingle().Which;
        update.GetProperty("tls_trust").GetString().Should().Be("none");
        update.GetProperty("tls_spki").GetString().Should().BeEmpty();
        (await ListenersAsync()).Should().NotContain(l => l.GetProperty("nodeId").GetGuid() == world.HubId);
        await AssertAuditAsync("hub_disabled");

        var again = await world.SetHubAsync("off");
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await world.PcHubUpdatesAfterAsync(head)).Should().HaveCount(1, "turning off what is off publishes nothing");
    }

    [Fact]
    public async Task APinnedHub_CanBeMovedToNormal_AndTheOldPinIsGone()
    {
        await world.ResetAsync();
        world.Hub.Certificate = world.SelfSigned;
        (await world.SetHubAsync("pin")).EnsureSuccessStatusCode();
        world.Hub.Certificate = world.CaLeaf;

        var resp = await world.SetHubAsync("public-ca");

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var row = await world.HubRowOnPcAsync();
        (row.TlsTrust, row.TlsSpki).Should().Be(("public-ca", null));
    }

    // ── the event reaches another node ────────────────────────────────────────

    [Fact]
    public async Task TheWhitelistEvent_IsAppliedByAnotherNode_AsAnyPeerWouldApplyIt()
    {
        await world.ResetAsync();
        using var other = new BmbWebApplicationFactory();
        await other.JoinNodeAsync(world.Hub.Admin, "Other", BlindHubWorld.Password);
        using (var scope = other.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = world.PcIdentity.NodeId, DisplayName = "PC", Ed25519PublicKey = world.PcIdentity.Ed25519PublicKey, Status = "A",
                IsSuperadmin = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        var head = await world.PcHeadAsync();
        (await world.SetHubAsync("public-ca")).EnsureSuccessStatusCode();
        var events = (await world.Pc.Services.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(head))
            .Where(e => e.EventType == EventTypes.WhitelistUpdate).ToList();

        using (var scope = other.Services.CreateScope())
        {
            var applier = scope.ServiceProvider.GetRequiredService<EventApplier>();
            foreach (var evt in events) (await applier.ApplyAsync(evt)).Should().Be(EventApplyResult.Applied);
            var row = (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(world.HubId))!;
            (row.TlsTrust, row.TlsSpki, row.ApiAddress).Should().Be(("public-ca", null, world.Hub.Origin));
        }

        // ... and the pin mode, then off
        world.Hub.Certificate = world.SelfSigned;
        var head2 = await world.PcHeadAsync();
        (await world.SetHubAsync("pin")).EnsureSuccessStatusCode();
        (await world.SetHubAsync("off")).EnsureSuccessStatusCode();
        var later = (await world.Pc.Services.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(head2))
            .Where(e => e.EventType == EventTypes.WhitelistUpdate).ToList();
        using var scope2 = other.Services.CreateScope();
        var applier2 = scope2.ServiceProvider.GetRequiredService<EventApplier>();
        var repo2 = scope2.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        (await applier2.ApplyAsync(later[0])).Should().Be(EventApplyResult.Applied);
        (await repo2.GetByNodeIdAsync(world.HubId))!.Should().Match<WhitelistEntry>(r => r.TlsTrust == "pin" && r.TlsSpki == SpkiPin.Of(world.SelfSigned));
        (await applier2.ApplyAsync(later[1])).Should().Be(EventApplyResult.Applied);
        (await repo2.GetByNodeIdAsync(world.HubId))!.Should().Match<WhitelistEntry>(r => r.TlsTrust == null && r.TlsSpki == null);
    }

    // ── a blind copy against the hub ──────────────────────────────────────────

    /// <summary>
    /// The whole path, over real TLS, in each mode: the PC sets the hub up, pairs a blind copy to it (the call code carries the
    /// mode), one copy downloads and installs the signed package and another syncs from the hub — with the app's own HTTP
    /// provider, which trusts the hub as the code says.
    /// </summary>
    [Theory]
    [InlineData("public-ca")]
    [InlineData("pin")]
    public async Task ABlindCopy_PairsAgainstTheHub_DownloadsThePackage_AndSyncs(string mode)
    {
        await world.ResetAsync();
        world.Hub.Certificate = mode == "pin" ? world.SelfSigned : world.CaLeaf;
        (await world.SetHubAsync(mode)).EnsureSuccessStatusCode();
        var anchors = new TlsTrustAnchors(world.Pki.Anchors);
        using var provider = new BlindHttpClientProvider(new BlindPhoneState(new MemoryStore()), null, null, anchors);

        // The copy that downloads the package.
        var phoneA = NewPhone("phone A");
        var callA = await PairAsync(phoneA);
        callA.Trust.Should().Be(mode);
        callA.Address.Should().Be(world.Hub.Origin);
        (mode == "pin" ? callA.SpkiPin : "").Should().Be(mode == "pin" ? SpkiPin.Of(world.SelfSigned) : "");
        callA.NodeId.Should().Be(world.HubId);
        callA.PublicKey.Should().Equal(world.HubIdentity.Ed25519PublicKey);
        await TheHubKnowsAsync(phoneA);
        var replica = await CreateReplicaClientAsync(phoneA);

        using (var http = provider.GetClient(callA))
            await replica.Client.FetchAndInstallAsync(http, callA, replica.WorkDirectory, progress: null, CancellationToken.None);

        (await new NodeIdentityRepository(replica.Factory).GetAsync())!.NodeId.Should().Be(phoneA.NodeId);
        (await new SyncPositionRepository(replica.Factory).GetAsync(world.HubId)).Should().NotBeNull("the next pull starts at the package's checkpoint");
        var producer = (await new WhitelistRepository(replica.Factory).GetByNodeIdAsync(world.HubId))!;
        producer.Ed25519PublicKey.Should().Equal(world.HubIdentity.Ed25519PublicKey, "the package says who produced it, and it was signed by that key");

        // The copy that syncs: an event the hub authored arrives over the same kind of connection.
        using var syncing = new BlindNodeFactory();
        _ = syncing.Services;
        var phoneB = await PhoneOfAsync(syncing);
        var callB = await PairAsync(phoneB);
        await TheHubKnowsAsync(phoneB);
        await syncing.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = world.HubId, DisplayName = "Hub", Ed25519PublicKey = world.HubIdentity.Ed25519PublicKey, Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        // Start the pull where the hub's log is now, so what arrives is exactly what the hub does next.
        long hubHead;
        using (var hubScope = world.Hub.Services.CreateScope())
            hubHead = await hubScope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        await syncing.Services.GetRequiredService<ISyncPositionRepository>().UpsertAsync(new SyncPosition
        {
            RemoteNodeId = world.HubId, LastSequenceNum = hubHead, UpdatedAt = DateTime.UtcNow
        });
        var folder = "/FromTheHub" + Guid.NewGuid().ToString("N")[..6];
        (await world.Hub.Admin.PostAsJsonAsync("/api/folders", new { path = folder })).EnsureSuccessStatusCode();
        var pull = new BlindPhonePullClient(syncing.Services.GetRequiredService<IServiceScopeFactory>(),
            syncing.Services.GetRequiredService<INodeAuthSigner>(), NullLogger<BlindPhonePullClient>.Instance);

        using (var http = provider.GetClient(callB))
            await pull.SyncOnceAsync(http, callB, CancellationToken.None);

        (await syncing.Services.GetRequiredService<ISyncPositionRepository>().GetAsync(world.HubId))!.LastSequenceNum.Should().BeGreaterThan(0);
        using var conn = syncing.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_folder WHERE path = @folder", new { folder }))
            .Should().Be(1, "the folder the hub created came through the call");
    }

    [Fact]
    public async Task ABlindCopyToldToTrustACa_RefusesAHubThatPresentsASelfSignedCertificate()
    {
        await world.ResetAsync();
        (await world.SetHubAsync("public-ca")).EnsureSuccessStatusCode();
        var phone = NewPhone("phone");
        var call = await PairAsync(phone);
        await TheHubKnowsAsync(phone);
        var replica = await CreateReplicaClientAsync(phone);
        world.Hub.Certificate = world.SelfSigned;
        using var provider = new BlindHttpClientProvider(new BlindPhoneState(new MemoryStore()), null, null, new TlsTrustAnchors(world.Pki.Anchors));

        try
        {
            using var http = provider.GetClient(call);
            var act = () => replica.Client.FetchAndInstallAsync(http, call, replica.WorkDirectory, progress: null, CancellationToken.None);

            await act.Should().ThrowAsync<HttpRequestException>("the certificate is not one a CA vouches for");
        }
        finally
        {
            world.Hub.Certificate = world.CaLeaf;
        }
    }

    [Fact]
    public async Task APinnedCall_ToAHubWhoseKeyChanged_IsRefused()
    {
        await world.ResetAsync();
        world.Hub.Certificate = world.SelfSigned;
        (await world.SetHubAsync("pin")).EnsureSuccessStatusCode();
        var phone = NewPhone("phone");
        var call = await PairAsync(phone);
        await TheHubKnowsAsync(phone);
        var replica = await CreateReplicaClientAsync(phone);
        world.Hub.Certificate = world.CaLeaf; // another key, and a certificate every CA-trusting client would take
        using var provider = new BlindHttpClientProvider(new BlindPhoneState(new MemoryStore()), null, null, new TlsTrustAnchors(world.Pki.Anchors));

        try
        {
            using var http = provider.GetClient(call);
            var act = () => replica.Client.FetchAndInstallAsync(http, call, replica.WorkDirectory, progress: null, CancellationToken.None);

            await act.Should().ThrowAsync<HttpRequestException>("a pinned node is trusted by its key and nothing a CA says");
        }
        finally
        {
            world.Hub.Certificate = world.CaLeaf;
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private sealed record Phone(Guid NodeId, byte[] PublicKey, byte[] Seed, string Name);

    private static Phone NewPhone(string name)
    {
        var (publicKey, seed) = Ed25519Signer.GenerateKeyPair();
        return new Phone(BlindNodeId.NewId(), publicKey, seed, name);
    }

    private static async Task<Phone> PhoneOfAsync(BlindNodeFactory host)
    {
        var identity = (await host.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        return new Phone(identity.NodeId, identity.Ed25519PublicKey, [], "syncing phone");
    }

    private readonly Dictionary<Guid, byte[]> _secrets = [];

    /// <summary>The PC pairs the phone to call the hub: the phone's code goes in, the "where to call" code comes out.</summary>
    private async Task<BlindCallCode> PairAsync(Phone phone)
    {
        var code = new BlindPhoneCode(phone.NodeId, phone.PublicKey, BlindPairingSecret.New(), RandomNumberGenerator.GetBytes(32), phone.Name);
        var resp = await world.PcClient.PostAsJsonAsync("/api/blind-nodes/android/", new { code = code.ToString(), listenerId = world.HubId });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var paired = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        BlindCallCode.TryParse(paired.GetProperty("callCode").GetString(), out var call).Should().BeTrue();
        call!.IsAuthenticBy(code.Secret).Should().BeTrue("the phone checks the code came from whoever read its own");
        _secrets[phone.NodeId] = code.Secret;
        return call;
    }

    /// <summary>What the mesh would have told the hub by now: the phone is a peer there (a blind peer, never a superadmin).</summary>
    private async Task TheHubKnowsAsync(Phone phone)
    {
        var repo = world.Hub.Services.GetRequiredService<IWhitelistRepository>();
        if (await repo.GetByNodeIdAsync(phone.NodeId) is not null) return;
        try
        {
            await repo.CreateAsync(new WhitelistEntry
            {
                NodeId = phone.NodeId, DisplayName = phone.Name, Ed25519PublicKey = phone.PublicKey, Status = "A",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // The PC's push got there first: the phone's whitelist_add reached the hub through the real connection.
        }
    }

    private sealed record Replica(DbConnectionFactory Factory, BlindPhoneReplicaClient Client, string WorkDirectory);

    /// <summary>A phone-shaped receiver, as the replica tests make one: a database, a v2 identity with its key outside it.</summary>
    private static async Task<Replica> CreateReplicaClientAsync(Phone phone)
    {
        var data = Path.Combine(Path.GetTempPath(), "bmb_hub_phone_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var db = Path.Combine(data, "beememorybank.db");
        var factory = new DbConnectionFactory(db);
        await new MigrationRunner(factory).RunMigrationsAsync();
        await new NodeIdentityRepository(factory).CreateAsync(new NodeIdentity
        {
            NodeId = phone.NodeId, DisplayName = phone.Name, Ed25519PublicKey = phone.PublicKey, Ed25519PrivateKey = [],
            Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion, CanGenerateEmbeddings = false, CreatedAt = DateTime.UtcNow
        });
        var client = new BlindPhoneReplicaClient(factory, new NodeIdentityRepository(factory), new SeedSigner(phone.Seed), data, db,
            NullLogger<BlindPhoneReplicaClient>.Instance);
        return new Replica(factory, client, Path.Combine(data, "replica"));
    }

    private sealed class SeedSigner(byte[] seed) : INodeAuthSigner
    {
        public byte[] SignChallenge(NodeIdentity identity, byte[] payload) => Ed25519Signer.Sign(seed, payload);
    }

    private sealed class MemoryStore : IBlindPhoneStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string? value)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
        }
    }

    private async Task AssertNothingSavedAsync(long head)
    {
        var row = await world.HubRowOnPcAsync();
        (row.TlsTrust, row.TlsSpki, row.ApiAddress).Should().Be((null, null, null), "a refusal changes nothing");
        (await world.PcHubUpdatesAfterAsync(head)).Should().BeEmpty("and publishes nothing");
    }

    private async Task AssertAuditAsync(string action)
    {
        using var conn = world.Pc.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_audit_log WHERE entity_id = @id AND action = @action",
            new { id = world.HubId.ToString(), action })).Should().BeGreaterThan(0, "an audit entry says who changed it");
    }

    /// <summary>An agent owned by the PC's superadmin: it passes the role check, which is why the endpoint also refuses agents.</summary>
    private async Task<string> CreateAgentKeyOnPcAsync()
    {
        int userId;
        using (var users = world.Pc.Services.CreateScope())
            userId = (await users.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("PC"))!.Id;
        var dek = world.Pc.Services.GetRequiredService<SessionService>().GetMasterDek();
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var (ciphertext, iv) = AgentKeyHelper.EncryptDek(apiKey, dek);
        Array.Clear(dek);
        using var scope = world.Pc.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAgentRepository>().CreateAsync(new Agent
        {
            Name = "Hub Test Agent", KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey), KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
            EncryptedDek = ciphertext, DekIV = iv, Status = "A", CreatedAt = DateTime.UtcNow, OwnerUserId = userId
        });
        return apiKey;
    }
}
