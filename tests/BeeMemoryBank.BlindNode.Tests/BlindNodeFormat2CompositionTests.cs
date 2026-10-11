using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Integration.Tests;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// Step A hooks H1 and H2 (docs/design/step-a-spec.md 15.2) in the Docker blind node's own host: the blind store, the
/// format-2 reader, the store as what this node serves and summarises, the format-2 writer signed with the external key, and
/// the activation observer that asks for a format-2 reseed.
/// </summary>
public class BlindNodeFormat2CompositionTests
{
    [Fact]
    public async Task TheBlindHost_ResolvesTheStoreTheReaderTheWriterAndTheObserver()
    {
        using var node = new BlindNodeFactory();
        await node.InitializeNodeAsync();
        node.Services.GetRequiredService<IBaselineStore>().Should().BeOfType<BaselineStore>();
        node.Services.GetRequiredService<IBlindPackageV2Reader>().Should().BeOfType<BlindPackageV2Reader>();
        node.Services.GetRequiredService<IBaselineSource>().Should().BeOfType<BlindBaselineSource>();
        node.Services.GetRequiredService<IBlindStoreSummarySource>().Should().BeOfType<BlindStoreSummarySource>();
        using var scope = node.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IBlindPackageV2Writer>().Should().BeOfType<BlindPackageV2Writer>();
        node.Services.GetServices<IMetadataActivationObserver>().Should().ContainSingle(o => o is BlindActivationObserver);
    }
}
