using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Step A (docs/design/step-a-spec.md 5, 16.2 items 1 and 7) on two real nodes in one process: the readiness report sees a
/// peer that advertised nothing (a 2.5 node) and refuses the switch; once every peer showed the capability the switch
/// creates the key and the event, the node declares protocol 4 and seals what it writes, a 2.6 peer that has not applied the
/// activation yet still syncs and switches as it pulls it, a 2.5 peer gets 426. On the wire: the canaries travel in clear
/// before the activation (the scanner's control) and nowhere after it, in pushes and in pulls.
/// </summary>
public class MetadataActivationTests : IAsyncLifetime
{
    private const string MasterPassword = "metadataActivation1";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private BmbWebApplicationFactory _a = null!;
    private BmbWebApplicationFactory _b = null!;
    private HttpClient _clientA = null!;
    private HttpClient _clientB = null!;

    public async Task InitializeAsync()
    {
        _a = new BmbWebApplicationFactory();
        _b = new BmbWebApplicationFactory();
        await _a.InitializeNodeAsync("NodeA", MasterPassword);
        _clientA = _a.CreateClient();
        await UnlockAsync(_clientA);
        await _b.JoinNodeAsync(_clientA, "NodeB", MasterPassword);
        _clientB = _b.CreateClient();
        await UnlockAsync(_clientB);
    }

    public Task DisposeAsync()
    {
        _clientA.Dispose();
        _clientB.Dispose();
        _a.Dispose();
        _b.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Readiness_SeesAPeerThatAdvertisesNothing_AsNotCapable_AndTheSwitchIsRefused()
    {
        // B authenticates the way a 2.5 build does: protocol 3, no capabilities.
        (await AuthenticateAs25Async(_b, _clientA)).StatusCode.Should().Be(HttpStatusCode.OK, "before activation a 2.5 peer still syncs");

        var report = await ReadinessAsync(_clientA);
        report.GetProperty("ready").GetBoolean().Should().BeFalse();
        var peer = report.GetProperty("peers").EnumerateArray().Single();
        peer.GetProperty("display_name").GetString().Should().Be("NodeB");
        peer.GetProperty("status").GetString().Should().Be("not_capable");
        report.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("code").GetString())
            .Should().Contain("peer_not_ready");

        (await ActivateAsync(_clientA, "wrong password")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var refused = await ActivateAsync(_clientA, MasterPassword);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("peer_not_ready");
        (await CountAsync(_a, "tbl_metadata_activation")).Should().Be(0);
        (await IdentityAsync(_clientA)).GetProperty("protocolVersion").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task TheSwitch_CreatesTheKeyAndTheEvent_A26PeerCatchesUp_AndA25PeerIsCutOff()
    {
        // B syncs as the 2.6 build it is: A sees the capability.
        await SyncAsync(_b, _clientA, _a);
        var report = await ReadinessAsync(_clientA);
        report.GetProperty("peers").EnumerateArray().Single().GetProperty("status").GetString().Should().Be("ready");
        report.GetProperty("ready").GetBoolean().Should().BeTrue(report.ToString());

        var resp = await ActivateAsync(_clientA, MasterPassword);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        body.GetProperty("generation").GetInt32().Should().Be(1);
        var keyId = body.GetProperty("key_id").GetString();

        (await CountAsync(_a, "tbl_metadata_key")).Should().Be(1);
        (await CountAsync(_a, "tbl_metadata_activation")).Should().Be(1);
        var identity = await IdentityAsync(_clientA);
        identity.GetProperty("protocolVersion").GetInt32().Should().Be(4);
        identity.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).Should().Contain(MetadataCapabilities.EnvelopeV1);
        using (var conn = Db(_a))
        {
            var control = (await conn.QueryAsync<(string Type, long Protocol)>(
                "SELECT event_type, protocol_version FROM tbl_event WHERE event_type IN ('metadata_key_set', 'metadata_activation') ORDER BY sequence_num")).ToList();
            control.Select(c => c.Type).Should().Equal("metadata_key_set", "metadata_activation");
            control.Should().OnlyContain(c => c.Protocol == 3, "the activation and its key are stamped before the switch");
        }

        // A writes sealed from now on.
        var title = MetadataCanaries.Ascii("Title");
        var created = await CreateArticleAsync(_clientA, title, "/Activated");
        using (var conn = Db(_a))
        {
            var row = await conn.QuerySingleAsync<(string Type, long Protocol, string? LocalType, string? ArticleId)>(
                "SELECT event_type, protocol_version, local_type, article_id FROM tbl_event ORDER BY sequence_num DESC LIMIT 1");
            row.Type.Should().Be("sealed");
            row.Protocol.Should().Be(4);
            row.LocalType.Should().Be(EventTypes.ArticleCreate);
            row.ArticleId.Should().BeNull();
        }

        // A 2.5 build is refused now.
        (await AuthenticateAs25Async(_b, _clientA)).StatusCode.Should().Be(HttpStatusCode.UpgradeRequired);

        // B (2.6, not activated yet) still syncs: it pulls the key, the activation and the sealed article, and switches.
        await SyncAsync(_b, _clientA, _a);
        _b.Services.GetRequiredService<ISyncProtocolPolicy>().SealsMetadata.Should().BeTrue();
        (await IdentityAsync(_clientB)).GetProperty("protocolVersion").GetInt32().Should().Be(4);
        var onB = await _clientB.GetFromJsonAsync<JsonElement>($"/api/articles/{created.GetProperty("id").GetString()}", JsonOpts);
        onB.GetProperty("title").GetString().Should().Be(title, "B opened the sealed event with the key it pulled");
        using (var conn = Db(_b))
            (await conn.ExecuteScalarAsync<string>("SELECT key_id FROM tbl_metadata_key")).Should().Be(keyId);
    }

    [Fact]
    public async Task TheWire_CarriesTheCanariesBeforeTheSwitch_AndNoneAfter()
    {
        var capture = new CapturingHandler();
        var wireToA = _a.CreateDefaultClient(capture);

        // Before: B's push carries its title, path and tag in clear (the scanner sees them).
        var before = new[] { MetadataCanaries.Ascii("Before"), MetadataCanaries.Ascii("BeforeFolder") };
        await CreateArticleAsync(_clientB, before[0], "/" + before[1]);
        await SyncAsync(_b, wireToA, _a);
        capture.Scan(before).Should().NotBeEmpty("before the switch the push is protocol 3, in clear");

        (await ActivateAsync(_clientA, MasterPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
        await SyncAsync(_b, wireToA, _a);
        _b.Services.GetRequiredService<ISyncProtocolPolicy>().SealsMetadata.Should().BeTrue();

        capture.Clear();
        var fromB = new[] { MetadataCanaries.NonAscii("Pushed"), MetadataCanaries.Ascii("PushedFolder"), MetadataCanaries.Ascii("PushedTag") };
        await CreateArticleAsync(_clientB, fromB[0], "/" + fromB[1], [fromB[2], fromB[2].ToUpperInvariant()]);
        var fromA = new[] { MetadataCanaries.NonAscii("Pulled"), MetadataCanaries.Ascii("PulledFolder") };
        await CreateArticleAsync(_clientA, fromA[0], "/" + fromA[1]);
        await SyncAsync(_b, wireToA, _a);

        capture.Bodies.Should().NotBeEmpty();
        capture.Scan([.. fromB, .. fromA]).Should().BeEmpty("after the switch content travels sealed, both ways");
        // And it did travel: both nodes hold both notes.
        (await TitlesAsync(_clientA)).Should().Contain(fromB[0]);
        (await TitlesAsync(_clientB)).Should().Contain(fromA[0]);

        // What A serves to any peer from its log carries no canary either.
        var token = await TokenAsync(_b, wireToA);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/sync/events?afterSequence=0");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var served = await (await wireToA.SendAsync(req)).Content.ReadAsByteArrayAsync();
        MetadataCanaries.Scan(served, [.. fromB, .. fromA], "served").Should().BeEmpty();
    }

    /// <summary>
    /// Stragglers on serve (spec 5.7): after the switch a blind requester (a blind phone pulling) is never given a
    /// protocol-1..3 content row, historic ones included; a full node still is.
    /// </summary>
    [Fact]
    public async Task AfterTheSwitch_ABlindRequester_IsNotServedProtocol3Content_AFullNodeIs()
    {
        var before = MetadataCanaries.Ascii("BeforeSwitch");
        await CreateArticleAsync(_clientA, before, "/Clear");
        var (blindId, blindSeed) = await AddBlindPeerAsync(_a);
        await AuthenticateWithAsync(_clientA, blindId, blindSeed); // A sees its capability: readiness lets the switch through
        await SyncAsync(_b, _clientA, _a);
        (await ActivateAsync(_clientA, MasterPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
        var after = MetadataCanaries.Ascii("AfterSwitch");
        await CreateArticleAsync(_clientA, after, "/Sealed");

        var toBlind = await EventsAsync(_clientA, await AuthenticateWithAsync(_clientA, blindId, blindSeed));
        toBlind.Select(e => e.GetProperty("eventType").GetString()).Should().Contain(EventTypes.Sealed)
            .And.NotContain([EventTypes.ArticleCreate, EventTypes.FolderCreate], "no clear content for a blind requester after the switch");
        MetadataCanaries.Scan(Encoding.UTF8.GetBytes(string.Join("\n", toBlind)), [before, after], "served to blind").Should().BeEmpty();

        var toFull = await EventsAsync(_clientA, await TokenAsync(_b, _clientA));
        toFull.Select(e => e.GetProperty("eventType").GetString()).Should().Contain(EventTypes.ArticleCreate,
            "a full node still gets the rows written before the switch");
    }

    /// <summary>
    /// Stragglers on serve must not stall a pull (review finding): a page is filled with rows the requester may have, so a
    /// client that follows its cursor row by row (<c>limit</c> 1, 2, 3) reaches every sealed row behind the pre-activation
    /// clear ones and ends with exactly what one big pull gives. Before the fix the page was cut first and filtered after,
    /// so a page that held only a clear row came back empty and the cursor never moved.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AfterTheSwitch_ABlindRequesterPagingByCursor_ReachesTheSealedRowsBehindTheClearOnes(int limit)
    {
        for (var i = 0; i < 3; i++) await CreateArticleAsync(_clientA, MetadataCanaries.Ascii("Clear" + i), "/Clear" + i);
        var (blindId, blindSeed) = await AddBlindPeerAsync(_a);
        await AuthenticateWithAsync(_clientA, blindId, blindSeed);
        await SyncAsync(_b, _clientA, _a);
        (await ActivateAsync(_clientA, MasterPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
        for (var i = 0; i < 2; i++) await CreateArticleAsync(_clientA, MetadataCanaries.Ascii("Sealed" + i), "/Sealed" + i);
        var token = await AuthenticateWithAsync(_clientA, blindId, blindSeed);

        var everything = (await EventsAsync(_clientA, token)).Select(e => e.GetProperty("eventId").GetString()).ToList();
        everything.Should().NotBeEmpty();

        var paged = new List<string?>();
        long cursor = 0;
        for (var round = 0; round < 500; round++)
        {
            var page = await EventsAsync(_clientA, token, cursor, limit);
            if (page.Count == 0) break;
            page.Count.Should().BeLessThanOrEqualTo(limit);
            paged.AddRange(page.Select(e => e.GetProperty("eventId").GetString()));
            cursor = page[^1].GetProperty("sequenceNum").GetInt64();
        }

        paged.Should().Equal(everything, "paging by the cursor reaches exactly what one pull gives, whatever the page size");
        (await EventsAsync(_clientA, token, 0, limit)).Should().NotBeEmpty("the first page already holds a row the requester may have");
        using var conn = Db(_a);
        var sealedIds = (await conn.QueryAsync<string>("SELECT event_id FROM tbl_event WHERE event_type = 'sealed'"))
            .Select(id => id.ToLowerInvariant()).ToList();
        sealedIds.Should().HaveCount(2);
        paged.Select(id => id!.ToLowerInvariant()).Should().Contain(sealedIds, "every sealed row behind the clear ones was reached");
    }

    /// <summary>
    /// When only rows a blind requester may not have are left, its page is empty (nothing is lost: the next baseline
    /// carries them) and the delivery position moves over them, as the push side does, so the node is not shown as behind.
    /// </summary>
    [Fact]
    public async Task AfterTheSwitch_WhenOnlyClearRowsAreLeft_TheBlindRequesterGetsAnEmptyPage_AndIsNotShownBehind()
    {
        var (blindId, blindSeed) = await AddBlindPeerAsync(_a);
        await AuthenticateWithAsync(_clientA, blindId, blindSeed);
        await SyncAsync(_b, _clientA, _a);
        (await ActivateAsync(_clientA, MasterPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
        await CreateArticleAsync(_clientA, MetadataCanaries.Ascii("Sealed"), "/Sealed");
        var token = await AuthenticateWithAsync(_clientA, blindId, blindSeed);
        var cursor = (await EventsAsync(_clientA, token))[^1].GetProperty("sequenceNum").GetInt64();

        // A straggler that reached A in clear after the switch (a 2.6 node that had not pulled it yet).
        using (var scope = _a.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().AppendAsync(new SyncEvent
            {
                EventId = Guid.NewGuid(), NodeId = Guid.NewGuid(), LamportTs = 9_000, EventType = EventTypes.FolderCreate,
                Payload = "{}", Signature = new byte[64], ProtocolVersion = 3, CreatedAt = DateTime.UtcNow,
            });
        long head;
        using (var scope = _a.Services.CreateScope())
            head = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
        head.Should().BeGreaterThan(cursor);

        (await EventsAsync(_clientA, token, cursor, 5)).Should().BeEmpty();

        using (var scope = _a.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<ISyncPushPositionRepository>().GetAsync(blindId))!
                .LastPushedSeq.Should().Be(head, "everything the requester may have was offered");
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    /// <summary>A blind peer whitelisted on <paramref name="node"/>, with a key this test holds.</summary>
    private static async Task<(Guid Id, byte[] Seed)> AddBlindPeerAsync(BmbWebApplicationFactory node)
    {
        var (pub, seed) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
        var id = BlindNodeId.NewId();
        using var scope = node.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = "Blind phone", Ed25519PublicKey = pub, Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return (id, seed);
    }

    /// <summary>Authenticates as a 2.6 peer whose key the test holds.</summary>
    private static async Task<string> AuthenticateWithAsync(HttpClient server, Guid nodeId, byte[] seed)
    {
        var challenge = await (await server.PostAsync("/api/sync/challenge", null)).Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var payload = "BMB-CHALLENGE-V2\0"u8.ToArray()
            .Concat(Guid.Parse(challenge.GetProperty("serverNodeId").GetString()!).ToByteArray())
            .Concat(Convert.FromBase64String(challenge.GetProperty("challenge").GetString()!)).ToArray();
        var resp = await server.PostAsJsonAsync("/api/sync/authenticate", new
        {
            NodeId = nodeId,
            ChallengeB64 = challenge.GetProperty("challenge").GetString(),
            SignatureB64 = Convert.ToBase64String(BeeMemoryBank.Crypto.Ed25519Signer.Sign(seed, payload)),
            ProtocolVersion = 4,
            Capabilities = new[] { MetadataCapabilities.EnvelopeV1 },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("token").GetString()!;
    }

    private static async Task<List<JsonElement>> EventsAsync(HttpClient server, string token, long afterSequence = 0, int limit = 1000)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/sync/events?afterSequence={afterSequence}&limit={limit}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var resp = await server.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var items = body.ValueKind == JsonValueKind.Array ? body : body.GetProperty("events");
        return items.EnumerateArray().ToList();
    }

    private sealed class CapturingHandler : DelegatingHandler
    {
        private readonly List<(string Where, byte[] Bytes)> _bodies = [];
        public IReadOnlyList<(string Where, byte[] Bytes)> Bodies { get { lock (_bodies) return _bodies.ToList(); } }
        public void Clear() { lock (_bodies) _bodies.Clear(); }

        public List<CanaryHit> Scan(IEnumerable<string> canaries)
        {
            var list = canaries.ToList();
            return Bodies.SelectMany(b => MetadataCanaries.Scan(b.Bytes, list, b.Where)).ToList();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null)
            {
                await request.Content.LoadIntoBufferAsync(ct);
                Add($"{request.Method} {request.RequestUri}", await request.Content.ReadAsByteArrayAsync(ct));
            }
            var response = await base.SendAsync(request, ct);
            await response.Content.LoadIntoBufferAsync(ct);
            Add($"response {request.RequestUri}", await response.Content.ReadAsByteArrayAsync(ct));
            return response;
        }

        private void Add(string where, byte[] bytes) { lock (_bodies) _bodies.Add((where, bytes)); }
    }

    private static async Task UnlockAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/session/unlock", new { password = MasterPassword });
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> ReadinessAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/sync/readiness?peers=false", JsonOpts);

    private static Task<HttpResponseMessage> ActivateAsync(HttpClient client, string password) =>
        client.PostAsJsonAsync("/api/sync/activation", new { password });

    private static async Task<JsonElement> IdentityAsync(HttpClient client) =>
        await client.GetFromJsonAsync<JsonElement>("/api/sync/identity", JsonOpts);

    private static async Task<JsonElement> CreateArticleAsync(HttpClient client, string title, string treePath, string[]? tags = null)
    {
        var resp = await client.PostAsJsonAsync("/api/articles", new { title, treePath, content = "body", conceptTags = tags ?? [] });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
    }

    private static async Task<List<string?>> TitlesAsync(HttpClient client)
    {
        var list = await client.GetFromJsonAsync<JsonElement>("/api/articles", JsonOpts);
        var items = list.ValueKind == JsonValueKind.Array ? list : list.GetProperty("items");
        return items.EnumerateArray().Select(a => a.GetProperty("title").GetString()).ToList();
    }

    private static async Task SyncAsync(BmbWebApplicationFactory node, HttpClient serverClient, BmbWebApplicationFactory server)
    {
        Guid serverId;
        using (var s = server.Services.CreateScope())
            serverId = (await s.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        using var scope = node.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SyncClient>().SyncWithAsync(serverClient, "", serverId);
    }

    /// <summary>A token as the 2.6 build gets it (capabilities included).</summary>
    private static async Task<string> TokenAsync(BmbWebApplicationFactory node, HttpClient server)
    {
        using var scope = node.Services.CreateScope();
        var identity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var serverIdentity = await IdentityAsync(server);
        return await PeerAuthenticator.AuthenticateAsync(scope.ServiceProvider.GetRequiredService<INodeAuthSigner>(), server, "",
            identity, Guid.Parse(serverIdentity.GetProperty("nodeId").GetString()!));
    }

    /// <summary>Authenticates the way a 2.5 build does: protocol 3, and no capabilities or version at all.</summary>
    private static async Task<HttpResponseMessage> AuthenticateAs25Async(BmbWebApplicationFactory node, HttpClient server)
    {
        var challenge = await (await server.PostAsync("/api/sync/challenge", null)).Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        using var scope = node.Services.CreateScope();
        var identity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var payload = "BMB-CHALLENGE-V2\0"u8.ToArray()
            .Concat(Guid.Parse(challenge.GetProperty("serverNodeId").GetString()!).ToByteArray())
            .Concat(Convert.FromBase64String(challenge.GetProperty("challenge").GetString()!)).ToArray();
        var signature = scope.ServiceProvider.GetRequiredService<INodeAuthSigner>().SignChallenge(identity, payload);
        return await server.PostAsJsonAsync("/api/sync/authenticate", new
        {
            NodeId = identity.NodeId,
            ChallengeB64 = challenge.GetProperty("challenge").GetString(),
            SignatureB64 = Convert.ToBase64String(signature),
            ProtocolVersion = 3,
        });
    }

    private static System.Data.IDbConnection Db(BmbWebApplicationFactory node) =>
        node.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();

    private static async Task<long> CountAsync(BmbWebApplicationFactory node, string table)
    {
        using var conn = Db(node);
        return await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {table}");
    }
}
