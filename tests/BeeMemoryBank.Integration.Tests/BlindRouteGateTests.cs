using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Api.Helpers;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// EndpointAuthGuardrailTests for the routes that exist only on a BLIND node (the shared
/// guardrail runs on a full node, where they are not mapped): every /api/blind route declares
/// .RequireSuperadmin() at its registration site — except the status, which checks the role
/// itself — and a caller on the 'user' role behind the internal key gets 403 from each.
///
/// <para>The peer-facing routes of pairing and seeding (BMB-52) are the other exception: a peer
/// presents the pair code's one-time secret or its sync token, never the internal key, so a role
/// gate cannot apply. Each is in the public-surface allow-list (pinned by the shared guardrail) and
/// authorizes in its handler; here a 'user' caller must simply get nowhere with them. So do the restore
/// routes a restoring device calls with the one-time restore code (BMB-53): package, events, claim.</para>
/// </summary>
public class BlindRouteGateTests : IAsyncLifetime
{
    private static readonly HashSet<string> PeerAuthenticated =
        ["/api/blind/seed", "/api/blind/seed/{seedId:guid}", "/api/blind/replica",
         "/api/blind/restore/package", "/api/blind/restore/events", "/api/blind/claim"];

    private readonly BlindNodeFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeNodeAsync(password: "blindGatePw");

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task EveryBlindRoute_IsSuperadminGated_OnTheBlindNode()
    {
        using var user = _factory.Server.CreateClient();
        user.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        user.DefaultRequestHeaders.Add("X-User-Role", "user");

        var routes = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/blind/") == true)
            .ToList();
        routes.Should().Contain(e => e.RoutePattern.RawText == "/api/blind/wipe", "the blind node maps its own routes");

        var failures = new List<string>();
        foreach (var e in routes)
        {
            var path = e.RoutePattern.RawText!;
            var method = e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.First();
            if (PeerAuthenticated.Contains(path))
            {
                var probe = await user.SendAsync(new HttpRequestMessage(new HttpMethod(method),
                    path.Replace("{seedId:guid}", Guid.NewGuid().ToString()))
                {
                    Content = method is "POST" or "PUT" ? JsonContent.Create(new { }) : null,
                });
                if (probe.IsSuccessStatusCode)
                    failures.Add($"{method} {path} answered {(int)probe.StatusCode} to a 'user' caller without a peer credential");
                continue;
            }
            if (path != "/api/blind/status" && e.Metadata.GetMetadata<RequiresSuperadmin>() is null)
                failures.Add($"{method} {path} has no .RequireSuperadmin()");

            var resp = await user.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = method is "POST" or "PUT" ? JsonContent.Create(new { }) : null,
            });
            if (resp.StatusCode != HttpStatusCode.Forbidden)
                failures.Add($"{method} {path} answered {(int)resp.StatusCode} to a 'user' caller, expected 403");
        }
        failures.Should().BeEmpty("a blind route must say who may call it where it is registered");
    }
}
