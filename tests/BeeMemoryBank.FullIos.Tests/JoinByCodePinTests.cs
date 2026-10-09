using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Cli.Tests;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// The phone's join by code (the Android app's <see cref="NodeSetupService"/>, linked into the iPhone app), run against a small TLS
/// server with a certificate nobody trusts: the node joined by a code is recorded with the key the code pinned, so the first sync
/// after the join can dial it. This replaces a test that read the source text for the assignment (week review F13): it would have
/// passed with the line in a dead branch; this one fails when the row does not carry the pin.
/// </summary>
public class JoinByCodePinTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private readonly X509Certificate2 _cert = CreateServerCertificate();

    public void Dispose() => _cert.Dispose();

    [Fact]
    public async Task TheNodeJoinedByCode_IsRecordedWithTheKeyTheCodePinned()
    {
        var hostId = Guid.NewGuid();
        await using var door = new PinnedJoinDoor(_cert, Token, new FakeJoinHost(hostId));
        var code = new JoinCode(door.Address, Token, SpkiPin.Of(_cert));

        await using var vault = await TestVault.PrepareAsync("join-pin");
        using (var scope = vault.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<NodeSetupService>().JoinAsync("Phone", door.Address, TestVault.Password, code);

        door.Paths.Should().Contain("/api/join").And.Contain("/api/sync/snapshot/for-join");
        using var check = vault.Services.CreateScope();
        var row = await check.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(hostId);
        row.Should().NotBeNull("the node the phone joined through is a peer of the phone now");
        row!.ApiAddress.Should().Be(door.Address);
        row.TlsSpki.Should().Be(SpkiPin.Of(_cert), "the pinned key is what the phone dials that node by from now on; the certificate is self-signed");
    }

    [Fact]
    public async Task AnotherKeyAtTheCodesAddress_IsRefused_AndNothingIsRecorded()
    {
        var hostId = Guid.NewGuid();
        await using var door = new PinnedJoinDoor(_cert, Token, new FakeJoinHost(hostId));
        var code = new JoinCode(door.Address, Token, SpkiPin.Of(CreateServerCertificate()));

        await using var vault = await TestVault.PrepareAsync("join-wrongpin");
        using (var scope = vault.Services.CreateScope())
        {
            var join = () => scope.ServiceProvider.GetRequiredService<NodeSetupService>().JoinAsync("Phone", door.Address, TestVault.Password, code);
            await join.Should().ThrowAsync<Exception>();
        }

        door.Paths.Should().BeEmpty("a client that does not recognize the key sends nothing, the password included");
        using var check = vault.Services.CreateScope();
        (await check.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(hostId)).Should().BeNull();
    }

    /// <summary>A TLS server answering <c>POST /api/join</c> (403 without the expected token) and the join's sync requests like <see cref="FakeJoinHost"/>.</summary>
    private sealed class PinnedJoinDoor : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _cert;
        private readonly string _expectedToken;
        private readonly string _joinResponse;
        private readonly FakeJoinHost _host;
        private readonly Task _loop;
        private readonly ConcurrentQueue<string> _paths = new();

        public IReadOnlyCollection<string> Paths => _paths.ToArray();
        public string Address { get; }

        public PinnedJoinDoor(X509Certificate2 cert, string expectedToken, FakeJoinHost host)
        {
            _cert = cert;
            _expectedToken = expectedToken;
            _host = host;
            _joinResponse = host.JoinResponseJson(TestVault.Password);
            _listener.Start();
            Address = $"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _loop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var ssl = new SslStream(client.GetStream(), false);
                    await ssl.AuthenticateAsServerAsync(_cert);
                    var (path, headers) = await ReadRequestAsync(ssl);
                    headers.TryGetValue("x-bmb-join-token", out var token);
                    _paths.Enqueue(path);

                    string status;
                    byte[] bytes;
                    var extraHeaders = "";
                    if (path != "/api/join" && _host.Answer(path) is { } sync)
                    {
                        status = sync.Status == 200 ? "200 OK" : $"{sync.Status} Error";
                        bytes = sync.Body;
                        extraHeaders = string.Concat(sync.Headers.Select(h => $"{h.Key}: {h.Value}\r\n"));
                    }
                    else
                    {
                        (status, var body) = token == _expectedToken
                            ? ("200 OK", _joinResponse)
                            : ("403 Forbidden", JsonSerializer.Serialize(new { error = "This join code is not valid." }));
                        bytes = Encoding.UTF8.GetBytes(body);
                    }
                    var head = $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\n{extraHeaders}Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await ssl.WriteAsync(bytes);
                    await ssl.FlushAsync();
                }
                catch (Exception)
                {
                    // A client that refused the certificate, or hung up: nothing was read, nothing is recorded.
                }
            }
        }

        private static async Task<(string Path, Dictionary<string, string> Headers)> ReadRequestAsync(Stream stream)
        {
            var buffer = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(one) == 0) throw new IOException("closed");
                buffer.Add(one[0]);
                var n = buffer.Count;
                if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n') break;
            }
            var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var path = lines[0].Split(' ')[1];
            var headers = lines.Skip(1).Select(l => l.Split(':', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());
            if (headers.TryGetValue("content-length", out var len) && int.TryParse(len, out var length))
            {
                var body = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var got = await stream.ReadAsync(body.AsMemory(read));
                    if (got == 0) break;
                    read += got;
                }
            }
            return (path, headers);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop; } catch { }
            _stop.Dispose();
        }
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=bmb-fullios-door-test", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }
}
