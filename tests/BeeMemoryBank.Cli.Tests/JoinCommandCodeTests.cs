using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// Task P7 for <c>bmb join --code</c>: the join code another computer's Connect a device card shows replaces the address, the join
/// goes only to the server holding the key the code pins, <c>/api/join</c> carries the code's one-time token, and the pinned key is
/// recorded on the host's whitelist row so the later sync can dial it. The other computer is a small TLS server that behaves like the
/// node's join door (a certificate nobody trusts, a token check) and records what reached it.
/// </summary>
public class JoinCommandCodeTests : IDisposable
{
    private const string Password = "cliJoinCodePassword1";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "bmb_cli_joincode_" + Guid.NewGuid().ToString("N"));
    private readonly X509Certificate2 _cert = CreateServerCertificate();

    public void Dispose()
    {
        _cert.Dispose();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Join_WithTheCode_SendsTheToken_AndRecordsThePinnedKeyOnTheHostsRow()
    {
        var hostId = Guid.NewGuid();
        await using var door = new TlsDoor(_cert, expectedToken: Token, JoinResponseJson(hostId));
        var code = new JoinCode(door.Address, Token, SpkiPin.Of(_cert)).ToString();

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, remoteUrl: "", Password, "CliJoiner", output: output, joinCode: code);

        rc.Should().Be(0, output.ToString());
        door.Requests.Should().ContainSingle().Which.Token.Should().Be(Token, "the code's token travels in X-BMB-Join-Token");
        await using var services = await CliServiceProvider.CreateAsync(_tempDir);
        using var scope = services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(hostId);
        row!.ApiAddress.Should().Be(door.Address);
        row.TlsSpki.Should().Be(SpkiPin.Of(_cert), "the pinned key is what the node dials that computer by from now on");
    }

    [Fact]
    public async Task Join_WithTheCode_TheTypedAddressIsIgnored()
    {
        await using var door = new TlsDoor(_cert, Token, JoinResponseJson(Guid.NewGuid()));
        var code = new JoinCode(door.Address, Token, SpkiPin.Of(_cert)).ToString();

        var rc = await JoinCommand.HandleAsync(_tempDir, remoteUrl: "https://elsewhere.invalid", Password, "CliJoiner",
            output: new StringWriter(), joinCode: code);

        rc.Should().Be(0);
        door.Requests.Should().HaveCount(1, "the join went to the code's address, not to what was typed");
    }

    [Fact]
    public async Task WrongPin_SendsNoRequestAtAll_AndSaysTheOtherComputerIsNotTheOne()
    {
        await using var door = new TlsDoor(_cert, Token, JoinResponseJson(Guid.NewGuid()));
        var elseKey = SpkiPin.Of(CreateServerCertificate());
        var code = new JoinCode(door.Address, Token, elseKey).ToString();

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, "", Password, "CliJoiner", output: output, joinCode: code);

        rc.Should().Be(1);
        output.ToString().Should().Contain("not the one the join code belongs to").And.NotContain(Password);
        door.Requests.Should().BeEmpty("the handshake fails on the pin before one request byte, the master password included, is sent");
    }

    [Fact]
    public async Task WrongToken_ShowsTheDoorsOwnSentence()
    {
        await using var door = new TlsDoor(_cert, expectedToken: Token, JoinResponseJson(Guid.NewGuid()));
        var code = new JoinCode(door.Address, OtherToken, SpkiPin.Of(_cert)).ToString();

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, "", Password, "CliJoiner", output: output, joinCode: code);

        rc.Should().Be(1);
        output.ToString().Should().Contain("403").And.Contain("This join code is not valid");
    }

    [Fact]
    public async Task ClosedDoor_SaysTheOtherComputerDidNotAnswer()
    {
        var port = FreePort();
        var code = new JoinCode($"https://127.0.0.1:{port}", Token, SpkiPin.Of(_cert)).ToString();

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, "", Password, "CliJoiner", output: output, joinCode: code);

        rc.Should().Be(1);
        output.ToString().Should().Contain("did not answer").And.Contain("15 minutes");
    }

    [Theory]
    [InlineData("bmb-join:?a=http%3A%2F%2F192.0.2.1&t=x&s=y")]
    [InlineData("https://192.0.2.1:5311")]
    [InlineData("bmb-join:?a=https%3A%2F%2F192.0.2.1%3A5311")]
    public async Task ADamagedCode_IsRefusedBeforeAnyConnection(string code)
    {
        var output = new StringWriter();

        var rc = await JoinCommand.HandleAsync(_tempDir, "", Password, "CliJoiner", output: output, joinCode: code);

        rc.Should().Be(2);
        output.ToString().Should().Contain("not valid").And.Contain("bmb-join:");
    }

    [Fact]
    public async Task NeitherAddressNorCode_SaysWhatToGive()
    {
        var output = new StringWriter();

        var rc = await JoinCommand.HandleAsync(_tempDir, "", Password, "CliJoiner", output: output);

        rc.Should().Be(2);
        output.ToString().Should().Contain("--remote").And.Contain("--code");
    }

    // ─── The other computer ─────────────────────────────────────────────────

    private const string Token = "2uTVKRgqdGVqPDxaGwLmhA";
    private const string OtherToken = "AAAAAAAAAAAAAAAAAAAAAA";

    private static string JoinResponseJson(Guid hostId)
    {
        var dek = MasterKeyManager.GenerateMasterDek();
        var salt = KeyDerivation.GenerateSalt();
        var (encDek, iv) = MasterKeyManager.WrapMasterDek(dek, KeyDerivation.DeriveKek(Password, salt));
        return JsonSerializer.Serialize(new
        {
            remoteNode = new
            {
                nodeId = hostId, displayName = "Host", protocolVersion = 2,
                ed25519PublicKeyB64 = Convert.ToBase64String(Ed25519Signer.GenerateKeyPair().publicKey)
            },
            keySlot = new
            {
                encryptedMasterDekB64 = Convert.ToBase64String(encDek),
                ivB64 = Convert.ToBase64String(iv),
                saltB64 = Convert.ToBase64String(salt),
                argonMemory = CryptoConstants.DefaultArgonMemory,
                argonIterations = CryptoConstants.DefaultArgonIterations,
                argonParallelism = CryptoConstants.DefaultArgonParallelism
            },
            whitelist = Array.Empty<object>()
        });
    }

    /// <summary>
    /// A TLS server that answers <c>POST /api/join</c> like the node's door: 403 with a sentence unless <c>X-BMB-Join-Token</c> is the
    /// expected one, the join response otherwise. It records every request that was read; a client that refuses the certificate at the
    /// handshake leaves no entry.
    /// </summary>
    private sealed class TlsDoor : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly X509Certificate2 _cert;
        private readonly string _expectedToken;
        private readonly string _joinResponse;
        private readonly Task _loop;

        public ConcurrentQueue<(string Path, string? Token)> RequestQueue { get; } = new();
        public IReadOnlyCollection<(string Path, string? Token)> Requests => RequestQueue.ToArray();
        public string Address { get; }

        public TlsDoor(X509Certificate2 cert, string expectedToken, string joinResponse)
        {
            _cert = cert;
            _expectedToken = expectedToken;
            _joinResponse = joinResponse;
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
                    RequestQueue.Enqueue((path, token));

                    var (status, body) = token == _expectedToken
                        ? ("200 OK", _joinResponse)
                        : ("403 Forbidden", JsonSerializer.Serialize(new { error = "This join code is not valid. Open Connect a device on the computer and use the code shown there." }));
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var head = $"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
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
        var req = new CertificateRequest("CN=bmb-cli-door-test", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
