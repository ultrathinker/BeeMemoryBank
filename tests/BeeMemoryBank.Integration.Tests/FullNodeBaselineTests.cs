using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The FULL node as it was before the vault split (BMB-99, release 2.0.1): its container and its routes, pinned in
/// docs/full-node/. The split must leave the full node unchanged, so any difference here is either an intended change
/// (regenerate with BMB_UPDATE_GOLDEN=1 and review the diff) or a registration/route lost in the move.
/// </summary>
public class FullNodeBaselineTests
{
    private sealed class CapturingFactory : BmbWebApplicationFactory
    {
        public List<ServiceDescriptor> Descriptors { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // ConfigureTestServices runs after the application's own registrations: the list is complete.
            builder.ConfigureTestServices(services => Descriptors.AddRange(services));
        }
    }

    [Fact]
    public async Task TheFullNodeContainerIsTheBaseline()
    {
        using var node = new CapturingFactory();
        await node.InitializeNodeAsync();
        node.Descriptors.Should().NotBeEmpty("the capture must see the registrations");
        var actual = DiSnapshot.Container(node.Descriptors) + DiSnapshot.Hosted(node.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>());
        actual.Should().Contain("Singleton BeeMemoryBank.Core.Services.SessionService", "the scan must see the vault services of a full node");
        DiSnapshot.Check("docs/full-node/DI.golden.txt", actual).Should().BeNull();
    }

    [Fact]
    public async Task TheFullNodeServesTheBaselineRoutes()
    {
        using var node = new BmbWebApplicationFactory();
        await node.InitializeNodeAsync();
        var actual = DiSnapshot.Routes(node.Services.GetRequiredService<EndpointDataSource>());
        actual.Split('\n').Length.Should().BeGreaterThan(100, "a full node serves hundreds of routes");
        DiSnapshot.Check("docs/full-node/ROUTES.golden.txt", actual).Should().BeNull();
    }
}
