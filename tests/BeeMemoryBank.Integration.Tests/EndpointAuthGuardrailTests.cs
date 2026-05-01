using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

public class EndpointAuthGuardrailTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();

    private static readonly HashSet<string> PublicRoutes =
    [
        "/health",
        "/api/version",
        "/api/init/status",
        "/api/session/status",
        "/api/session/unlock",
        "/api/session/login",
        "/api/sync/identity",
        "/api/sync/sentinel",
        "/api/sync/challenge",
        "/api/sync/authenticate",
        "/api/join",
        "/api/snapshots/restore/progress",
    ];

    private static readonly HashSet<string> SyncTokenRoutes =
    [
        "/api/sync/events",
        "/api/sync/report-position",
    ];

    public async Task InitializeAsync()
    {
        await _factory.InitializeNodeAsync();
    }

    public Task DisposeAsync()
    {
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Every_ApiGetEndpoint_RejectsUnauthenticatedRequests()
    {
        using var client = _factory.Server.CreateClient();

        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var routeEndpoints = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => IsMethod(e, "GET"))
            .Where(e =>
            {
                var p = e.RoutePattern.RawText;
                return p != null && (p.StartsWith("/api/") || p == "/health");
            })
            .ToList();

        var failures = new List<string>();

        foreach (var endpoint in routeEndpoints)
        {
            var pattern = endpoint.RoutePattern.RawText!;
            var url = SubstituteRouteParams(pattern);

            if (PublicRoutes.Contains(pattern))
                continue;

            if (SyncTokenRoutes.Contains(pattern))
                continue;

            try
            {
                var response = await client.GetAsync(url);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    failures.Add($"GET {url} returned 200 OK without authentication. " +
                                 "Add InternalKeyValidator.Validate() or add to PublicRoutes.");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"GET {url} threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        failures.Should().BeEmpty(
            "Every /api/* GET endpoint must reject unauthenticated requests. " +
            "If a new public endpoint is intentionally unauthenticated, add it to PublicRoutes.");
    }

    [Fact]
    public async Task Every_ApiMutationEndpoint_RejectsUnauthenticatedRequests()
    {
        using var client = _factory.Server.CreateClient();

        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        var mutationEndpoints = dataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e =>
            {
                var methods = e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
                return methods != null && methods.Any(m => m is "POST" or "PUT" or "DELETE" or "PATCH");
            })
            .Where(e =>
            {
                var p = e.RoutePattern.RawText;
                return p != null && p.StartsWith("/api/");
            })
            .ToList();

        var failures = new List<string>();

        foreach (var endpoint in mutationEndpoints)
        {
            var pattern = endpoint.RoutePattern.RawText!;
            var url = SubstituteRouteParams(pattern);
            var method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.First(m => m is "POST" or "PUT" or "DELETE" or "PATCH");

            if (PublicRoutes.Contains(pattern))
                continue;

            if (SyncTokenRoutes.Contains(pattern))
                continue;

            try
            {
                HttpResponseMessage response;
                if (method == "DELETE")
                {
                    var req = new HttpRequestMessage(HttpMethod.Delete, url);
                    response = await client.SendAsync(req);
                }
                else if (method == "PATCH")
                {
                    var req = new HttpRequestMessage(new HttpMethod("PATCH"), url)
                    {
                        Content = JsonContent.Create(new { path = "/test" })
                    };
                    response = await client.SendAsync(req);
                }
                else
                {
                    var body = BuildMinimalBody(pattern);
                    var req = new HttpRequestMessage(new HttpMethod(method), url)
                    {
                        Content = JsonContent.Create(body)
                    };
                    response = await client.SendAsync(req);
                }

                if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
                {
                    failures.Add($"{method} {url} returned {response.StatusCode} without authentication. " +
                                 "Add InternalKeyValidator.Validate() or add to PublicRoutes.");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{method} {url} threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        failures.Should().BeEmpty(
            "Every /api/* mutation endpoint must reject unauthenticated requests. " +
            "If a new public endpoint is intentionally unauthenticated, add it to PublicRoutes.");
    }

    private static string SubstituteRouteParams(string pattern)
    {
        return System.Text.RegularExpressions.Regex.Replace(pattern, @"\{(\w+)(?::(\w+))?\}", match =>
        {
            var constraint = match.Groups[2].Success ? match.Groups[2].Value : "";
            return constraint switch
            {
                "guid" => "00000000-0000-0000-0000-000000000000",
                "int" => "0",
                _ => "0"
            };
        });
    }

    private static object BuildMinimalBody(string pattern)
    {
        if (pattern.Contains("search"))
            return new { q = "test" };
        if (pattern.Contains("snapshots") && pattern.Contains("restore"))
            return new { fileName = "test" };
        if (pattern.Contains("snapshots"))
            return new { };
        if (pattern.Contains("keys") && pattern.Contains("change-password"))
            return new { currentPassword = "x", newPassword = "y" };
        if (pattern.Contains("keys") && pattern.Contains("add-recovery"))
            return new { password = "x" };
        if (pattern.Contains("change-password"))
            return new { currentPassword = "x", newPassword = "y" };
        if (pattern.Contains("folder") || pattern.Contains("move"))
            return new { path = "/test", destinationPath = "/test2" };
        if (pattern.Contains("articles"))
            return new { title = "t", treePath = "/", content = "" };
        if (pattern.Contains("comment"))
            return new { articleId = Guid.Empty, text = "t" };
        if (pattern.Contains("agent"))
            return new { name = "t" };
        if (pattern.Contains("user"))
            return new { username = "t", password = "t", displayName = "t" };
        return new { };
    }

    private static bool IsMethod(RouteEndpoint endpoint, string method)
    {
        return endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true;
    }
}
