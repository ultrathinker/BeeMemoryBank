using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BeeMemoryBank.Crypto.Tests;

/// <summary>
/// The key pin a phone checks before it sends the master password to a node it joins without a hub
/// (plan section 10). The end-to-end tests stand up a real TLS server and record every byte of
/// application data it receives: with a wrong pin, not one byte — above all not the password.
/// </summary>
public class SpkiPinTests
{
    private const string PasswordMarker = "master-password-3f9a";

    [Fact]
    public void Of_IsTheBase64UrlSha256OfTheSubjectPublicKeyInfo()
    {
        using var cert = CreateCertificate();

        var expected = Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        SpkiPin.Of(cert).Should().Be(expected).And.HaveLength(43);
    }

    [Fact]
    public void Matches_OnlyThePinnedKey()
    {
        using var a = CreateCertificate();
        using var b = CreateCertificate();

        SpkiPin.Matches(a, SpkiPin.Of(a)).Should().BeTrue();
        SpkiPin.Matches(a, SpkiPin.Of(b)).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 at all!")]
    [InlineData("AAAA")] // decodes, but is not a SHA-256
    public void Matches_NeverForAMalformedPin(string? pin)
    {
        using var cert = CreateCertificate();
        SpkiPin.Matches(cert, pin).Should().BeFalse();
    }

    [Fact]
    public void Matches_NeverWithoutACertificate() =>
        SpkiPin.Matches(null, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA").Should().BeFalse();

    [Fact]
    public async Task PinnedHandler_WrongPin_RefusesTheHandshake_AndNoPasswordByteLeaves()
    {
        using var serverCert = CreateCertificate();
        using var otherCert = CreateCertificate();
        await using var server = await RecordingTlsServer.StartAsync(serverCert);

        using var client = new HttpClient(SpkiPin.CreatePinnedHandler(SpkiPin.Of(otherCert)));
        var send = () => client.PostAsync($"https://127.0.0.1:{server.Port}/api/join",
            new StringContent($"{{\"masterPassword\":\"{PasswordMarker}\"}}"));

        await send.Should().ThrowAsync<HttpRequestException>();
        (await server.ReceivedAsync()).Should().NotContain(PasswordMarker)
            .And.BeEmpty("the TLS handshake must fail before any request is written");
    }

    [Fact]
    public async Task PinnedHandler_RightPin_ConnectsWithoutAnyTrustedCa()
    {
        using var serverCert = CreateCertificate(); // self-signed, trusted by nobody
        await using var server = await RecordingTlsServer.StartAsync(serverCert);

        using var client = new HttpClient(SpkiPin.CreatePinnedHandler(SpkiPin.Of(serverCert)));
        var resp = await client.PostAsync($"https://127.0.0.1:{server.Port}/api/join",
            new StringContent($"{{\"masterPassword\":\"{PasswordMarker}\"}}"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await server.ReceivedAsync()).Should().Contain(PasswordMarker);
    }

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task PinnedHandler_RedirectToPlainHttp_IsRefused_AndThePasswordStaysPut(int status)
    {
        using var serverCert = CreateCertificate();
        await using var plain = await RecordingTlsServer.StartAsync(cert: null);
        await using var pinned = await RecordingTlsServer.StartAsync(serverCert,
            $"HTTP/1.1 {status} Redirect\r\nLocation: http://127.0.0.1:{plain.Port}/api/join\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        using var client = new HttpClient(SpkiPin.CreatePinnedHandler(SpkiPin.Of(serverCert)));
        var send = () => client.PostAsync($"https://127.0.0.1:{pinned.Port}/api/join",
            new StringContent($"{{\"masterPassword\":\"{PasswordMarker}\"}}"));

        await send.Should().ThrowAsync<HttpRequestException>("a pinned connection never follows a redirect");
        (await plain.ReceivedOrNothingAsync()).Should().NotContain(PasswordMarker,
            "the master password must never leave over plain http");
    }

    [Fact]
    public async Task PinnedHandler_RedirectToAnotherOrigin_IsRefused_EvenWithTheSameKey()
    {
        using var serverCert = CreateCertificate();
        await using var other = await RecordingTlsServer.StartAsync(serverCert);
        await using var pinned = await RecordingTlsServer.StartAsync(serverCert,
            $"HTTP/1.1 308 Permanent Redirect\r\nLocation: https://127.0.0.1:{other.Port}/api/join\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        using var client = new HttpClient(SpkiPin.CreatePinnedHandler(SpkiPin.Of(serverCert)));
        var send = () => client.PostAsync($"https://127.0.0.1:{pinned.Port}/api/join",
            new StringContent($"{{\"masterPassword\":\"{PasswordMarker}\"}}"));

        await send.Should().ThrowAsync<HttpRequestException>();
        (await other.ReceivedOrNothingAsync()).Should().NotContain(PasswordMarker,
            "the code named one origin; nothing is sent anywhere else");
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=bmb-pin-test", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // SChannel cannot serve an ephemeral key; a PFX round-trip gives it a usable one.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    /// <summary>
    /// A one-connection server (TLS with <c>cert</c>, plain without) that records the plaintext it read
    /// and answers with <c>response</c> (200 by default).
    /// </summary>
    private sealed class RecordingTlsServer : IAsyncDisposable
    {
        private const string Ok = "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        private readonly TcpListener _listener;
        private readonly Task<string> _received;

        private RecordingTlsServer(TcpListener listener, X509Certificate2? cert, string response)
        {
            _listener = listener;
            _received = ServeOneAsync(cert, response);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static Task<RecordingTlsServer> StartAsync(X509Certificate2? cert, string response = Ok)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new RecordingTlsServer(listener, cert, response));
        }

        public Task<string> ReceivedAsync() => _received.WaitAsync(TimeSpan.FromSeconds(30));

        /// <summary>What arrived, or "" if nobody connected within a moment.</summary>
        public async Task<string> ReceivedOrNothingAsync()
        {
            var done = await Task.WhenAny(_received, Task.Delay(TimeSpan.FromSeconds(2)));
            return done == _received ? await _received : "";
        }

        private async Task<string> ServeOneAsync(X509Certificate2? cert, string response)
        {
            using var tcp = await _listener.AcceptTcpClientAsync();
            Stream stream = tcp.GetStream();
            if (cert != null)
            {
                var tls = new SslStream(stream);
                try { await tls.AuthenticateAsServerAsync(cert); }
                catch (Exception ex) when (ex is IOException or AuthenticationException) { return ""; }
                stream = tls;
            }
            await using var _ = stream;

            var received = new StringBuilder();
            var buffer = new byte[8192];
            try
            {
                while (!received.ToString().Contains(PasswordMarker))
                {
                    var n = await stream.ReadAsync(buffer);
                    if (n == 0) break;
                    received.Append(Encoding.UTF8.GetString(buffer, 0, n));
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
            catch (IOException) { }
            return received.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try { await _received; } catch { }
        }
    }
}
