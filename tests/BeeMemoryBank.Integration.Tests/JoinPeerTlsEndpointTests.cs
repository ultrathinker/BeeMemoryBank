using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The join hands the joiner the way the host dials each other peer over TLS (a pin, or the public CAs), so a joiner checks that
/// peer as the host does: through the real <c>POST /api/join</c>, and through the Setup page's join (<c>/api/init/join</c>) that reads it.
/// </summary>
public class JoinPeerTlsEndpointTests : IAsyncLifetime
{
    private const string Password = "joinPeerTlsPassword1";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly Guid _pinnedPeer = Guid.NewGuid();
    private readonly Guid _publicCaPeer = Guid.NewGuid();
    private readonly Guid _plainPeer = Guid.NewGuid();
    private readonly string _pin = System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private BmbWebApplicationFactory _host = null!;
    private HttpClient _hostClient = null!;

    public async Task InitializeAsync()
    {
        // The limiter of /api/join is process-wide (5 per 5 minutes per caller); these joins must neither hit it nor use up what the join tests
        // of other classes of this process count on.
        BeeMemoryBank.Api.Middleware.RateLimitMiddleware.ResetForTests();
        _host = new BmbWebApplicationFactory();
        _hostClient = _host.CreateClient();
        await _host.InitializeNodeAsync("PeerTlsHost", Password);
        (await _hostClient.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();

        using var scope = _host.Services.CreateScope();
        var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        await AddPeerAsync(whitelist, _pinnedPeer, "https://peer-b.example:5301", tlsTrust: BlindTrust.Pin, tlsSpki: _pin);
        await AddPeerAsync(whitelist, _publicCaPeer, "https://peer-c.example", tlsTrust: BlindTrust.PublicCa);
        await AddPeerAsync(whitelist, _plainPeer, "https://peer-d.example");
    }

    public Task DisposeAsync()
    {
        BeeMemoryBank.Api.Middleware.RateLimitMiddleware.ResetForTests();
        _hostClient.Dispose();
        _host.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task TheJoinResponse_CarriesThePinOfEveryPeerThatHasOne()
    {
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        var resp = await _hostClient.PostAsJsonAsync("/api/join", new
        {
            masterPassword = Password,
            nodeId = Guid.NewGuid(),
            displayName = "Joiner",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var peers = (await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("whitelist").EnumerateArray()
            .ToDictionary(e => e.GetProperty("nodeId").GetGuid());

        string? Field(Guid id, string name) =>
            peers[id].TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        Field(_pinnedPeer, "tlsTrust").Should().Be("pin");
        Field(_pinnedPeer, "tlsSpki").Should().Be(_pin);
        Field(_publicCaPeer, "tlsTrust").Should().Be("public-ca");
        Field(_publicCaPeer, "tlsSpki").Should().BeNull();
        Field(_plainPeer, "tlsTrust").Should().BeNull();
        Field(_plainPeer, "tlsSpki").Should().BeNull();
    }

    [Fact]
    public async Task TheSetupPagesJoin_RecordsThePeersWithTheirPins()
    {
        using var joiner = new BmbWebApplicationFactory();
        joiner.RouteOutboundHttpThrough(_host.Server.CreateHandler());
        var joinerClient = joiner.CreateClient();

        var resp = await joinerClient.PostAsJsonAsync("/api/init/join", new
        {
            adminUsername = "admin",
            displayName = "Joiner",
            remoteUrl = "http://host", // arbitrary: RouteOutboundHttpThrough ignores the host
            password = Password
        }, JsonOpts);
        resp.IsSuccessStatusCode.Should().BeTrue(await resp.Content.ReadAsStringAsync());

        using var scope = joiner.Services.CreateScope();
        var rows = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var pinned = (await rows.GetByNodeIdAsync(_pinnedPeer))!;
        (pinned.TlsTrust, pinned.TlsSpki).Should().Be((BlindTrust.Pin, _pin));
        var publicCa = (await rows.GetByNodeIdAsync(_publicCaPeer))!;
        (publicCa.TlsTrust, publicCa.TlsSpki).Should().Be((BlindTrust.PublicCa, null));
        var plain = (await rows.GetByNodeIdAsync(_plainPeer))!;
        (plain.TlsTrust, plain.TlsSpki).Should().Be((null, null));
    }

    /// <summary>
    /// A row in pin mode whose pin is damaged or lost cannot be dialled safely. The host must leave it out of the join response: serialized
    /// without TLS fields it would read as an old-style peer, and the joiner would then check it through the public CAs, which a
    /// CA-valid impostor at that address passes. Leaving it out keeps the wire format as it was for nodes of every version.
    /// </summary>
    [Fact]
    public async Task ThePinnedPeerWhosePinIsNotAPin_IsNotHandedOverAsAnUnpinnedPeer()
    {
        var malformedPin = Guid.NewGuid();
        var lostPin = Guid.NewGuid();
        var legacyMalformedPin = Guid.NewGuid();
        using (var scope = _host.Services.CreateScope())
        {
            var whitelist = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            await AddPeerAsync(whitelist, malformedPin, "https://peer-e.example", tlsTrust: BlindTrust.Pin, tlsSpki: "not-a-pin");
            await AddPeerAsync(whitelist, lostPin, "https://peer-f.example", tlsTrust: BlindTrust.Pin, tlsSpki: null);
            await AddPeerAsync(whitelist, legacyMalformedPin, "https://peer-g.example", tlsTrust: null, tlsSpki: "not-a-pin");
        }

        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        var resp = await _hostClient.PostAsJsonAsync("/api/join", new
        {
            masterPassword = Password,
            nodeId = Guid.NewGuid(),
            displayName = "Joiner",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var ids = (await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("whitelist").EnumerateArray()
            .Select(e => e.GetProperty("nodeId").GetGuid()).ToList();
        ids.Should().NotContain(new[] { malformedPin, lostPin, legacyMalformedPin },
            "a peer whose pin is not a pin must not reach the joiner as a peer with no TLS requirement");
        ids.Should().Contain(new[] { _pinnedPeer, _publicCaPeer, _plainPeer }, "the peers that can be dialled safely are still handed over");

        // Through the Setup page's join: the joiner must not end up holding the damaged peer at all, unpinned or otherwise.
        using var joiner = new BmbWebApplicationFactory();
        joiner.RouteOutboundHttpThrough(_host.Server.CreateHandler());
        var joinerClient = joiner.CreateClient();
        var init = await joinerClient.PostAsJsonAsync("/api/init/join", new
        {
            adminUsername = "admin",
            displayName = "Joiner2",
            remoteUrl = "http://host",
            password = Password
        }, JsonOpts);
        init.IsSuccessStatusCode.Should().BeTrue(await init.Content.ReadAsStringAsync());

        using var joinerScope = joiner.Services.CreateScope();
        var rows = joinerScope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        foreach (var id in new[] { malformedPin, lostPin, legacyMalformedPin })
            (await rows.GetByNodeIdAsync(id, includeDeleted: true)).Should().BeNull();
        (await rows.GetByNodeIdAsync(_pinnedPeer)).Should().NotBeNull();
    }

    private static Task AddPeerAsync(IWhitelistRepository whitelist, Guid nodeId, string address, string? tlsTrust = null, string? tlsSpki = null) =>
        whitelist.CreateAsync(new WhitelistEntry
        {
            NodeId = nodeId,
            DisplayName = "Peer " + nodeId.ToString("N")[..6],
            Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            ApiAddress = address,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            TlsTrust = tlsTrust,
            TlsSpki = tlsSpki
        });
}
