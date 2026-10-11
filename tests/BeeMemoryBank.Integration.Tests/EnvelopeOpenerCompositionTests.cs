using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Step A (docs/design/step-a-spec.md 6.3, 9.4): a full node opens sealed events with its key ring; the same Api started
/// as a blind node (BMB_ROLE=blind) relays them instead and never holds one. One composition test per host.
/// </summary>
public class EnvelopeOpenerCompositionTests
{
    [Fact]
    public async Task AFullNode_OpensSealedEvents()
    {
        using var node = new BmbWebApplicationFactory();
        await node.InitializeNodeAsync();
        node.Services.GetRequiredService<IEnvelopeOpener>().Should().BeOfType<VaultEnvelopeOpener>();
        node.Services.GetRequiredService<IMetadataKeyRing>().Should().BeOfType<MetadataKeyRing>();
        node.Services.GetRequiredService<ISyncProtocolPolicy>().Should().BeOfType<SyncProtocolPolicy>();
    }

    [Fact]
    public async Task TheApiInTheBlindRole_RelaysSealedEvents_AndHoldsNothing()
    {
        using var node = new BlindNodeFactory();
        await node.InitializeNodeAsync();
        node.Services.GetRequiredService<IEnvelopeOpener>().Should().BeOfType<RelayEnvelopeOpener>();
        node.Services.GetRequiredService<IHeldEventDrain>().Should().BeOfType<NoHeldEventDrain>();
    }
}
