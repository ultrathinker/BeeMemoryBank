using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Step A hooks H1-H3 (docs/design/step-a-spec.md 15.2) in the Api: started as a blind node (BMB_ROLE=blind) it has the
/// blind store, the reader, the store as what it serves, the format-2 writer and the reseed observer; as a full node it has
/// only the writer a hub serves with (its baseline source and observer are track 3's), and no blind store.
/// </summary>
public class Format2CompositionTests
{
    [Fact]
    public async Task TheApiInTheBlindRole_HasTheBlindStoreAndTheFormat2Services()
    {
        using var node = new BlindNodeFactory();
        await node.InitializeNodeAsync();
        node.Services.GetRequiredService<IBaselineStore>().Should().BeOfType<BaselineStore>();
        node.Services.GetRequiredService<IBlindPackageV2Reader>().Should().BeOfType<BlindPackageV2Reader>();
        node.Services.GetRequiredService<IBaselineSource>().Should().BeOfType<BlindBaselineSource>();
        node.Services.GetRequiredService<IBlindStoreSummarySource>().Should().BeOfType<BlindStoreSummarySource>();
        using var scope = node.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IBlindPackageV2Writer>().Should().BeOfType<BlindPackageV2Writer>();
        scope.ServiceProvider.GetServices<IBlindPackageV2Writer>().Should().HaveCount(1, "the blind role's writer, not the hub's as well");
        node.Services.GetServices<IMetadataActivationObserver>().Should().ContainSingle(o => o is BlindActivationObserver);
    }

    [Fact]
    public async Task AFullNode_HasTheHubsWriter_AndNoBlindStore()
    {
        using var node = new BmbWebApplicationFactory();
        await node.InitializeNodeAsync();
        using var scope = node.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IBlindPackageV2Writer>().Should().BeOfType<BlindPackageV2Writer>();
        node.Services.GetService<IBaselineStore>().Should().BeNull("a full node keeps no adopted baseline");
        node.Services.GetService<IBlindStoreSummarySource>().Should().BeNull();
        node.Services.GetServices<IMetadataActivationObserver>().Should().NotContain(o => o is BlindActivationObserver);
    }
}
