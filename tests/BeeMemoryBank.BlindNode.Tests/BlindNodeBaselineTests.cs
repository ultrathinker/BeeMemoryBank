using BeeMemoryBank.Integration.Tests;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// The blind node host as it was before the vault split (BMB-99): its container (with the hosted services), pinned in
/// docs/blind-node/DI.golden.txt. Its routes are pinned by BlindRouteMatrixTests (47). A difference is an intended change
/// (regenerate with BMB_UPDATE_GOLDEN=1, review the diff) or a registration lost or gained by the move.
/// </summary>
public class BlindNodeBaselineTests
{
    private sealed class CapturingFactory : BlindNodeFactory
    {
        public List<ServiceDescriptor> Descriptors { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => Descriptors.AddRange(services));
        }
    }

    [Fact]
    public async Task TheBlindHostContainerIsTheBaseline()
    {
        using var node = new CapturingFactory();
        await node.InitializeNodeAsync();
        node.Descriptors.Should().NotBeEmpty();
        var actual = DiSnapshot.Container(node.Descriptors) + DiSnapshot.Hosted(node.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>());
        actual.Should().Contain("EventApplier", "the scan must see the registrations of the blind host");
        DiSnapshot.Check("docs/blind-node/DI.golden.txt", actual).Should().BeNull();
    }
}
