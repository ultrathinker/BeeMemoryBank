using System.Net;
using System.Net.Sockets;
using System.Text;
using BeeMemoryBank.Cli.Tests;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Mobile.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// The phone's join (<see cref="NodeSetupService.JoinAsync"/>, shared by the Android app and the iPhone full app) against a
/// fake host, on the app's own composition. What it records must be what <c>bmb join</c> and the Setup page record.
/// </summary>
public class PhoneJoinTests
{
    [Fact]
    public async Task APhoneThatJoinedFromASnapshot_RestartsWithItsClockPastTheImportedRows()
    {
        var host = new FakeJoinHost(Guid.NewGuid(), cpSeq: 42, lamportTs: 4_242);
        using var server = new MiniHost(path => path == "/api/join" ? Json(host.JoinResponseJson(TestVault.Password)) : host.Answer(path));

        var paths = new FullNodePaths(TestFolders.New("phone-join-lamport"));
        await using (var first = new ServiceCollection().AddFullNode(paths).BuildServiceProvider())
        {
            await FullNodeServices.PrepareAsync(first);
            using var scope = first.CreateScope();
            await scope.ServiceProvider.GetRequiredService<NodeSetupService>().JoinAsync("Phone", server.Url, TestVault.Password);
            first.GetRequiredService<LamportClock>().Current.Should().BeGreaterThanOrEqualTo(4_242, "in the process that joined, the clock was moved");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // A new process on the same folder: Android kills the app routinely, and the next start prepares it again. The event log
        // holds nothing of the imported rows, so only the durable floor can bring the clock back past them.
        await using var second = new ServiceCollection().AddFullNode(paths).BuildServiceProvider();
        await FullNodeServices.PrepareAsync(second);
        using var scope2 = second.CreateScope();
        (await scope2.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxLamportTimestampAsync())
            .Should().BeGreaterThanOrEqualTo(4_242, "a restarted phone must continue past the rows it imported (SnapshotJoin, LamportFloor)");
        second.GetRequiredService<LamportClock>().Current.Should().BeGreaterThanOrEqualTo(4_242);
    }

    [Fact]
    public async Task InheritedPeers_KeepTheKeyTheHostDialsThemBy_AndAPeerWithAnUnusablePinIsLeftOut()
    {
        var pinned = Guid.NewGuid();
        var publicCa = Guid.NewGuid();
        var oldStyle = Guid.NewGuid();
        var damaged = Guid.NewGuid();
        var pin = FakeJoinHost.NewPin();
        var host = new FakeJoinHost(Guid.NewGuid());
        var answer = host.JoinResponseJson(TestVault.Password, whitelist:
        [
            FakeJoinHost.Peer(pinned, "https://peer-b.example:5301", "pin", pin),
            FakeJoinHost.Peer(publicCa, "https://peer-c.example", "public-ca"),
            // a host of a version before 2.5.1 sends neither field
            FakeJoinHost.Peer(oldStyle, "https://peer-d.example"),
            FakeJoinHost.Peer(damaged, "https://peer-e.example", "pin", "not-a-pin")
        ]);
        using var server = new MiniHost(path => path == "/api/join" ? Json(answer) : host.Answer(path));

        await using var test = await TestVault.PrepareAsync("phone-join-peer-tls");
        using (var scope = test.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<NodeSetupService>().JoinAsync("Phone", server.Url, TestVault.Password);

        using var check = test.Services.CreateScope();
        var rows = check.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var b = (await rows.GetByNodeIdAsync(pinned))!;
        (b.TlsTrust, b.TlsSpki).Should().Be((BlindTrust.Pin, pin));
        var c = (await rows.GetByNodeIdAsync(publicCa))!;
        (c.TlsTrust, c.TlsSpki).Should().Be((BlindTrust.PublicCa, null));
        var d = (await rows.GetByNodeIdAsync(oldStyle))!;
        (d.TlsTrust, d.TlsSpki).Should().Be((null, null), "a host that sends no pin information leaves the peer as it always was");
        (await rows.GetByNodeIdAsync(damaged)).Should().BeNull(
            "recording it without its pin would have it checked through the public CAs instead of the key the host requires");

        // What the phone's sync actually asks: the pin registry's answer for each peer's address.
        var registry = new SpkiPinRegistry(test.Services.GetRequiredService<IServiceScopeFactory>());
        registry.PinFor(new Uri("https://peer-b.example:5301")).Should().Be(pin);
        registry.PinFor(new Uri("https://peer-c.example")).Should().BeNull();
        registry.PinFor(new Uri("https://peer-d.example")).Should().BeNull();
    }

    [Fact]
    public async Task AFailedJoin_LeavesNoNodeOnThePhone_AndSaysWhichRowToRevokeOnTheHost()
    {
        var forged = new FakeJoinHost(Guid.NewGuid(), badSignature: true);
        using var server = new MiniHost(path => path == "/api/join" ? Json(forged.JoinResponseJson(TestVault.Password)) : forged.Answer(path));

        await using var test = await TestVault.PrepareAsync("phone-join-failed");
        using var scope = test.Services.CreateScope();
        var act = () => scope.ServiceProvider.GetRequiredService<NodeSetupService>().JoinAsync("My phone", server.Url, TestVault.Password);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("signature verification failed")
            .And.Contain("still lists this phone ('My phone')", "the host recorded it when the key exchange worked and cannot be told")
            .And.Contain("revoke it").And.NotContain(TestVault.Password);
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().BeNull("a retry starts clean");
        (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedJoin_TellsTheHostToTakeTheRowBack_AndSaysSoInsteadOfAskingForARevoke()
    {
        var forged = new FakeJoinHost(Guid.NewGuid(), badSignature: true);
        using var server = new RecordingJoinHost(r => r.Path switch
        {
            "/api/join" => RecordingJoinHost.Reply(200, forged.JoinResponseJson(TestVault.Password)),
            "/api/join/abort" => RecordingJoinHost.Reply(200, "{\"aborted\":true,\"removed\":true}"),
            _ => forged.Answer(r.Path)
        });

        await using var test = await TestVault.PrepareAsync("phone-join-abort");
        using var scope = test.Services.CreateScope();
        var act = () => scope.ServiceProvider.GetRequiredService<NodeSetupService>().JoinAsync("My phone", server.Url, TestVault.Password);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("signature verification failed", "the original error stays the reason")
            .And.Contain("no longer lists this phone ('My phone')")
            .And.NotContain("revoke it").And.NotContain(TestVault.Password);

        var abort = server.Requests.Should().ContainSingle(r => r.Path == "/api/join/abort").Subject;
        using var sent = System.Text.Json.JsonDocument.Parse(abort.Body);
        sent.RootElement.GetProperty("masterPassword").GetString().Should().Be(TestVault.Password);
        using var join = System.Text.Json.JsonDocument.Parse(server.Requests.First(r => r.Path == "/api/join").Body);
        sent.RootElement.GetProperty("nodeId").GetGuid().Should().Be(join.RootElement.GetProperty("nodeId").GetGuid());
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().BeNull("a retry starts clean");
    }

    [Fact]
    public async Task AFailedJoin_AgainstAnOldHost_KeepsTheAccurateSentence()
    {
        var forged = new FakeJoinHost(Guid.NewGuid(), badSignature: true);
        using var server = new RecordingJoinHost(r => r.Path switch
        {
            "/api/join" => RecordingJoinHost.Reply(200, forged.JoinResponseJson(TestVault.Password)),
            "/api/join/abort" => null,
            _ => forged.Answer(r.Path)
        });

        await using var test = await TestVault.PrepareAsync("phone-join-abort-old-host");
        using var scope = test.Services.CreateScope();
        var act = () => scope.ServiceProvider.GetRequiredService<NodeSetupService>().JoinAsync("My phone", server.Url, TestVault.Password);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("signature verification failed").And.Contain("still lists this phone ('My phone')").And.Contain("revoke it");
        server.Paths.Should().Contain("/api/join/abort");
    }

    private async Task<(Exception Error, RecordingJoinHost Server, TestVault Test)> JoinWithAnswerAsync(string name, Func<FakeJoinHost, string> answer)
    {
        var host = new FakeJoinHost(Guid.NewGuid());
        var server = new RecordingJoinHost(r => r.Path switch
        {
            "/api/join" => RecordingJoinHost.Reply(200, answer(host)),
            "/api/join/abort" => RecordingJoinHost.Reply(200, "{\"aborted\":true,\"removed\":true}"),
            _ => host.Answer(r.Path)
        });
        var test = await TestVault.PrepareAsync(name);
        using var scope = test.Services.CreateScope();
        var act = () => scope.ServiceProvider.GetRequiredService<NodeSetupService>().JoinAsync("My phone", server.Url, TestVault.Password);
        var error = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        return (error, server, test);
    }

    [Theory]
    [InlineData("{\"remoteNode\":{\"nodeId\":")]
    [InlineData("this is not json")]
    public async Task AnAnswerOf200ThatIsNotReadable_TellsTheHostOnce_AndKeepsTheOriginalError(string body)
    {
        var (error, server, test) = await JoinWithAnswerAsync("phone-join-unreadable", _ => body);
        using var _s = server; await using var _t = test;

        error.Message.Should().Contain("The join could not be completed").And.Contain("no longer lists this phone ('My phone')")
            .And.NotContain("revoke it").And.NotContain(TestVault.Password);
        server.Paths.Should().Equal("/api/join", "/api/join/abort");
    }

    [Theory]
    [InlineData("keySlot", "encryptedMasterDekB64")]
    [InlineData("keySlot", "ivB64")]
    [InlineData("keySlot", "saltB64")]
    public async Task AKeySlotThatIsNotBase64_TellsTheHostOnce_AndKeepsTheOriginalError(string section, string field)
    {
        var (error, server, test) = await JoinWithAnswerAsync("phone-join-bad-slot",
            h => FakeJoinHost.WithField(h.JoinResponseJson(TestVault.Password), section, field, "***not base64***"));
        using var _s = server; await using var _t = test;

        error.Message.Should().Contain("The join could not be completed").And.Contain("Base-64")
            .And.Contain("no longer lists this phone ('My phone')").And.NotContain(TestVault.Password);
        server.Paths.Should().Equal("/api/join", "/api/join/abort");
    }

    [Fact]
    public async Task AHostKeyThatIsNotBase64_FailsTheLocalWrites_TheHostIsToldOnce_AndNoNodeIsLeft()
    {
        var (error, server, test) = await JoinWithAnswerAsync("phone-join-bad-host-key",
            h => FakeJoinHost.WithField(h.JoinResponseJson(TestVault.Password), "remoteNode", "ed25519PublicKeyB64", "***not base64***"));
        using var _s = server; await using var _t = test;

        error.Message.Should().Contain("Base-64").And.Contain("no longer lists this phone ('My phone')");
        server.Paths.Count(p => p == "/api/join/abort").Should().Be(1);
        using var scope = test.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().BeNull("a retry starts clean");
    }

    [Fact]
    public async Task AJoinTheHostRefused_AsksNothing()
    {
        using var server = new RecordingJoinHost(r => RecordingJoinHost.Reply(401, "{\"error\":\"Invalid master password\"}"));

        await using var test = await TestVault.PrepareAsync("phone-join-refused");
        using var scope = test.Services.CreateScope();
        var act = () => scope.ServiceProvider.GetRequiredService<NodeSetupService>().JoinAsync("My phone", server.Url, TestVault.Password);

        await act.Should().ThrowAsync<InvalidOperationException>();
        server.Paths.Should().Equal("/api/join");
    }

    [Fact]
    public async Task TheDownloadedSnapshot_IsStagedInTheDataFolderOwnerOnly_AndNothingIsLeftWhenTheJoinIsDone()
    {
        var host = new FakeJoinHost(Guid.NewGuid());
        var release = new TaskCompletionSource();
        using var server = new MiniHost(
            path => path == "/api/join" ? Json(host.JoinResponseJson(TestVault.Password)) : host.Answer(path),
            custom: async ctx =>
            {
                // The snapshot, half of it now and the rest when the test lets it go: the join is in the middle of its download.
                if (ctx.Request.Url!.AbsolutePath != "/api/sync/snapshot/for-join") return false;
                var (_, headers, body) = host.Answer("/api/sync/snapshot/for-join")!.Value;
                foreach (var (name, value) in headers) ctx.Response.Headers[name] = value;
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body.AsMemory(0, body.Length / 2));
                await ctx.Response.OutputStream.FlushAsync();
                await release.Task;
                await ctx.Response.OutputStream.WriteAsync(body.AsMemory(body.Length / 2));
                ctx.Response.Close();
                return true;
            });

        var paths = new FullNodePaths(TestFolders.New("phone-join-staging"));
        await using var services = new ServiceCollection().AddFullNode(paths).BuildServiceProvider();
        await FullNodeServices.PrepareAsync(services);
        var staging = SnapshotStaging.DirIn(paths.DataDirectory);

        using var scope = services.CreateScope();
        var join = Task.Run(() => scope.ServiceProvider.GetRequiredService<NodeSetupService>().JoinAsync("Phone", server.Url, TestVault.Password));

        string archive = "";
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !join.IsCompleted)
        {
            archive = Directory.Exists(staging) ? Directory.GetFiles(staging).FirstOrDefault(f => new FileInfo(f).Length > 0) ?? "" : "";
            if (archive != "") break;
            await Task.Delay(50);
        }
        archive.Should().NotBeEmpty("the archive being downloaded is a file in the data folder's staging folder, not in the OS temp folder");
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(archive).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite, "nobody else on the machine may read the vault being downloaded");

        release.SetResult();
        await join;
        Directory.GetFileSystemEntries(staging).Should().BeEmpty("the archive and the extracted database are removed once the import is done");
    }

    [Fact]
    public async Task WhatAKilledJoinLeftInTheStagingFolder_IsRemovedAtTheNextStart()
    {
        var paths = new FullNodePaths(TestFolders.New("phone-join-killed"));
        var archive = SnapshotStaging.NewArchive(paths.DataDirectory);
        File.WriteAllBytes(archive, [1, 2, 3]);
        var extracted = SnapshotStaging.NewDirectory(paths.DataDirectory);
        File.WriteAllText(Path.Combine(extracted, "beememorybank.db"), "the vault, in clear");

        await using var services = new ServiceCollection().AddFullNode(paths).BuildServiceProvider();
        await FullNodeServices.PrepareAsync(services);

        File.Exists(archive).Should().BeFalse();
        Directory.Exists(extracted).Should().BeFalse();
    }

    private static (int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body) Json(string body) =>
        (200, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(body));

    /// <summary>A plain-HTTP loopback host answering by path; the join takes its address as given in a test.</summary>
    private sealed class MiniHost : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<string, (int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)?> _answer;
        private readonly Task _loop;

        private readonly Func<HttpListenerContext, Task<bool>>? _custom;

        public MiniHost(Func<string, (int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)?> answer,
            Func<HttpListenerContext, Task<bool>>? custom = null)
        {
            _answer = answer;
            _custom = custom;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://localhost:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string Url { get; }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                using (var reader = new StreamReader(ctx.Request.InputStream)) await reader.ReadToEndAsync();
                // A request the test answers by hand (a response that is sent in two parts); it must not hold up the next request.
                if (_custom is not null && ctx.Request.Url!.AbsolutePath == "/api/sync/snapshot/for-join")
                {
                    _ = Task.Run(async () => { try { await _custom(ctx); } catch { /* the test ended */ } });
                    continue;
                }
                var (status, headers, body) = _answer(ctx.Request.Url!.AbsolutePath) ?? (404, new Dictionary<string, string>(), []);
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
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch { /* the listener is gone either way */ }
        }
    }
}
