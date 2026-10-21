using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Integration.Tests;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// The door's table and the endpoints agree (srvfix9, the route-table contract the review named): the endpoints that carry <see cref="BlindMeteredEndpoint"/> in the blind host are
/// exactly the keys of <see cref="BlindUsageMiddleware.DataRouteKeys"/>. A bulk endpoint added to the endpoint table (and the routes golden) with its marker but not to the door's table, or a
/// key left in the table after its endpoint went, fails here instead of being served unmetered.
/// </summary>
public sealed class BlindMeteredEndpointMetadataTests : IDisposable
{
    private readonly BlindNodeFactory _node = new();

    public void Dispose() => _node.Dispose();

    [Fact]
    public void The_endpoints_that_carry_the_metered_marker_are_exactly_the_doors_data_routes()
    {
        _ = _node.Services;
        var marked = _node.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<BlindMeteredEndpoint>() is not null)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(method => method + " " + endpoint.RoutePattern.RawText))
            .ToList();

        marked.Should().OnlyHaveUniqueItems();
        marked.Should().BeEquivalentTo(BlindUsageMiddleware.DataRouteKeys,
            "every metered endpoint is in the door's table and every key of the table is a marked endpoint");
    }
}
