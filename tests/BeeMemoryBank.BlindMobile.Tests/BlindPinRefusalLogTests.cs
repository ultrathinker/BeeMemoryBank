using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// On the phone, a listening node that presents ANOTHER certificate than the one pinned at pairing (a man in the middle,
/// or a replaced server) was refused, but the log only said "First load failed: Connection failure" - the same words as
/// for a node that is simply off. A pin refusal is a security event and has its own line. Real TLS on loopback.
/// </summary>
public sealed class BlindPinRefusalLogTests
{
    [Fact]
    public async Task AServerWithAnotherKeyThanThePinned_IsRefused_AndTheLogSaysThePinnedKeyDidNotMatch()
    {
        using var genuine = NewCertificate();
        using var impostor = NewCertificate();
        await using var server = new TinyTlsServer(impostor);
        var log = NewLog();
        using var provider = new BlindHttpClientProvider(new BlindPhoneState(new InMemoryStore()), host => BlindRunReport.Record(log, "pin", $"Refused {host}: it answered with another key than the pinned one."));

        using var client = provider.GetClient(explicitPin: SpkiPin.Of(genuine));
        var call = async () => await client.GetAsync($"https://127.0.0.1:{server.Port}/api/blind/replica");

        await call.Should().ThrowAsync<HttpRequestException>("the key is not the pinned one");
        var line = log.Latest(10).Should().ContainSingle().Which;
        line.Kind.Should().Be("pin");
        line.Message.Should().Contain($"127.0.0.1:{server.Port}", "the line names the node that was refused");
        server.Requests.Should().Be(0, "nothing may be sent to a node whose key does not match");
    }

    [Fact]
    public async Task AServerWithThePinnedKey_IsReached_AndNothingIsLoggedAboutPins()
    {
        using var genuine = NewCertificate();
        await using var server = new TinyTlsServer(genuine);
        var log = NewLog();
        using var provider = new BlindHttpClientProvider(new BlindPhoneState(new InMemoryStore()), host => BlindRunReport.Record(log, "pin", $"Refused {host}: it answered with another key than the pinned one."));

        using var client = provider.GetClient(explicitPin: SpkiPin.Of(genuine));
        var response = await client.GetAsync($"https://127.0.0.1:{server.Port}/ok");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        log.Latest(10).Should().BeEmpty();
    }

    private static X509Certificate2 NewCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=bmb-test", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(30));
        // SslStream on Windows needs a key with a persisted handle: round-trip through PFX.
        return new X509Certificate2(created.Export(X509ContentType.Pfx), (string?)null);
    }

    private static BlindPhoneLog NewLog() =>
        new(Path.Combine(Path.GetTempPath(), "bmb-pinlog-" + Guid.NewGuid().ToString("N") + ".jsonl"), TimeProvider.System);

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

    /// <summary>Accepts TLS connections with the certificate it was given and answers every request with 200.</summary>
    private sealed class TinyTlsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _stop = new();
        private int _requests;

        public TinyTlsServer(X509Certificate2 certificate)
        {
            _certificate = certificate;
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Requests => Volatile.Read(ref _requests);

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
                    await tls.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray());
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
}
