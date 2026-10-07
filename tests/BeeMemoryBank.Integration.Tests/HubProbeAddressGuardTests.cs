using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.TestSupport;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// "Let blind copies call this node" asks this server to connect to an address an administrator typed (ADR 0007). A blind copy
/// can only call a hub through an address it can reach, so loopback, link-local (cloud metadata included), unspecified and
/// multicast targets are never a hub — and must not become a way to make the server probe its own machine or network. The
/// private LAN ranges stay allowed (a pinned hub on a company or home network is a supported setup).
/// These tests drive the probe as production wires it, against a live TLS server on the loopback interface: a refusal means
/// the server was never sent a thing.
/// </summary>
public sealed class HubProbeAddressGuardTests : IAsyncLifetime
{
    private readonly X509Certificate2 _certificate = TestPki.SelfSigned("localhost");
    private readonly Guid _node = Guid.NewGuid();
    private LoopbackTlsServer _server = null!;

    public Task InitializeAsync()
    {
        _server = new LoopbackTlsServer(_certificate, IdentityReply(_node));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _certificate.Dispose();
    }

    internal static string IdentityReply(Guid node) =>
        Reply(200, JsonSerializer.Serialize(new { nodeId = node, ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]) }));

    internal static string Reply(int status, string body) =>
        $"HTTP/1.1 {status} Reply\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    private static HubTrustProbe Production() => new(TlsTrustAnchors.None);

    /// <summary>For tests whose hub is on the loopback interface: every address is allowed, names resolve as usual.</summary>
    internal static readonly HubProbeNetwork AnyAddress = new(_ => true, (host, ct) => Dns.GetHostAddressesAsync(host, ct));

    /// <summary>Every way of writing the loopback address of the machine the live server is on.</summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]              // a name that resolves to loopback
    [InlineData("2130706433")]             // the same address as one number
    [InlineData("127.1")]                  // short form
    [InlineData("[::ffff:127.0.0.1]")]     // IPv4-mapped IPv6
    public async Task ALoopbackTarget_IsRefused_AndNothingIsSentToIt(string host)
    {
        var act = () => Production().ProbeAsync($"https://{host}:{_server.Port}", BlindTrust.Pin, null, CancellationToken.None);

        (await act.Should().ThrowAsync<HubProbeException>()).WithMessage("*can never be a hub*");
        _server.Requests.Should().Be(0, "the guard refuses before a request is made");
    }

    [Theory]
    [InlineData("169.254.169.254")]        // link-local: the cloud metadata address
    [InlineData("169.254.1.1")]
    [InlineData("[fe80::1]")]              // IPv6 link-local
    [InlineData("0.0.0.0")]                // unspecified
    [InlineData("[::]")]
    [InlineData("224.0.0.1")]              // multicast
    [InlineData("239.255.255.250")]
    [InlineData("[ff02::1]")]
    [InlineData("[::ffff:169.254.169.254]")] // IPv4-mapped forms of the above
    [InlineData("[::ffff:0.0.0.0]")]
    public async Task ALinkLocalUnspecifiedOrMulticastTarget_IsRefused_WithoutAConnection(string host)
    {
        var act = () => Production().ProbeAsync($"https://{host}:443", BlindTrust.PublicCa, null, CancellationToken.None);

        (await act.Should().ThrowAsync<HubProbeException>()).WithMessage("*can never be a hub*");
    }

    private HubTrustProbe With(Func<IPAddress, bool> isAllowed, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) =>
        new(TlsTrustAnchors.None, new HubProbeNetwork(isAllowed, resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct))));

    /// <summary>A name is judged on every address it resolves to: one good answer does not carry a bad one along.</summary>
    [Fact]
    public async Task ANameWithOneBadAnswer_IsRefused_BeforeAnyConnection()
    {
        var asked = 0;
        var probe = With(HostAddressPolicy.IsPossibleHub, (_, _) =>
        {
            asked++;
            return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Loopback });
        });

        var act = () => probe.ProbeAsync($"https://rebind.test:{_server.Port}", BlindTrust.Pin, null, CancellationToken.None);

        (await act.Should().ThrowAsync<HubProbeException>()).WithMessage("*can never be a hub*");
        asked.Should().Be(1);
        _server.Requests.Should().Be(0);
    }

    /// <summary>
    /// The check is made again on the address the socket actually ended up on: whatever the first answer said, a connection
    /// to a refused address is dropped before the TLS handshake, so nothing is sent.
    /// </summary>
    [Fact]
    public async Task TheAddressActuallyConnectedTo_IsCheckedToo()
    {
        var calls = new List<IPAddress>();
        var probe = With(a => { calls.Add(a); return calls.Count == 1; }, (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));

        var act = () => probe.ProbeAsync($"https://hub.test:{_server.Port}", BlindTrust.Pin, null, CancellationToken.None);

        (await act.Should().ThrowAsync<HubProbeException>()).WithMessage("*can never be a hub*");
        calls.Should().HaveCount(2, "once for what the name resolved to, once for where the socket connected");
        _server.Requests.Should().Be(0);
    }

    /// <summary>The connection goes to the addresses that were checked, not to a second resolution of the name.</summary>
    [Fact]
    public async Task AnAllowedName_IsConnectedToAtWhatWasResolved()
    {
        var probe = With(_ => true, (host, _) => Task.FromResult(host == "hub.test" ? new[] { IPAddress.Loopback } : []));

        var found = await probe.ProbeAsync($"https://hub.test:{_server.Port}", BlindTrust.Pin, null, CancellationToken.None);

        found.NodeId.Should().Be(_node);
        _server.Requests.Should().Be(1);
    }

    /// <summary>A private LAN address is a supported hub: the rule lets it through to the connection (here stopped before any packet).</summary>
    [Theory]
    [InlineData("10.20.30.40")]
    [InlineData("172.20.1.1")]
    [InlineData("192.168.50.20")]
    [InlineData("fd12:3456::1")]
    public async Task APrivateLanAddress_IsNotRefused_ByTheRule(string address)
    {
        using var stop = new CancellationTokenSource();
        var decisions = new List<bool>();
        var probe = With(a =>
        {
            var allowed = HostAddressPolicy.IsPossibleHub(a);
            decisions.Add(allowed);
            if (allowed) stop.Cancel(); // the connection attempt then ends before a packet is sent
            return allowed;
        }, (_, _) => Task.FromResult(new[] { IPAddress.Parse(address) }));

        var act = () => probe.ProbeAsync("https://hub.lan:8443", BlindTrust.Pin, null, stop.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("the address was allowed, so the probe went on to connect");
        decisions.Should().Equal(true);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("::1")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("::")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("ff02::1")]
    [InlineData("ff0e::1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::ffff:127.0.0.1")]          // IPv4-mapped
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:0.0.0.0")]
    [InlineData("::ffff:224.0.0.1")]
    [InlineData("::127.0.0.1")]               // IPv4-compatible
    [InlineData("64:ff9b::7f00:1")]           // NAT64 of 127.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe")]        // NAT64 of the cloud metadata address
    [InlineData("2002:7f00:1::")]             // 6to4 of 127.0.0.1
    [InlineData("2002:a9fe:a9fe::1")]         // 6to4 of the metadata address
    public void TheseAddresses_AreNeverAHub(string address) =>
        HostAddressPolicy.IsPossibleHub(IPAddress.Parse(address)).Should().BeFalse();

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.10")]
    [InlineData("fd00::1")]
    [InlineData("fc00::5")]
    [InlineData("::ffff:10.0.0.5")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("64:ff9b::a00:1")]            // NAT64 of 10.0.0.1: a LAN address stays a LAN address
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("192.169.0.1")]
    [InlineData("223.255.255.255")]
    [InlineData("2606:4700:4700::1111")]
    public void TheseAddresses_MayBeAHub(string address) =>
        HostAddressPolicy.IsPossibleHub(IPAddress.Parse(address)).Should().BeTrue();

    /// <summary>The probe relay's own rule (open internet only) is still what it was, and a little stricter: private ranges stay refused there.</summary>
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("10.0.0.5", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::ffff:10.0.0.5", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    public void TheProbeRelayRule_StaysPublicOnly(string address, bool isPublic) =>
        HostAddressPolicy.IsPublic(IPAddress.Parse(address)).Should().Be(isPublic);

    /// <summary>Redirects are never followed: the redirect target is not contacted and the answer says so.</summary>
    [Fact]
    public async Task ARedirect_IsNotFollowed_AndItsTargetIsNeverContacted()
    {
        await using var target = new LoopbackTlsServer(_certificate, IdentityReply(_node));
        await using var redirecting = new LoopbackTlsServer(_certificate,
            LoopbackTlsServer.Redirect($"https://127.0.0.1:{target.Port}/api/sync/identity"));

        var act = () => With(_ => true).ProbeAsync($"https://127.0.0.1:{redirecting.Port}", BlindTrust.Pin, null, CancellationToken.None);

        (await act.Should().ThrowAsync<HubProbeException>()).WithMessage("*redirects*");
        redirecting.Requests.Should().Be(1);
        target.Requests.Should().Be(0);
    }

    /// <summary>What the target sent (body, status line, a redirect location) never comes back in the error.</summary>
    [Theory]
    [InlineData(500, "INTERNAL-SECRET-BODY")]
    [InlineData(404, "INTERNAL-SECRET-BODY")]
    [InlineData(200, "INTERNAL-SECRET-BODY")]                          // not an identity document
    [InlineData(200, "{\"nodeId\":\"INTERNAL-SECRET-BODY\"}")]         // an identity document that does not parse
    public async Task TheErrorText_NeverEchoesWhatTheTargetSent(int status, string body)
    {
        await using var server = new LoopbackTlsServer(_certificate, Reply(status, body));

        var act = () => With(_ => true).ProbeAsync($"https://127.0.0.1:{server.Port}", BlindTrust.Pin, null, CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<HubProbeException>()).Which;
        thrown.Message.Should().NotContain("INTERNAL-SECRET").And.NotContain(status.ToString());
    }

    [Fact]
    public async Task ARedirectsLocation_IsNotEchoed()
    {
        await using var server = new LoopbackTlsServer(_certificate, LoopbackTlsServer.Redirect("https://INTERNAL-SECRET-HOST.corp/x"));

        var act = () => With(_ => true).ProbeAsync($"https://127.0.0.1:{server.Port}", BlindTrust.Pin, null, CancellationToken.None);

        (await act.Should().ThrowAsync<HubProbeException>()).Which.Message.Should().NotContain("INTERNAL-SECRET");
    }

    /// <summary>The identity document is a few hundred bytes: a target that sends more is not read to the end.</summary>
    [Fact]
    public async Task AnOversizedAnswer_IsRefused_WithoutItsContent()
    {
        await using var server = new LoopbackTlsServer(_certificate, Reply(200, new string('A', 200_000)));

        var act = () => With(_ => true).ProbeAsync($"https://127.0.0.1:{server.Port}", BlindTrust.Pin, null, CancellationToken.None);

        (await act.Should().ThrowAsync<HubProbeException>()).Which.Message.Should().NotContain("AAAA");
    }

    [Fact]
    public async Task AClosedPort_IsSaidSo_InTheServersOwnWords()
    {
        var closed = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        var act = () => With(_ => true).ProbeAsync($"https://127.0.0.1:{port}", BlindTrust.Pin, null, CancellationToken.None);

        (await act.Should().ThrowAsync<HubProbeException>()).WithMessage($"*127.0.0.1:{port}*refused*");
    }
}

/// <summary>
/// The endpoint as a node runs it ("Let blind copies call this node" with the shipped address rule), aimed at a live TLS
/// server on the loopback interface that answers as the chosen node would. Before the rule it was saved; now the request is
/// refused, nothing is published and nothing is changed.
/// </summary>
public sealed class HubSetupAddressGuardTests : IAsyncLifetime
{
    private const string Password = "hubGuardPassword1";

    private readonly X509Certificate2 _certificate = TestPki.SelfSigned("localhost");
    private readonly Guid _node = Guid.NewGuid();
    private BmbWebApplicationFactory _api = null!;
    private HttpClient _client = null!;
    private LoopbackTlsServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = new LoopbackTlsServer(_certificate, HubProbeAddressGuardTests.IdentityReply(_node));
        _api = new BmbWebApplicationFactory();
        _client = _api.CreateClient();
        await AddNodeAsync(_api, _client);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _api.Dispose();
        await _server.DisposeAsync();
        _certificate.Dispose();
    }

    private async Task AddNodeAsync(BmbWebApplicationFactory api, HttpClient client)
    {
        await api.InitializeNodeAsync("Admin", Password);
        (await client.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();
        using var scope = api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = _node, DisplayName = "Server", Ed25519PublicKey = new byte[32], Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
    }

    private async Task<int> EventCountAsync()
    {
        using var scope = _api.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(0, 10_000)).Count;
    }

    [Theory]
    [InlineData("pin", "127.0.0.1")]
    [InlineData("public-ca", "127.0.0.1")]
    [InlineData("pin", "localhost")]
    [InlineData("pin", "[::ffff:127.0.0.1]")]
    [InlineData("pin", "169.254.169.254")]
    public async Task ALoopbackOrMetadataAddress_IsRefused_NothingIsPublished_AndNothingChanges(string trust, string host)
    {
        var before = await EventCountAsync();

        var resp = await _client.PutAsJsonAsync($"/api/whitelist/{_node}/hub",
            new { trust, address = $"https://{host}:{_server.Port}", password = Password });

        var text = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain("can never be a hub");
        _server.Requests.Should().Be(0);
        using var scope = _api.Services.CreateScope();
        var row = (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(_node))!;
        (row.ApiAddress, row.TlsTrust, row.TlsSpki).Should().Be((null, null, null));
        (await EventCountAsync()).Should().Be(before);
    }

    /// <summary>The address rule is not the only check: another node answering is still refused, and its id is not repeated.</summary>
    [Fact]
    public async Task AnotherNodeAtTheAddress_IsRefused_WithoutItsId()
    {
        var other = Guid.NewGuid();
        await using var impostor = new LoopbackTlsServer(_certificate, HubProbeAddressGuardTests.IdentityReply(other));
        using var factory = new AllowLoopbackNodeFactory();
        using var client = factory.CreateClient();
        await AddNodeAsync(factory, client);

        var resp = await client.PutAsJsonAsync($"/api/whitelist/{_node}/hub",
            new { trust = "pin", address = $"https://127.0.0.1:{impostor.Port}", password = Password });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var text = await resp.Content.ReadAsStringAsync();
        text.Should().Contain("different node").And.NotContain(other.ToString());
    }

    /// <summary>A node on the loopback interface, as the probe is wired only in tests: everything but the address rule is production's.</summary>
    private sealed class AllowLoopbackNodeFactory : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
                services.AddSingleton(new HubTrustProbe(TlsTrustAnchors.None, HubProbeAddressGuardTests.AnyAddress)));
        }
    }
}
