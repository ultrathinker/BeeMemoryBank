using System.Text;
using System.Text.Json;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// <c>bmb join</c> against a host that has activated metadata encryption (docs/design/step-a-spec.md 11, 16.4 item 6): it says what it
/// can read, opens the sealed archive with the key the host just handed it, and refuses a plain archive from a host that seals.
/// </summary>
[Collection("CliSealedStatics")]
public class JoinCommandActivatedHostTests : IDisposable
{
    private const string Password = "cliActivatedJoinPassword";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "bmb_cli_joinact_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        JoinedNode.Reset();
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

        VaultStoreFiles.DetectMode(_tempDir).Should().Be(VaultStoreMode.Sealed, "a node that joined is encrypted under the password it was given (BMB-306)");
        await using var services = await JoinedNode.OpenAsync(_tempDir, Password);
        using var scope = services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.InitialSyncCompleted.Should().BeTrue();
        (await scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>().GetAsync(host.HostId))!.LastSequenceNum.Should().Be(42);
        scope.ServiceProvider.GetRequiredService<ISyncProtocolPolicy>().SealsMetadata.Should().BeTrue("the activation came with the snapshot of the host");
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
        await using var services = await JoinedNode.OpenAsync(_tempDir, Password);
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
        await using var services = await JoinedNode.OpenAsync(_tempDir, Password);
        using var scope = services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Join_ToAHostBeforeActivation_IsRefused_WithTheSentence_AndLeavesNothing()
    {
        // A new node joins only a node on protocol 4 (BMB-306): no plain archive is taken, there is no compatibility mode.
        var host = new FakeJoinHost(Guid.NewGuid(), cpSeq: 7, activation: FakeHostActivation.None);
        using var server = HostFor(host);

        var output = new StringWriter();
        var rc = await JoinCommand.HandleAsync(_tempDir, server.Url, Password, "CliJoiner", output: output);

        rc.Should().Be(1, output.ToString());
        output.ToString().Should().Contain("Cannot join: the other computer does not encrypt its metadata yet")
            .And.Contain("protocol 3").And.Contain("Switch metadata encryption on there first").And.NotContain(Password);
        server.Requests.Should().NotContain(r => r.Path == "/api/sync/snapshot/for-join", "the refusal comes before any archive is asked for");
        server.Requests.Should().Contain(r => r.Path == "/api/join/abort", "the host is told that the joiner gave up, so its pending row is not left behind");
        await using var services = await JoinedNode.OpenAsync(_tempDir, Password);
        using var scope = services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().BeNull("no node was made, and nothing was imported");
        VaultStoreFiles.DetectMode(_tempDir).Should().NotBe(VaultStoreMode.Sealed);
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
