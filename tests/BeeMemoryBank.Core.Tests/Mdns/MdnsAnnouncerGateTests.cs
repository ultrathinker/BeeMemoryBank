using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Infrastructure.Mdns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Core.Tests.Mdns;

/// <summary>
/// The network-setting gate of the announcer (task P7): a desktop node announces itself only while "Devices on my network" is on, and
/// withdraws when it goes off. These tests never open a multicast socket (the closed gate returns before anything is touched); the
/// announcement itself is <see cref="MdnsRoundTripTests"/>.
/// </summary>
public class MdnsAnnouncerGateTests
{
    private static MdnsAnnouncer Announcer(Func<bool>? gate)
    {
        var options = new MdnsAnnouncerOptions { AnnounceGate = gate };
        // No INodeIdentityRepository is registered: a gate that lets the evaluation continue meets that, a closed gate never does.
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new MdnsAnnouncer(scopes, new InvisibleModeService(), options, NullLogger<MdnsAnnouncer>.Instance);
    }

    [Fact]
    public async Task ClosedGate_AnnouncesNothing_AndTouchesNeitherTheDatabaseNorTheNetwork()
    {
        var announcer = Announcer(() => false);

        await announcer.EvaluateAsync(CancellationToken.None);

        announcer.IsAdvertising.Should().BeFalse();
    }

    [Fact]
    public async Task OpenGate_LetsTheNormalChecksRun()
    {
        var announcer = Announcer(() => true);

        // The normal path starts by reading the node's identity; here that is not registered, which is how the test sees it ran.
        await FluentActions.Awaiting(() => announcer.EvaluateAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task NoGate_BehavesAsBefore_ForServersAndDocker()
    {
        var announcer = Announcer(null);

        await FluentActions.Awaiting(() => announcer.EvaluateAsync(CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>("without a gate the evaluation goes straight to the identity, as it always did");
    }

    [Fact]
    public async Task TheGate_IsAskedOnEveryEvaluation_SoTheSettingTakesEffectWithoutARestart()
    {
        var open = false;
        var calls = 0;
        var options = new MdnsAnnouncerOptions { AnnounceGate = () => { calls++; return open; } };
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var announcer = new MdnsAnnouncer(scopes, new InvisibleModeService(), options, NullLogger<MdnsAnnouncer>.Instance);

        await announcer.EvaluateAsync(CancellationToken.None);
        await announcer.EvaluateAsync(CancellationToken.None);
        calls.Should().Be(2);

        open = true;
        await FluentActions.Awaiting(() => announcer.EvaluateAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Be(3, "the gate is read again each cycle, and a change is seen on the next one");
    }
}
