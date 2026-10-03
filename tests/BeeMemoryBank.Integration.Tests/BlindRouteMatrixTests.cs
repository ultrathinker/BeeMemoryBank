using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The route matrix of a blind node: every method + pattern it serves, pinned in docs/blind-node/ROUTES.golden.txt.
/// The same file is linked into BeeMemoryBank.BlindNode.Tests, so the Api in the blind role and the dedicated host are
/// held to ONE list - an endpoint cannot appear on (or vanish from) a blind node without this file changing in review.
/// Regenerate after an intended change with BMB_UPDATE_GOLDEN=1.
/// </summary>
public class BlindRouteMatrixTests
{
    private static string GoldenPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the tests run from inside the repository");
        return Path.Combine(dir!.FullName, "docs", "blind-node", "ROUTES.golden.txt");
    }

    [Fact]
    public async Task TheBlindNodeServesExactlyTheRoutesInTheGoldenFile()
    {
        using var blind = new BlindNodeFactory();
        await blind.InitializeNodeAsync();

        var routes = blind.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e =>
            {
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
                return $"{string.Join(",", methods.OrderBy(m => m, StringComparer.Ordinal))} {e.RoutePattern.RawText}";
            })
            .Distinct()
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        var actual = string.Join("\n", routes) + "\n";
        var path = GoldenPath();
        if (Environment.GetEnvironmentVariable("BMB_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        var expected = File.ReadAllText(path).Replace("\r\n", "\n");
        actual.Should().Be(expected,
            "a blind node serves only what the mesh needs from a store that cannot read; a change to this list is a change to what is reachable");
    }
}
