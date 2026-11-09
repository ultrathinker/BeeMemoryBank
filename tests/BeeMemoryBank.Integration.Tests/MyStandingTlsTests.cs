using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-337: <c>GET /api/sync/my-standing</c> says, for each peer in its list, how this node reaches it over TLS (the join's rule, <see cref="JoinTls"/>), so a
/// device that joined before 2.5.1 and holds no key for a blind node can take it (<see cref="PeerTlsBackfill"/>). The members are optional additions: every
/// member an older client reads is still there.
/// </summary>
public class MyStandingTlsTests : IDisposable
{
    private static readonly string Pin = Base64Url.EncodeToString(Enumerable.Repeat((byte)3, 32).ToArray());

    private readonly BmbWebApplicationFactory _node = new();
    private readonly Guid _caller = Guid.NewGuid();
    private readonly Guid _blind = BlindNodeId.NewId();
    private readonly Guid _publicCa = Guid.NewGuid();
    private readonly Guid _plain = Guid.NewGuid();
    private readonly Guid _damaged = Guid.NewGuid();
    private readonly Guid _legacyPin = Guid.NewGuid();

    public void Dispose() => _node.Dispose();

    private async Task AddAsync(Guid id, string? trust, string? spki, string name)
    {
        await _node.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = name, Ed25519PublicKey = new byte[32], Status = "A", TlsTrust = trust, TlsSpki = spki,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
    }

    private async Task<JsonElement> StandingAsync()
    {
        await _node.InitializeNodeAsync(password: "myStandingTlsPw1!");
        await AddAsync(_caller, null, null, "caller");
        await AddAsync(_blind, "pin", Pin, "blind");
        await AddAsync(_publicCa, "public-ca", null, "hub-on-a-ca");
        await AddAsync(_plain, null, null, "plain");
        await AddAsync(_damaged, "pin", "not-a-pin", "damaged");
        await AddAsync(_legacyPin, null, Pin, "legacy-pin");   // a pin with no mode: a row of an older build
        using var http = _node.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer",
            _node.Services.GetRequiredService<SyncTokenStore>().IssueToken(_caller, SyncProtocolVersion.Current));

        var resp = await http.GetAsync("/api/sync/my-standing");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static JsonElement PeerOf(JsonElement standing, Guid id) =>
        standing.GetProperty("peers").EnumerateArray().Single(p => p.GetProperty("node_id").GetGuid() == id);

    [Fact]
    public async Task APinnedPeer_IsReportedWithItsMode_AndItsKey()
    {
        var standing = await StandingAsync();

        var peer = PeerOf(standing, _blind);
        peer.GetProperty("tls_trust").GetString().Should().Be("pin");
        peer.GetProperty("tls_spki").GetString().Should().Be(Pin);
    }

    [Fact]
    public async Task APinWithNoMode_IsReportedAsAPin_AsTheJoinHandsItOn()
    {
        var peer = PeerOf(await StandingAsync(), _legacyPin);

        peer.GetProperty("tls_trust").GetString().Should().Be("pin");
        peer.GetProperty("tls_spki").GetString().Should().Be(Pin);
    }

    [Fact]
    public async Task APeerOnAPublicCa_IsReportedWithNoKey_AndAPeerWithNothing_WithNeither()
    {
        var standing = await StandingAsync();

        var onCa = PeerOf(standing, _publicCa);
        onCa.GetProperty("tls_trust").GetString().Should().Be("public-ca");
        onCa.GetProperty("tls_spki").ValueKind.Should().Be(JsonValueKind.Null);
        var plain = PeerOf(standing, _plain);
        plain.GetProperty("tls_trust").ValueKind.Should().Be(JsonValueKind.Null);
        plain.GetProperty("tls_spki").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ADamagedPin_IsNotReported()
    {
        var peer = PeerOf(await StandingAsync(), _damaged);

        peer.GetProperty("tls_trust").ValueKind.Should().Be(JsonValueKind.Null);
        peer.GetProperty("tls_spki").ValueKind.Should().Be(JsonValueKind.Null, "the join refuses a damaged pin too: it is not handed on");
    }

    [Fact]
    public async Task TheAnswer_StillCarriesEveryMemberAnOlderClientReads_AndReadsAsAStanding()
    {
        var standing = await StandingAsync();

        foreach (var name in new[] { "responder_node_id", "protocol", "caller_is_superadmin", "reseed_needed", "peers", "dek_exposure", "capabilities", "app_version", "activated" })
            standing.TryGetProperty(name, out _).Should().BeTrue(name);
        var peer = PeerOf(standing, _blind);
        foreach (var name in new[] { "node_id", "display_name", "last_protocol_version", "last_protocol_seen_at", "created_at", "capabilities", "app_version", "capabilities_seen_at" })
            peer.TryGetProperty(name, out _).Should().BeTrue(name);

        // The record both sides read it with.
        var read = standing.Deserialize<MyStanding>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        read.Peers.Single(p => p.NodeId == _blind).TlsSpki.Should().Be(Pin);
    }
}
