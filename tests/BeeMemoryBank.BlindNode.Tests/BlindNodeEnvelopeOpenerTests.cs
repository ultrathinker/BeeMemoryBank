using BeeMemoryBank.Integration.Tests;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// Step A (docs/design/step-a-spec.md 9.4): the Docker blind node stores and relays sealed events and never opens one; it
/// never holds an event. The container it builds says so.
/// </summary>
public class BlindNodeEnvelopeOpenerTests
{
    [Fact]
    public async Task TheBlindHost_ResolvesTheRelayOpener_AndHoldsNothing()
    {
        using var node = new BlindNodeFactory();
        await node.InitializeNodeAsync();
        node.Services.GetRequiredService<IEnvelopeOpener>().Should().BeOfType<RelayEnvelopeOpener>();
        node.Services.GetRequiredService<IHeldEventDrain>().Should().BeOfType<NoHeldEventDrain>();
        node.Services.GetRequiredService<IHeldEventStatus>().Should().BeOfType<HeldEventStatus>();
    }
}
