using System.Net;
using System.Net.Sockets;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// The peers <c>bmb join</c> inherits from the host's whitelist keep the way the host dials them: a peer in <c>pin</c> mode
/// stays pinned, so this node checks it by its key and not through the public CAs a self-signed peer never passes.
/// </summary>
public class JoinCommandPeerTlsTests : IDisposable
{
    private const string Password = "cliJoinPeerTlsPassword";

    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "bmb_cli_join_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Join_InheritedPeers_KeepTheirPin_AndAnUnusablePinLeavesThePeerOut()
    {
        var pinned = Guid.NewGuid();
        var publicCa = Guid.NewGuid();
        var oldStyle = Guid.NewGuid();
        var damaged = Guid.NewGuid();
        var pin = FakeJoinHost.NewPin();
        var host = new FakeJoinHost(Guid.NewGuid());
        var answer = host.JoinResponseJson(Password, whitelist:
        [
            FakeJoinHost.Peer(pinned, "https://peer-b.example:5301", "pin", pin),
            FakeJoinHost.Peer(publicCa, "https://peer-c.example", "public-ca"),
            FakeJoinHost.Peer(oldStyle, "https://peer-d.example"),
            FakeJoinHost.Peer(damaged, "https://peer-e.example", "pin", "not-a-pin")
        ]);

        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var serve = ServeJoinAsync(listener, host, answer);

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, $"http://localhost:{port}", Password, "CliJoiner", output: output);
        // A join that stopped early must fail this test with its output, not wait for requests that never come.
        listener.Stop();
        try { await serve; } catch (HttpListenerException) when (rc != 0) { }
        rc.Should().Be(0, output.ToString());

        await using var services = await CliServiceProvider.CreateAsync(_tempDir);
        using var scope = services.CreateScope();
        var rows = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var b = (await rows.GetByNodeIdAsync(pinned))!;
        (b.TlsTrust, b.TlsSpki).Should().Be((BlindTrust.Pin, pin));
        var c = (await rows.GetByNodeIdAsync(publicCa))!;
        (c.TlsTrust, c.TlsSpki).Should().Be((BlindTrust.PublicCa, null));
        var d = (await rows.GetByNodeIdAsync(oldStyle))!;
        (d.TlsTrust, d.TlsSpki).Should().Be((null, null), "a host that sends no pin information leaves the peer as it always was");
        (await rows.GetByNodeIdAsync(damaged)).Should().BeNull("a pin that is not a pin must not become an unpinned peer");
    }

    /// <summary>The join (<c>/api/join</c> first), then the snapshot's three requests; done once the snapshot is served.</summary>
    private static async Task ServeJoinAsync(HttpListener listener, FakeJoinHost host, string joinBody)
    {
        var path = "";
        for (var request = 0; request < 4 && path != "/api/sync/snapshot/for-join"; request++)
        {
            var ctx = await listener.GetContextAsync();
            path = ctx.Request.Url!.AbsolutePath;
            if (request == 0) path.Should().Be("/api/join");
            using (var reader = new StreamReader(ctx.Request.InputStream)) await reader.ReadToEndAsync();
            var (status, headers, bytes) = path == "/api/join"
                ? (200, new Dictionary<string, string>(), System.Text.Encoding.UTF8.GetBytes(joinBody))
                : host.Answer(path) ?? (404, new Dictionary<string, string>(), []);
            ctx.Response.StatusCode = status;
            foreach (var (name, value) in headers) ctx.Response.Headers[name] = value;
            ctx.Response.ContentType = path == "/api/sync/snapshot/for-join" ? "application/gzip" : "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
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
