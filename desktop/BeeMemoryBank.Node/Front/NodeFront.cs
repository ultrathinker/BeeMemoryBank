using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using BeeMemoryBank.Hosting;
using BeeMemoryBank.Hosting.AspNetCore;

namespace BeeMemoryBank.Node;

/// <summary>
/// A reverse proxy front for BeeMemoryBank Node.
/// Routes requests to Api and Web child processes based on path/method constraints.
/// </summary>
public class NodeFront
{
    private readonly IReadOnlyDictionary<string, ReadyFileInfo> _children;
    private readonly string _apiUrl;
    private readonly string _webUrl;

    /// <summary>
    /// Initializes the front by extracting Api and Web target URLs from the child process infos.
    /// </summary>
    public NodeFront(IReadOnlyDictionary<string, ReadyFileInfo> children)
    {
        _children = children ?? throw new ArgumentNullException(nameof(children));

        var apiChild = children.Values.FirstOrDefault(c => c.ApplicationName.Contains("Api", StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Api child process ready info not found.");
        var webChild = children.Values.FirstOrDefault(c => c.ApplicationName.Contains("Web", StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Web child process ready info not found.");

        _apiUrl = apiChild.Urls.FirstOrDefault() ?? throw new ArgumentException("Api child process has no registered URLs.");
        _webUrl = webChild.Urls.FirstOrDefault() ?? throw new ArgumentException("Web child process has no registered URLs.");
    }

    /// <summary>
    /// Alternate constructor specifying URLs directly, mainly for testability.
    /// </summary>
    public NodeFront(string apiUrl, string webUrl, IReadOnlyDictionary<string, ReadyFileInfo> children)
    {
        _apiUrl = apiUrl ?? throw new ArgumentNullException(nameof(apiUrl));
        _webUrl = webUrl ?? throw new ArgumentNullException(nameof(webUrl));
        _children = children ?? throw new ArgumentNullException(nameof(children));
    }

    /// <summary>
    /// Registers Kestrel body limits and YARP proxy services.
    /// </summary>
    public void RegisterServices(IServiceCollection services)
    {
        // Limit request body size to 500 MB (large file uploads must pass through)
        services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestBodySize = 500L * 1024 * 1024;
        });

        // Configure YARP routes and clusters in-memory
        var routes = new[]
        {
            new RouteConfig
            {
                RouteId = "api-mcp",
                ClusterId = "Api",
                Match = new RouteMatch { Path = "/mcp" },
                Order = 1
            },
            new RouteConfig
            {
                RouteId = "api-mcp-rest",
                ClusterId = "Api",
                Match = new RouteMatch { Path = "/mcp/{**rest}" },
                Order = 1
            },
            new RouteConfig
            {
                RouteId = "api-sync-rest",
                ClusterId = "Api",
                Match = new RouteMatch { Path = "/api/sync/{**rest}" },
                Order = 1
            },
            new RouteConfig
            {
                RouteId = "api-join",
                ClusterId = "Api",
                Match = new RouteMatch { Path = "/api/join", Methods = new[] { "POST" } },
                Order = 1
            },
            new RouteConfig
            {
                RouteId = "api-health",
                ClusterId = "Api",
                Match = new RouteMatch { Path = "/health" },
                Order = 1
            },
            new RouteConfig
            {
                RouteId = "web-catchall",
                ClusterId = "Web",
                Match = new RouteMatch { Path = "{**catchall}" },
                Order = 1000 // Lowest priority
            }
        };

        var clusters = new[]
        {
            new ClusterConfig
            {
                ClusterId = "Api",
                Destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    { "api-destination", new DestinationConfig { Address = _apiUrl } }
                },
                HttpRequest = new ForwarderRequestConfig
                {
                    ActivityTimeout = TimeSpan.FromMinutes(35)
                }
            },
            new ClusterConfig
            {
                ClusterId = "Web",
                Destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    { "web-destination", new DestinationConfig { Address = _webUrl } }
                },
                HttpRequest = new ForwarderRequestConfig
                {
                    ActivityTimeout = TimeSpan.FromMinutes(35)
                }
            }
        };

        services.AddReverseProxy()
            .LoadFromMemory(routes, clusters);
    }

    /// <summary>
    /// Maps direct endpoints (including loopback-only /node/* status endpoints) and reverse proxy middleware.
    /// </summary>
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var nodeGroup = endpoints.MapGroup("/node")
            .AddEndpointFilter(async (context, next) =>
            {
                var remoteIp = context.HttpContext.Connection.RemoteIpAddress;
                if (!LoopbackIpMatcher.IsLoopback(remoteIp))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
                return await next(context);
            });

        nodeGroup.MapGet("/status", () =>
        {
            var version = typeof(NodeFront).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.1";
            var status = new
            {
                version,
                children = _children.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new
                    {
                        state = "Running",
                        pid = kvp.Value.Pid
                    }
                )
            };
            return Results.Json(status);
        });

        nodeGroup.MapPost("/lock", () =>
        {
            // TODO: Needs real wiring later when the internal-key client is implemented.
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        });

        nodeGroup.MapPost("/sync-now", () =>
        {
            // TODO: Needs real wiring later when the internal-key client is implemented.
            return Results.StatusCode(StatusCodes.Status501NotImplemented);
        });

        // Map the YARP reverse proxy to route other incoming requests
        endpoints.MapReverseProxy();
    }
}

/// <summary>
/// Static builder to allow registering NodeFront on a WebApplicationBuilder in a single call.
/// </summary>
public static class NodeFrontBuilder
{
    /// <summary>
    /// Configures the reverse proxy services and registers the NodeFront instance in DI.
    /// </summary>
    public static NodeFront Build(WebApplicationBuilder builder, IReadOnlyDictionary<string, ReadyFileInfo> children)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (children == null) throw new ArgumentNullException(nameof(children));

        var front = new NodeFront(children);
        front.RegisterServices(builder.Services);
        builder.Services.AddSingleton(front);

        return front;
    }
}
