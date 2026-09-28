using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Plan 4.4: a peer whose whitelist row carries tls_spki (a blind node's self-signed certificate)
/// is accepted on exactly that key — whatever the chain says — and every other host is checked
/// the ordinary way.
/// </summary>
public class SpkiPinningTests : SyncTestFixture
{
    private SpkiPinRegistry _pins = null!;
    private X509Certificate2 _blindCert = null!;
    private X509Certificate2 _otherCert = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _blindCert = SelfSigned();
        _otherCert = SelfSigned();
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = BlindNodeId.NewId(), DisplayName = "Blind", Ed25519PublicKey = new byte[32],
            ApiAddress = "https://blind.lan:5610", TlsSpki = Spki.Of(_blindCert), Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        _pins = new SpkiPinRegistry(new ServiceCollection()
            .AddSingleton<IWhitelistRepository>(WhitelistRepo)
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
    }

    /// <summary>
    /// Review L-stage1 #5: the handler every sync client uses never follows a redirect itself...
    /// </summary>
    [Fact]
    public void TheSyncHandler_DoesNotFollowRedirects()
    {
        var handler = (SpkiPinGuardHandler)_pins.CreateHandler();

        ((HttpClientHandler)handler.InnerHandler!).AllowAutoRedirect.Should().BeFalse();
    }

    /// <summary>
    /// ...and a redirect a pinned peer answers with — here to an unpinned host whose certificate a CA
    /// would accept — is an error, with nothing sent to where it points.
    /// </summary>
    [Fact]
    public async Task ARedirectFromAPinnedPeer_IsAnError_AndIsNotFollowed()
    {
        var wire = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.TemporaryRedirect)
        {
            Headers = { Location = new Uri("https://ca-valid.example.com/api/sync/identity") }
        });
        using var http = new HttpClient(new SpkiPinGuardHandler(_pins, wire));

        var act = () => http.GetAsync("https://blind.lan:5610/api/sync/identity");

        await act.Should().ThrowAsync<HttpRequestException>();
        wire.Sent.Should().Equal("https://blind.lan:5610/api/sync/identity");
    }

    /// <summary>Plain HTTP would skip the certificate check where the pin lives: refused before sending.</summary>
    [Fact]
    public async Task PlainHttpToAPinnedPeer_IsRefused_BeforeAnythingIsSent()
    {
        var wire = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        using var http = new HttpClient(new SpkiPinGuardHandler(_pins, wire));

        var act = () => http.GetAsync("http://blind.lan:5610/api/sync/identity");

        await act.Should().ThrowAsync<HttpRequestException>();
        wire.Sent.Should().BeEmpty();
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add(request.RequestUri!.ToString());
            return Task.FromResult(answer(request));
        }
    }

    [Fact]
    public void PinnedPeer_IsAcceptedOnItsKey_DespiteAnUntrustedChain() =>
        _pins.Validate(Get("https://blind.lan:5610/api/sync/identity"), _blindCert, SslPolicyErrors.RemoteCertificateChainErrors)
            .Should().BeTrue();

    [Fact]
    public void PinnedPeer_IsRefusedOnAnyOtherKey_EvenWithAValidChain() =>
        _pins.Validate(Get("https://blind.lan:5610/api/sync/identity"), _otherCert, SslPolicyErrors.None)
            .Should().BeFalse("a pinned peer presenting another key is someone else");

    [Fact]
    public void UnpinnedHost_GetsOrdinaryValidation()
    {
        _pins.Validate(Get("https://hub.example/api/sync/identity"), _otherCert, SslPolicyErrors.None).Should().BeTrue();
        _pins.Validate(Get("https://hub.example/api/sync/identity"), _otherCert, SslPolicyErrors.RemoteCertificateChainErrors)
            .Should().BeFalse("no pin means no exception to the ordinary rules");
    }

    [Fact]
    public void AnotherPortOnTheSameHost_IsNotPinned() =>
        _pins.Validate(Get("https://blind.lan:443/"), _blindCert, SslPolicyErrors.RemoteCertificateChainErrors)
            .Should().BeFalse();

    [Fact]
    public void ExplicitPin_OnTheRequest_DecidesBeforeAnyRowExists()
    {
        var req = Get("https://new-blind.lan:5610/api/sync/identity");
        req.Options.Set(SpkiPinRegistry.ExplicitPin, Spki.Of(_otherCert));

        _pins.Validate(req, _otherCert, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue();
        _pins.Validate(req, _blindCert, SslPolicyErrors.None).Should().BeFalse();
    }

    private static HttpRequestMessage Get(string url) => new(HttpMethod.Get, url);

    private static X509Certificate2 SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new CertificateRequest("CN=test", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
