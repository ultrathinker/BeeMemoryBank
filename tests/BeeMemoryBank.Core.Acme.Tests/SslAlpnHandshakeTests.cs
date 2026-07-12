using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.Core.Services.Acme;
using Certes;

namespace BeeMemoryBank.Core.Acme.Tests;

/// <summary>
/// End-to-end offline proof that the TLS-ALPN-01 plumbing works: a challenge certificate built by
/// <see cref="TlsAlpn01CertificateBuilder"/> and served via <see cref="TlsAlpnChallengeResponder"/>
/// completes a real <c>SslStream</c> handshake negotiating the <c>acme-tls/1</c> ALPN protocol —
/// exactly what happens when the Let's Encrypt validator probes the listener. No network egress,
/// no ACME server: just two loopback SslStreams.
/// </summary>
public class SslAlpnHandshakeTests
{
    private const string Domain = "node.example.com";
    private static readonly SslApplicationProtocol AcmeTlsAlpn =
        new(TlsAlpn01CertificateBuilder.AcmeTlsAlpnProtocol);

    [Fact]
    public async Task ChallengeCert_ServedViaResponder_NegotiatesAcmeTlsAlpn()
    {
        // Real ACME key authorization from an offline ES256 account key.
        var accountKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var keyAuthz = accountKey.KeyAuthorization("dummy-token-123");

        // Build the challenge cert the same way the service does, and register it with the
        // responder as if a validation were in flight.
        using var challengeCert = TlsAlpn01CertificateBuilder.Build(Domain, keyAuthz);
        var responder = new TlsAlpnChallengeResponder();
        responder.SetChallenge(Domain, challengeCert);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var clientTask = Task.Run(() => ConnectClientAsync(port));

            using var tcpServer = await listener.AcceptTcpClientAsync();
            using var serverStream = new SslStream(tcpServer.GetStream(), leaveInnerStreamOpen: false);

            // The TLS listener answers the probe: serve whatever cert the responder has for the SNI,
            // and offer the acme-tls/1 application protocol.
            var servedCert = responder.TryGetChallengeCert(Domain);
            servedCert.Should().NotBeNull("a challenge cert must be registered during validation");

            // The builder emits an ephemeral (in-memory) key, which is correct for the single-use
            // challenge cert and works directly on Linux/OpenSSL. Windows SChannel, however, cannot
            // drive a server-side TLS handshake with an ephemeral key, so on this Windows test host
            // we reload the cert into a persisted key set. (This is purely a test-host accommodation;
            // see the task report's platform note for the production implication.)
            using var serverCert = ReloadForHostPlatform(servedCert!);

            await serverStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = serverCert,
                ApplicationProtocols = new List<SslApplicationProtocol> { AcmeTlsAlpn },
            });

            // The CA-side client connects requesting acme-tls/1.
            var (clientStream, presentedSubject) = await clientTask;
            await using var _ = clientStream;

            clientStream.NegotiatedApplicationProtocol.Should().Be(AcmeTlsAlpn,
                "the handshake must negotiate the acme-tls/1 ALPN protocol per RFC 8737 §4");
            serverStream.NegotiatedApplicationProtocol.Should().Be(AcmeTlsAlpn);

            // The cert actually presented over the wire must be the challenge cert (carries the SNI).
            presentedSubject.Should().Contain(Domain);
        }
        finally
        {
            responder.Clear();
            listener.Stop();
        }
    }

    private static async Task<(SslStream Stream, string PresentedSubject)> ConnectClientAsync(int port)
    {
        // TcpClient is intentionally not disposed here: its underlying socket is closed when the
        // returned SslStream (leaveInnerStreamOpen:false) is disposed by the caller.
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);

        string? presentedSubject = null;
        var stream = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = Domain,
            ApplicationProtocols = new List<SslApplicationProtocol> { AcmeTlsAlpn },
            RemoteCertificateValidationCallback = (_, cert, _, errors) =>
            {
                // Accept the self-signed challenge cert (validation probes do this), and capture
                // the leaf subject for the assertion.
                if (cert is X509Certificate2 c) presentedSubject = c.Subject;
                return true;
            },
        });

        return (stream, presentedSubject ?? "");
    }

    /// <summary>
    /// Reloads <paramref name="cert"/> into a persisted key set on Windows (SChannel cannot use
    /// ephemeral keys for server-side TLS). On Linux the original ephemeral cert is returned as-is.
    /// </summary>
    private static X509Certificate2 ReloadForHostPlatform(X509Certificate2 cert)
    {
        if (!OperatingSystem.IsWindows()) return cert;
        var pfx = cert.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, (string?)null, X509KeyStorageFlags.DefaultKeySet);
    }
}
