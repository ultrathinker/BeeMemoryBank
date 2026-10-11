using System.Text.Json;
using BeeMemoryBank.Cli.Tests;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Mobile.Services;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// The phone's join (<see cref="NodeSetupService.JoinAsync"/>, shared by the Android app and the iPhone full app) against a host that has
/// activated metadata encryption (docs/design/step-a-spec.md 11, 16.4 item 6).
/// </summary>
public class PhoneJoinActivatedHostTests
{
    private static RecordingJoinHost HostFor(FakeJoinHost host) =>
        new(seen => seen.Path == "/api/join"
            ? RecordingJoinHost.Reply(200, host.JoinResponseJson(TestVault.Password))
            : host.Answer(seen.Path));

    [Fact]
    public async Task APhoneSaysWhatItCanRead_OpensTheSealedArchive_AndImportsIt()
    {
        var host = new FakeJoinHost(Guid.NewGuid(), cpSeq: 42, lamportTs: 4_242, activation: FakeHostActivation.Sealed);
        using var server = HostFor(host);

        await using var test = await TestVault.PrepareAsync("phone-join-activated");
        using (var scope = test.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<NodeSetupService>().JoinAsync("Phone", server.Url, TestVault.Password);

        using (var join = JsonDocument.Parse(server.Requests.Single(r => r.Path == "/api/join").Body))
        {
            join.RootElement.GetProperty("protocolVersion").GetInt32().Should().Be(3);
            join.RootElement.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).Should().Equal([MetadataCapabilities.EnvelopeV1]);
        }
        using (var auth = JsonDocument.Parse(server.Requests.Single(r => r.Path == "/api/sync/authenticate").Body))
            auth.RootElement.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()).Should().Equal([MetadataCapabilities.EnvelopeV1]);
        using var check = test.Services.CreateScope();
        (await check.ServiceProvider.GetRequiredService<ISyncPositionRepository>().GetAsync(host.HostId))!.LastSequenceNum.Should().Be(42);
        (await check.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.InitialSyncCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task APhoneRefusesAPlainArchiveFromAHostThatDeclaresProtocol4_AndLeavesNoNode()
    {
        var host = new FakeJoinHost(Guid.NewGuid(), activation: FakeHostActivation.PlainWhileActivated);
        using var server = HostFor(host);

        await using var test = await TestVault.PrepareAsync("phone-join-plain-refused");
        using var scope = test.Services.CreateScope();
        var act = () => scope.ServiceProvider.GetRequiredService<NodeSetupService>().JoinAsync("Phone", server.Url, TestVault.Password);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Contain("plain snapshot").And.NotContain(TestVault.Password);
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().BeNull("a retry starts clean");
        (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task APhoneRefusesAnArchiveSealedUnderAnotherKey()
    {
        var host = new FakeJoinHost(Guid.NewGuid(), activation: FakeHostActivation.SealedUnderAnotherKey);
        using var server = HostFor(host);

        await using var test = await TestVault.PrepareAsync("phone-join-wrong-key");
        using var scope = test.Services.CreateScope();
        var act = () => scope.ServiceProvider.GetRequiredService<NodeSetupService>().JoinAsync("Phone", server.Url, TestVault.Password);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("could not be opened with this vault's master key");
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().BeNull();
    }

    [Fact]
    public async Task AHostBeforeActivation_IsJoinedAsBefore()
    {
        var host = new FakeJoinHost(Guid.NewGuid(), cpSeq: 7);
        using var server = HostFor(host);

        await using var test = await TestVault.PrepareAsync("phone-join-before-activation");
        using var scope = test.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NodeSetupService>().JoinAsync("Phone", server.Url, TestVault.Password);

        (await scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>().GetAsync(host.HostId))!.LastSequenceNum.Should().Be(7);
    }
}
