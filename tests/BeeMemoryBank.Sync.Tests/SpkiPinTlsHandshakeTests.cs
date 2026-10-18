using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The pinned sync handler over a REAL TLS handshake. The other pin tests call <c>Validate</c> or use plain HTTP, so none of them
/// noticed that the certificate callback of a <see cref="SocketsHttpHandler"/> is given the SslStream as its sender, not the request:
/// the first version of the checked connect cast the sender to a request and every https call of a node failed with an InvalidCastException.
/// </summary>
public class SpkiPinTlsHandshakeTests : SyncTestFixture, IDisposable
{
    private readonly List<TcpListener> _listeners = [];

    public void Dispose()
    {
        foreach (var l in _listeners) l.Stop();
    }

    private SpkiPinRegistry Registry() => new(new ServiceCollection()
        .AddSingleton<IWhitelistRepository>(WhitelistRepo)
        .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

    [Fact]
    public async Task APinnedPeer_ThatPresentsItsKey_IsReached()
    {
        var cert = SelfSigned();
        var port = Serve(cert);
        await Pin(port, cert);
        using var http = new HttpClient(Registry().CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };

        var body = await http.GetStringAsync($"https://127.0.0.1:{port}/api/sync/identity");

        body.Should().Be("ok");
    }

    [Fact]
    public async Task APinnedPeer_ThatPresentsAnotherKey_IsRefused()
    {
        var pinned = SelfSigned();
        var port = Serve(SelfSigned());
        await Pin(port, pinned);
        using var http = new HttpClient(Registry().CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };

        var act = () => http.GetStringAsync($"https://127.0.0.1:{port}/api/sync/identity");

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        thrown.ToString().Should().NotContain("InvalidCast", "a refusal is the certificate check saying no, not the check failing to run");
    }

    [Fact]
    public async Task APeerWithNoPin_ThatPresentsASelfSignedCertificate_IsRefusedByTheOrdinaryCheck()
    {
        var port = Serve(SelfSigned());
        using var http = new HttpClient(Registry().CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };

        var act = () => http.GetStringAsync($"https://127.0.0.1:{port}/api/sync/identity");

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        thrown.ToString().Should().NotContain("InvalidCast");
    }

    [Fact]
    public async Task TheKeyOnTheRequest_IsEnoughBeforeAnyRowExists_AndAnotherKeyOnTheRequestIsRefused()
    {
        var cert = SelfSigned();
        var port = Serve(cert);
        using var http = new HttpClient(Registry().CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };

        var good = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}/x");
        good.Options.Set(SpkiPinRegistry.ExplicitPin, Spki.Of(cert));
        using var reached = await http.SendAsync(good);
        (await reached.Content.ReadAsStringAsync()).Should().Be("ok");

        var bad = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}/x");
        bad.Options.Set(SpkiPinRegistry.ExplicitPin, Spki.Of(SelfSigned()));
        var act = () => http.SendAsync(bad);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public void ALane_IsKeyedByTheExactPin_NotByItsLetterCase_AndNoPinIsNotAnEmptyPin()
    {
        var uri = new Uri("https://Peer.Example:5610/api/sync/identity");

        SpkiPinRegistry.LaneKey(uri, "AbCdEf").Should().NotBe(SpkiPinRegistry.LaneKey(uri, "abcdef"), "base64url pins are case sensitive");
        SpkiPinRegistry.LaneKey(uri, null).Should().NotBe(SpkiPinRegistry.LaneKey(uri, ""));
        SpkiPinRegistry.LaneKey(uri, "AbCdEf").Should().Be(SpkiPinRegistry.LaneKey(new Uri("https://peer.example:5610/x"), "AbCdEf"), "the host is compared in lower case, the path is no part of it");
        SpkiPinRegistry.LaneKey(uri, "AbCdEf").Should().NotBe(SpkiPinRegistry.LaneKey(new Uri("https://peer.example:443/x"), "AbCdEf"), "another port is another peer");
    }

    [Fact]
    public async Task TwoRequestsRacingForANewLane_BothGetAnAnswer()
    {
        var cert = SelfSigned();
        var port = Serve(cert);
        await Pin(port, cert);
        using var http = new HttpClient(Registry().CreateHandler()) { Timeout = TimeSpan.FromSeconds(30) };

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => http.GetStringAsync($"https://127.0.0.1:{port}/x")));

        answers.Should().OnlyContain(a => a == "ok");
    }

    private async Task Pin(int port, X509Certificate2 cert) =>
        await WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = BlindNodeId.NewId(), DisplayName = "Pinned", Ed25519PublicKey = new byte[32],
            ApiAddress = $"https://127.0.0.1:{port}", TlsSpki = Spki.Of(cert), Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

    /// <summary>A loopback TLS server: answers every request 200 "ok" once the handshake went through.</summary>
    private int Serve(X509Certificate2 cert)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            using var tls = new SslStream(client.GetStream());
                            await tls.AuthenticateAsServerAsync(cert);
                            var buffer = new byte[4096];
                            while (await tls.ReadAsync(buffer) > 0)
                                await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
                        }
                        catch (Exception) { }
                    }
                });
            }
        });
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);   // a certificate with its key, usable by a server on every platform
    }
}
