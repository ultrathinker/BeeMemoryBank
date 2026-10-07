using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BeeMemoryBank.TestSupport;

/// <summary>
/// A throw-away certificate authority for tests of the public-CA trust mode (ADR 0007): a root, and server certificates it
/// issues. The root goes to the code under test as an extra trust anchor (<c>TlsTrustAnchors</c>) — the same shape as a CA a
/// user installed — so the production check runs unchanged: a real chain build and the real host-name check. Nothing in
/// production accepts an invalid certificate for a test's sake.
/// </summary>
public sealed class TestPki : IDisposable
{
    private readonly X509Certificate2 _root;

    public TestPki(string subject = "CN=BMB test root")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow.AddDays(60));
        _root = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    /// <summary>The root as the code under test gets it: the certificate only, no key.</summary>
    public X509Certificate2 RootCertificate => X509CertificateLoader.LoadCertificate(_root.RawData);

    /// <summary>The root as a trust-anchor collection (a fresh copy each time).</summary>
    public X509Certificate2Collection Anchors => [RootCertificate];

    /// <summary>A server certificate for <paramref name="dnsName"/> and the loopback addresses, signed by this root.</summary>
    public X509Certificate2 IssueServer(string dnsName = "localhost", DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(dnsName);
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        var serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7F;
        using var issued = request.Create(_root, notBefore ?? DateTimeOffset.UtcNow.AddHours(-1), notAfter ?? DateTimeOffset.UtcNow.AddDays(7), serial);
        using var withKey = issued.CopyWithPrivateKey(key);
        // SslStream on Windows needs a key with a persisted handle: round-trip through PFX.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    /// <summary>A self-signed server certificate for <paramref name="dnsName"/> and loopback: valid for nobody but a pin.</summary>
    public static X509Certificate2 SelfSigned(string dnsName = "localhost", DateTimeOffset? notAfter = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(dnsName);
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), notAfter ?? DateTimeOffset.UtcNow.AddDays(7));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    public void Dispose() => _root.Dispose();
}

/// <summary>
/// A TLS server on the loopback interface that answers every request with a fixed reply, and counts the requests it was
/// sent: a client that refuses the certificate ends the handshake, so the count stays at zero — which is how a test shows
/// that nothing was sent to a node it should not trust.
/// </summary>
public sealed class LoopbackTlsServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _response;
    private int _requests;

    /// <param name="response">The whole HTTP reply; by default 200 with a short body.</param>
    public LoopbackTlsServer(X509Certificate2 certificate, string? response = null)
    {
        _certificate = certificate;
        _response = response ?? "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok";
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int Requests => Volatile.Read(ref _requests);

    /// <summary>A reply that redirects to <paramref name="location"/>.</summary>
    public static string Redirect(string location, int status = 302) =>
        $"HTTP/1.1 {status} Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => ServeAsync(tcp));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient tcp)
    {
        using (tcp)
        {
            try
            {
                await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(_certificate);
                var buffer = new byte[4096];
                var read = await tls.ReadAsync(buffer);
                if (read == 0) return;
                Interlocked.Increment(ref _requests);
                await tls.WriteAsync(Encoding.ASCII.GetBytes(_response));
                await tls.FlushAsync();
            }
            catch (Exception) when (!_stop.IsCancellationRequested)
            {
                // A client that refuses the certificate ends the handshake with an error here; that is the expected case.
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
