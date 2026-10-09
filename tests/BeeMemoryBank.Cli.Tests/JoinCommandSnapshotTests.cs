using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// BMB-81: <c>bmb join</c> takes the host's snapshot like the Setup page and the phone do, records the pull position at
/// its checkpoint and the clock past it, and when anything fails says why, exits non-zero and leaves no half-made node.
/// The host is a local HTTP listener (<see cref="FakeJoinHost"/>); the real host and a compacted log are in the
/// integration tests (CliJoinTests).
/// </summary>
public class JoinCommandSnapshotTests : IDisposable
{
    private const string Password = "cliJoinSnapshotPassword";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "bmb_cli_joinsnap_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Join_TakesTheSnapshot_RecordsThePullPosition_TheClock_AndTheInitialSync()
    {
        var host = new FakeJoinHost(Guid.NewGuid(), cpSeq: 42, lamportTs: 4_242);
        using var server = new Host(path => path == "/api/join" ? Ok(host.JoinResponseJson(Password)) : host.Answer(path));

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().Be(0, output.ToString());
        output.ToString().Should().Contain("checkpoint 42");
        server.Paths.Should().ContainInOrder("/api/join", "/api/sync/challenge", "/api/sync/authenticate", "/api/sync/snapshot/for-join");

        await using var services = await CliServiceProvider.CreateAsync(_tempDir);
        using var scope = services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>().GetAsync(host.HostId))!
            .LastSequenceNum.Should().Be(42, "the first sync continues after the snapshot, not from 0");
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!
            .InitialSyncCompleted.Should().BeTrue();
        (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxLamportTimestampAsync())
            .Should().BeGreaterThanOrEqualTo(4_242, "the clock the next start begins from is past the imported rows");
    }

    [Fact]
    public async Task Join_WhenTheSnapshotDoesNotVerify_FailsNonZero_LeavesNoNode_AndCanBeRunAgain()
    {
        var forged = new FakeJoinHost(Guid.NewGuid(), badSignature: true);
        using (var server = new Host(path => path == "/api/join" ? Ok(forged.JoinResponseJson(Password)) : forged.Answer(path)))
        {
            var output = new StringWriter();
            var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

            rc.Should().NotBe(0);
            output.ToString().Should().Contain("signature verification failed").And.Contain("can be run again").And.NotContain(Password);
            // The host cannot be told: say which row to revoke there, and that notes already copied stay (nothing here claims "nothing was kept").
            output.ToString().Should().Contain("still lists this node ('CliJoiner'").And.Contain("revoke it").And.NotContain("Nothing was kept");
        }

        await using (var services = await CliServiceProvider.CreateAsync(_tempDir))
        {
            using var scope = services.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().BeNull("no half-made node");
            (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync()).Should().BeEmpty();
            (await scope.ServiceProvider.GetRequiredService<IKeySlotRepository>().GetAllAsync()).Should().BeEmpty();
        }

        var good = new FakeJoinHost(Guid.NewGuid());
        using var retry = new Host(path => path == "/api/join" ? Ok(good.JoinResponseJson(Password)) : good.Answer(path));
        var again = new StringWriter();
        (await JoinCommand.HandleAsync(_tempDir, retry.Url, Password, "CliJoiner", output: again))
            .Should().Be(0, "the failed join left nothing that stops the next one: " + again);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    public async Task Join_ToAHostOnAnotherProtocol_IsRefused_BeforeAnythingIsWritten(int hostProtocol)
    {
        var host = new FakeJoinHost(Guid.NewGuid());
        using var server = new Host(path => path == "/api/join" ? Ok(host.JoinResponseJson(Password, hostProtocol)) : host.Answer(path));

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().Be(1);
        output.ToString().Should().Contain($"protocol version ({hostProtocol})");
        // Nothing is written here, but the host already wrote this node's row, so it is asked to take it back (JoinCommandAbortTests).
        server.Paths.Should().Equal("/api/join", "/api/join/abort");
        await using var services = await CliServiceProvider.CreateAsync(_tempDir);
        using var scope = services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Join_ToABlindNode_SaysToJoinANodeThatHoldsTheData()
    {
        var blindId = BlindNodeId.NewId();
        using var server = new Host(path => path == "/api/sync/challenge"
            ? Ok(JsonSerializer.Serialize(new { challenge = "AAAA", serverNodeId = blindId }))
            : null);

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().Be(1);
        output.ToString().Should().Contain("blind node").And.Contain("Join a node that holds the data");
    }

    private static (int, IReadOnlyDictionary<string, string>, byte[])? Ok(string json) =>
        (200, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(json));

    /// <summary>A local HTTP host answering by path (404 for null); records the paths it was asked for.</summary>
    private sealed class Host : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<string, (int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)?> _answer;
        private readonly Task _loop;
        private readonly List<string> _paths = new();

        public Host(Func<string, (int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)?> answer)
        {
            _answer = answer;
            var port = FreePort();
            Url = $"http://localhost:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string Url { get; }

        public IReadOnlyList<string> Paths
        {
            get { lock (_paths) return _paths.ToList(); }
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                var path = ctx.Request.Url!.AbsolutePath;
                lock (_paths) _paths.Add(path);
                using (var reader = new StreamReader(ctx.Request.InputStream)) await reader.ReadToEndAsync();
                var (status, headers, body) = _answer(path) ?? (404, new Dictionary<string, string>(), []);
                ctx.Response.StatusCode = status;
                foreach (var (name, value) in headers) ctx.Response.Headers[name] = value;
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch { }
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
}
