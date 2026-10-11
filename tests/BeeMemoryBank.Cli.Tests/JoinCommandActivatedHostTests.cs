using System.Text;
using System.Text.Json;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// <c>bmb join</c> against a host that has activated metadata encryption (docs/design/step-a-spec.md 11, 16.4 item 6): it says what it
/// can read, opens the sealed archive with the key the host just handed it, and refuses a plain archive from a host that seals.
/// </summary>
public class JoinCommandActivatedHostTests : IDisposable
{
    private const string Password = "cliActivatedJoinPassword";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "bmb_cli_joinact_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static RecordingJoinHost HostFor(FakeJoinHost host) =>
        new(seen => seen.Path == "/api/join"
            ? RecordingJoinHost.Reply(200, host.JoinResponseJson(Password))
            : host.Answer(seen.Path));

    [Fact]
    public async Task Join_SendsTheProtocolAndTheCapability_OpensTheSealedArchive_AndImportsIt()
    {
        var host = new FakeJoinHost(Guid.NewGuid(), cpSeq: 42, lamportTs: 4_242, activation: FakeHostActivation.Sealed);
        using var server = HostFor(host);

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().Be(0, output.ToString());
        output.ToString().Should().Contain("checkpoint 42");
        var join = server.Requests.Single(r => r.Path == "/api/join");
        using (var body = JsonDocument.Parse(join.Body))
        {
            body.RootElement.GetProperty("protocolVersion").GetInt32().Should().Be(3, "a fresh node has applied no activation");
            body.RootElement.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).Should().Equal([MetadataCapabilities.EnvelopeV1]);
        }
        using (var auth = JsonDocument.Parse(server.Requests.Single(r => r.Path == "/api/sync/authenticate").Body))
            auth.RootElement.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).Should().Equal(
                [MetadataCapabilities.EnvelopeV1], "an activated host takes a protocol-3 peer only with the capability");

        await using var services = await CliServiceProvider.CreateAsync(_tempDir);
        using var scope = services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.InitialSyncCompleted.Should().BeTrue();
        (await scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>().GetAsync(host.HostId))!.LastSequenceNum.Should().Be(42);
    }

    [Fact]
    public async Task Join_RefusesAPlainArchiveFromAHostThatDeclaresProtocol4_AndLeavesNoNode()
    {
        var host = new FakeJoinHost(Guid.NewGuid(), activation: FakeHostActivation.PlainWhileActivated);
        using var server = HostFor(host);

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().NotBe(0);
        output.ToString().Should().Contain("plain snapshot").And.Contain("can be run again").And.NotContain(Password);
        await using var services = await CliServiceProvider.CreateAsync(_tempDir);
        using var scope = services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().BeNull("no half-made node");
    }

    [Fact]
    public async Task Join_RefusesAnArchiveSealedUnderAnotherKey_AndImportsNothing()
    {
        var host = new FakeJoinHost(Guid.NewGuid(), activation: FakeHostActivation.SealedUnderAnotherKey);
        using var server = HostFor(host);

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().NotBe(0);
        output.ToString().Should().Contain("could not be opened with this vault's master key").And.NotContain(Password);
        await using var services = await CliServiceProvider.CreateAsync(_tempDir);
        using var scope = services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Join_ToAHostBeforeActivation_StillTakesThePlainArchive()
    {
        var host = new FakeJoinHost(Guid.NewGuid(), cpSeq: 7);
        using var server = HostFor(host);

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().Be(0, output.ToString());
        output.ToString().Should().Contain("checkpoint 7");
    }

    [Fact]
    public async Task Join_ToAHostOnAProtocolThisBuildCannotApply_IsStillRefused()
    {
        var host = new FakeJoinHost(Guid.NewGuid(), activation: FakeHostActivation.Sealed);
        using var server = new RecordingJoinHost(seen => seen.Path == "/api/join"
            ? RecordingJoinHost.Reply(200, host.JoinResponseJson(Password, protocolVersion: 5))
            : host.Answer(seen.Path));

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().Be(1);
        output.ToString().Should().Contain("protocol version (5)").And.Contain("higher than local version (4)");
    }
}
