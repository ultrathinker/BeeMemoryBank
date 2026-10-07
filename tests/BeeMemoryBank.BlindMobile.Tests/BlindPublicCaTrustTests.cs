using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.TestSupport;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// The blind app's side of the trust modes (ADR 0007), over real TLS on the loopback interface. A call code for a node on a
/// public CA gets the system's certificate validation — an invalid, expired, self-signed or wrongly named certificate is refused
/// before a byte is sent — and a client that reaches that node's origin only, over https only, following no redirect. A pinned
/// code keeps its pin. The test CA is an extra trust anchor handed to the provider: the production path (no anchors) is tested
/// to refuse the very same certificate.
/// </summary>
public sealed class BlindPublicCaTrustTests : IDisposable
{
    private static readonly byte[] NodeKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private readonly TestPki _pki = new();
    private readonly List<string> _certificateRefused = [];
    private readonly List<string> _pinRefused = [];

    public void Dispose() => _pki.Dispose();

    private BlindHttpClientProvider Provider(bool withTestRoot = true, BlindPhoneState? state = null) =>
        new(state ?? new BlindPhoneState(new InMemoryStore()), _pinRefused.Add, _certificateRefused.Add,
            withTestRoot ? new TlsTrustAnchors(_pki.Anchors) : null);

    private static BlindCallCode PublicCaCode(string host, int port) =>
        BlindCallCode.CreatePublicCa($"https://{host}:{port}", Guid.NewGuid(), NodeKey, BlindPairingSecret.New());

    [Fact]
    public async Task PublicCa_ACertificateFromATrustedRoot_ForTheHostName_IsReached()
    {
        using var cert = _pki.IssueServer("localhost");
        await using var server = new LoopbackTlsServer(cert);
        using var provider = Provider();

        using var client = provider.GetClient(PublicCaCode("localhost", server.Port));
        var response = await client.GetAsync($"https://localhost:{server.Port}/api/blind/replica");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        server.Requests.Should().Be(1);
        _certificateRefused.Should().BeEmpty();
    }

    [Fact]
    public async Task PublicCa_ACertificateForAnotherName_IsRefused_AndNothingIsSent()
    {
        using var cert = _pki.IssueServer("somewhere-else.test");
        await using var server = new LoopbackTlsServer(cert);
        using var provider = Provider();
        using var client = provider.GetClient(PublicCaCode("localhost", server.Port));

        var call = async () => await client.GetAsync($"https://localhost:{server.Port}/x");

        await call.Should().ThrowAsync<HttpRequestException>("the certificate is not for this host name");
        server.Requests.Should().Be(0);
        _certificateRefused.Should().ContainSingle().Which.Should().Contain($"localhost:{server.Port}");
    }

    [Fact]
    public async Task PublicCa_AnExpiredCertificate_IsRefused()
    {
        using var cert = _pki.IssueServer("localhost", notBefore: DateTimeOffset.UtcNow.AddDays(-5), notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        await using var server = new LoopbackTlsServer(cert);
        using var provider = Provider();
        using var client = provider.GetClient(PublicCaCode("localhost", server.Port));

        var call = async () => await client.GetAsync($"https://localhost:{server.Port}/x");

        await call.Should().ThrowAsync<HttpRequestException>();
        server.Requests.Should().Be(0);
    }

    [Fact]
    public async Task PublicCa_ACertificateFromAnAuthorityNobodyTrusts_IsRefused()
    {
        using var stranger = new TestPki("CN=Stranger root");
        using var cert = stranger.IssueServer("localhost");
        await using var server = new LoopbackTlsServer(cert);
        using var provider = Provider();
        using var client = provider.GetClient(PublicCaCode("localhost", server.Port));

        var call = async () => await client.GetAsync($"https://localhost:{server.Port}/x");

        await call.Should().ThrowAsync<HttpRequestException>();
        server.Requests.Should().Be(0);
    }

    [Fact]
    public async Task PublicCa_ASelfSignedCertificate_IsRefused_EvenWithItsOwnPinKnown()
    {
        using var cert = TestPki.SelfSigned("localhost");
        await using var server = new LoopbackTlsServer(cert);
        using var provider = Provider();
        using var client = provider.GetClient(PublicCaCode("localhost", server.Port));

        var call = async () => await client.GetAsync($"https://localhost:{server.Port}/x");

        await call.Should().ThrowAsync<HttpRequestException>("self-signed means pin mode, never this one");
        server.Requests.Should().Be(0);
    }

    [Fact]
    public async Task PublicCa_WithoutTheTestRoot_AsEveryShippedBuildRuns_EvenAValidTestChainIsRefused()
    {
        using var cert = _pki.IssueServer("localhost");
        await using var server = new LoopbackTlsServer(cert);
        using var provider = Provider(withTestRoot: false);
        using var client = provider.GetClient(PublicCaCode("localhost", server.Port));

        var call = async () => await client.GetAsync($"https://localhost:{server.Port}/x");

        await call.Should().ThrowAsync<HttpRequestException>(
            "the platform does not trust the test CA, and nothing in the production path accepts a certificate for any other reason");
        server.Requests.Should().Be(0);
    }

    [Fact]
    public async Task PublicCa_ARedirect_IsNotFollowed()
    {
        using var cert = _pki.IssueServer("localhost");
        await using var server = new LoopbackTlsServer(cert, LoopbackTlsServer.Redirect("https://elsewhere.test/api/blind/replica"));
        using var provider = Provider();
        using var client = provider.GetClient(PublicCaCode("localhost", server.Port));

        var call = async () => await client.GetAsync($"https://localhost:{server.Port}/api/blind/replica");

        (await call.Should().ThrowAsync<HttpRequestException>()).Which.Message.Should().Contain("redirect");
        server.Requests.Should().Be(1, "the first request went out; nothing followed it");
    }

    [Fact]
    public async Task PublicCa_AnotherOriginOfTheSameServer_IsRefusedBeforeAConnectionIsMade()
    {
        using var cert = _pki.IssueServer("localhost");
        await using var server = new LoopbackTlsServer(cert);
        using var provider = Provider();
        using var client = provider.GetClient(PublicCaCode("localhost", server.Port));

        // 127.0.0.1 is in the certificate too, so the certificate would pass; the origin is what the code named.
        var call = async () => await client.GetAsync($"https://127.0.0.1:{server.Port}/x");

        await call.Should().ThrowAsync<HttpRequestException>().WithMessage("*only calls*");
        server.Requests.Should().Be(0);
    }

    [Fact]
    public async Task PublicCa_PlainHttp_IsNeverUsed()
    {
        using var cert = _pki.IssueServer("localhost");
        await using var server = new LoopbackTlsServer(cert);
        using var provider = Provider();
        using var client = provider.GetClient(PublicCaCode("localhost", server.Port));

        var call = async () => await client.GetAsync($"http://localhost:{server.Port}/x");

        await call.Should().ThrowAsync<HttpRequestException>().WithMessage("*Cleartext*");
        server.Requests.Should().Be(0);
    }

    [Fact]
    public async Task APinnedCode_KeepsItsPin_ACaValidCertificateWithAnotherKeyIsRefused_AndTheRightKeyIsReached()
    {
        using var validButOther = _pki.IssueServer("localhost");
        using var selfSigned = TestPki.SelfSigned("localhost");
        var code = BlindCallCode.Create($"https://localhost:{1}", Guid.NewGuid(), SpkiPin.Of(selfSigned), NodeKey, BlindPairingSecret.New());
        using var provider = Provider();

        await using (var wrong = new LoopbackTlsServer(validButOther))
        {
            using var client = provider.GetClient(code);
            var call = async () => await client.GetAsync($"https://localhost:{wrong.Port}/x");
            await call.Should().ThrowAsync<HttpRequestException>("a pinned node is trusted by its key and by nothing a CA says");
            wrong.Requests.Should().Be(0);
        }
        _pinRefused.Should().ContainSingle();
        _certificateRefused.Should().BeEmpty("a pin refusal is not a certificate refusal");

        await using var right = new LoopbackTlsServer(selfSigned);
        using var again = provider.GetClient(code);
        (await again.GetAsync($"https://localhost:{right.Port}/x")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheProviderServesBothModes_AndFollowsTheCallCodeInTheStateWhenAskedForTheDefaultClient()
    {
        using var cert = _pki.IssueServer("localhost");
        await using var server = new LoopbackTlsServer(cert);
        var state = new BlindPhoneState(new InMemoryStore()) { CallCode = PublicCaCode("localhost", server.Port) };
        using var provider = Provider(state: state);

        using var viaFactory = ((IHttpClientFactory)provider).CreateClient("any");
        (await viaFactory.GetAsync($"https://localhost:{server.Port}/x")).StatusCode.Should().Be(HttpStatusCode.OK,
            "the app's default client follows the paired node's trust mode");

        using var selfSigned = TestPki.SelfSigned("localhost");
        await using var pinnedServer = new LoopbackTlsServer(selfSigned);
        var pinned = BlindCallCode.Create($"https://localhost:{pinnedServer.Port}", Guid.NewGuid(), SpkiPin.Of(selfSigned), NodeKey, BlindPairingSecret.New());
        using var pinnedClient = provider.GetClient(pinned);
        (await pinnedClient.GetAsync($"https://localhost:{pinnedServer.Port}/x")).StatusCode.Should().Be(HttpStatusCode.OK);

        // and back: the public-CA client was replaced, not leaked into the pinned one
        using var again = provider.GetClient(PublicCaCode("localhost", server.Port));
        (await again.GetAsync($"https://localhost:{server.Port}/x")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class InMemoryStore : IBlindPhoneStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Get(string key) => _values.GetValueOrDefault(key);

        public void Set(string key, string? value)
        {
            if (value is null) _values.Remove(key);
            else _values[key] = value;
        }
    }
}
