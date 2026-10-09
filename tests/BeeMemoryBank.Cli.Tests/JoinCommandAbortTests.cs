using System.Net;
using System.Text.Json;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// A join that fails after the host wrote the joiner's row tells the host to take the row back (<c>POST /api/join/abort</c>), best
/// effort: an old host that does not know the route, a host that answers something else or not at all, keeps today's accurate sentence
/// ("the other computer still lists this node ... revoke it") and the original error.
/// </summary>
public class JoinCommandAbortTests : IDisposable
{
    private const string Password = "cliJoinAbortPassword";
    private const string Removed = "{\"aborted\":true,\"removed\":true}";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "bmb_cli_joinabort_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static (int, IReadOnlyDictionary<string, string>, byte[])? Ok(string json) => RecordingJoinHost.Reply(200, json);

    private static Func<SeenRequest, (int, IReadOnlyDictionary<string, string>, byte[])?> HostOf(
        FakeJoinHost host, Func<SeenRequest, (int, IReadOnlyDictionary<string, string>, byte[])?>? abort = null, int? protocol = null) =>
        r => r.Path switch
        {
            "/api/join" => Ok(host.JoinResponseJson(Password, protocol)),
            "/api/join/abort" => abort?.Invoke(r),
            _ => host.Answer(r.Path)
        };

    private static Guid NodeIdOf(string output) =>
        Guid.Parse(output.Split('\n').First(l => l.StartsWith("Generated nodeId: ")).Split(": ")[1].Trim());

    [Fact]
    public async Task AFailedSnapshot_TellsTheHostToTakeTheRowBack_WithTheProofAndTheKeyOfTheJoin()
    {
        var forged = new FakeJoinHost(Guid.NewGuid(), badSignature: true);
        using var server = new RecordingJoinHost(HostOf(forged, abort: _ => Ok(Removed)));

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().NotBe(0);
        var aborts = server.Requests.Where(r => r.Path == "/api/join/abort").ToList();
        aborts.Should().ContainSingle();
        aborts[0].Method.Should().Be("POST");
        using var sent = JsonDocument.Parse(aborts[0].Body);
        sent.RootElement.GetProperty("masterPassword").GetString().Should().Be(Password);
        sent.RootElement.GetProperty("nodeId").GetGuid().Should().Be(NodeIdOf(output.ToString()));
        using var join = JsonDocument.Parse(server.Requests.First(r => r.Path == "/api/join").Body);
        sent.RootElement.GetProperty("ed25519PublicKeyB64").GetString()
            .Should().Be(join.RootElement.GetProperty("ed25519PublicKeyB64").GetString());

        output.ToString().Should().Contain("signature verification failed", "the original error is still the first thing said")
            .And.Contain("no longer lists")
            .And.NotContain("revoke it").And.NotContain(Password);
        server.Paths.Last().Should().Be("/api/join/abort");
    }

    [Fact]
    public async Task AnOldHost_ThatAnswers404ToTheAbort_KeepsTheAccurateSentence()
    {
        var forged = new FakeJoinHost(Guid.NewGuid(), badSignature: true);
        using var server = new RecordingJoinHost(HostOf(forged, abort: null));

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().NotBe(0);
        server.Paths.Should().Contain("/api/join/abort");
        output.ToString().Should().Contain("signature verification failed").And.Contain("can be run again")
            .And.Contain("still lists this node ('CliJoiner'").And.Contain("revoke it");
    }

    [Theory]
    [InlineData(405)]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(500)]
    public async Task AHostOrProxyThatRefusesTheAbort_KeepsTheAccurateSentence(int status)
    {
        var forged = new FakeJoinHost(Guid.NewGuid(), badSignature: true);
        using var server = new RecordingJoinHost(HostOf(forged, abort: _ => RecordingJoinHost.Reply(status, "{\"error\":\"no\"}")));

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().NotBe(0);
        output.ToString().Should().Contain("still lists this node ('CliJoiner'").And.Contain("revoke it").And.NotContain("no longer lists");
    }

    [Fact]
    public async Task AHostThatDoesNotAnswerTheAbort_DoesNotHoldTheJoinUp_AndKeepsTheAccurateSentence()
    {
        var forged = new FakeJoinHost(Guid.NewGuid(), badSignature: true);
        using var server = new RecordingJoinHost(HostOf(forged, abort: _ =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(4));
            return RecordingJoinHost.Reply(200);
        }));

        var output = new StringWriter();
        var started = DateTime.UtcNow;
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output, requestTimeout: TimeSpan.FromSeconds(1));

        rc.Should().NotBe(0);
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(30));
        output.ToString().Should().Contain("signature verification failed").And.Contain("still lists this node ('CliJoiner'");
    }

    [Fact]
    public async Task AHostOnAnotherProtocol_IsTold_BecauseItAlreadyWroteTheRow()
    {
        var host = new FakeJoinHost(Guid.NewGuid());
        using var server = new RecordingJoinHost(HostOf(host, abort: _ => Ok(Removed), protocol: 99));

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().Be(1);
        output.ToString().Should().Contain("protocol version (99)");
        server.Paths.Should().Equal("/api/join", "/api/join/abort");
    }

    [Fact]
    public async Task AJoinTheHostRefused_HasNoRowToTakeBack_AndAsksNothing()
    {
        using var server = new RecordingJoinHost(r =>
            r.Path == "/api/join" ? RecordingJoinHost.Reply(401, "{\"error\":\"Invalid master password\"}") : null);

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().Be(1);
        server.Paths.Should().Equal("/api/join");
    }

    [Theory]
    [InlineData("{\"remoteNode\":{\"nodeId\":")]
    [InlineData("this is not json")]
    public async Task AnAnswerOf200ThatIsNotReadable_TellsTheHostOnce_AndKeepsTheOriginalError(string body)
    {
        using var server = new RecordingJoinHost(r =>
            r.Path == "/api/join" ? Ok(body) : r.Path == "/api/join/abort" ? Ok(Removed) : null);

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().Be(1);
        server.Paths.Should().Equal("/api/join", "/api/join/abort");
        output.ToString().Should().Contain("Error: the join could not be completed").And.Contain("no longer lists")
            .And.NotContain(Password);
    }

    [Theory]
    [InlineData("keySlot", "encryptedMasterDekB64")]
    [InlineData("keySlot", "ivB64")]
    [InlineData("keySlot", "saltB64")]
    public async Task AKeySlotThatIsNotBase64_TellsTheHostOnce_AndKeepsTheOriginalError(string section, string field)
    {
        var host = new FakeJoinHost(Guid.NewGuid());
        using var server = new RecordingJoinHost(r => r.Path switch
        {
            "/api/join" => Ok(FakeJoinHost.WithField(host.JoinResponseJson(Password), section, field, "***not base64***")),
            "/api/join/abort" => Ok(Removed),
            _ => host.Answer(r.Path)
        });

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().Be(1);
        server.Paths.Should().Equal("/api/join", "/api/join/abort");
        output.ToString().Should().Contain("Error: the join could not be completed").And.Contain("Base-64")
            .And.Contain("no longer lists").And.NotContain(Password);
    }

    [Fact]
    public async Task AnAnswerWithoutAKeySlot_TellsTheHostOnce()
    {
        var host = new FakeJoinHost(Guid.NewGuid());
        var noSlot = System.Text.Json.Nodes.JsonNode.Parse(host.JoinResponseJson(Password))!;
        noSlot.AsObject().Remove("keySlot");
        using var server = new RecordingJoinHost(r =>
            r.Path == "/api/join" ? Ok(noSlot.ToJsonString()) : r.Path == "/api/join/abort" ? Ok(Removed) : null);

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().Be(1);
        server.Paths.Should().Equal("/api/join", "/api/join/abort");
        output.ToString().Should().Contain("Error: the join could not be completed");
    }

    [Fact]
    public async Task AHostKeyThatIsNotBase64_FailsTheLocalWrites_AndTheHostIsToldOnce()
    {
        var host = new FakeJoinHost(Guid.NewGuid());
        using var server = new RecordingJoinHost(r => r.Path switch
        {
            "/api/join" => Ok(FakeJoinHost.WithField(host.JoinResponseJson(Password), "remoteNode", "ed25519PublicKeyB64", "***not base64***")),
            "/api/join/abort" => Ok(Removed),
            _ => host.Answer(r.Path)
        });

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().Be(1);
        server.Paths.Count(p => p == "/api/join/abort").Should().Be(1);
        output.ToString().Should().Contain("Base-64").And.Contain("no longer lists");
    }

    [Fact]
    public async Task AGoodJoin_NeverCallsTheAbort()
    {
        var host = new FakeJoinHost(Guid.NewGuid());
        using var server = new RecordingJoinHost(HostOf(host));

        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: new StringWriter());

        rc.Should().Be(0);
        server.Paths.Should().NotContain("/api/join/abort");
    }

    [Fact]
    public async Task TheClient_SendsTheCodesTokenInTheJoinHeader_AndNeverThrows()
    {
        using var server = new RecordingJoinHost(_ => RecordingJoinHost.Reply(200, "{\"aborted\":true,\"removed\":false}"));
        using var http = new HttpClient();

        var told = await JoinAbortClient.TryAbortAsync(http, server.Url + "/", Password, Guid.NewGuid(), new byte[32], joinToken: "tok-123");

        told.Should().Be(JoinAbortOutcome.NothingToRemove);
        server.Requests.Should().ContainSingle().Which.Headers[BeeMemoryBank.Core.Models.JoinCode.TokenHeader].Should().Be("tok-123");

        var unreachable = await JoinAbortClient.TryAbortAsync(http, "http://localhost:1", Password, Guid.NewGuid(), new byte[32]);
        unreachable.Should().Be(JoinAbortOutcome.Failed);
    }
}
