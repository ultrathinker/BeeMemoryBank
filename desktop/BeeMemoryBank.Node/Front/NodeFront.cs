using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using BeeMemoryBank.Core.Services;
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
    /// The fixed port for the opt-in HTTPS listener (the plain-HTTP listener stays on its own
    /// port, configured via the front's <c>--urls</c> argument). Chosen distinct from the
    /// standalone/Docker ports (5300/5301) and the front's HTTP port (5310).
    /// </summary>
    public const int HttpsPort = 5311;

    /// <summary>
    /// Registers Kestrel body limits and YARP proxy services. When
    /// <paramref name="enableHttps"/> is true (and on Windows, with a usable
    /// <paramref name="dataPath"/>), additionally registers an ADDITIVE HTTPS listener on
    /// <see cref="HttpsPort"/> (<c>0.0.0.0:5311</c>) backed by <see cref="LocalCaService"/>'s
    /// leaf certificate. The existing plain-HTTP listener (driven by the front's
    /// <c>--urls</c> via <c>IServerAddressesFeature</c>) is never removed or modified: it is a
    /// separate binding mechanism, so the two listeners always coexist.
    /// </summary>
    /// <param name="enableHttps">
    /// Opt-in flag for the second HTTPS listener. Defaults to false (OFF), matching "по кнопке"
    /// in the superplan — a later task wires an actual UI toggle. When false, behavior is
    /// byte-for-byte identical to before this method grew these parameters.
    /// </param>
    /// <param name="dataPath">
    /// Data directory passed to <see cref="LocalCaService"/> for cert generation/reload. Only
    /// used when <paramref name="enableHttps"/> is true.</param>
    public void RegisterServices(IServiceCollection services, bool enableHttps = false, string? dataPath = null)
    {
        // The cert selector resolves the leaf FRESH on every TLS handshake (rather than capturing a
        // cert once at startup) so the 90-day leaf rotation "just works" without a process restart:
        // GetOrCreateLeafCertificate is cheap (reloads the on-disk cert, only re-mints on expiry/SAN
        // change). CachedLeafCert wraps that with caching of a SChannel-usable copy (see its doc).
        var caService = (enableHttps && OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(dataPath))
            ? new LocalCaService(dataPath)
            : null;
        var leafProvider = caService != null ? new CachedLeafCert(caService) : null;

        // Limit request body size to 500 MB (large file uploads must pass through) and, when
        // opted in, add the additive HTTPS listener. Both compose onto the same Kestrel options
        // as the existing body-size override.
        services.Configure<KestrelServerOptions>(options =>
        {
            options.Limits.MaxRequestBodySize = 500L * 1024 * 1024;

            if (leafProvider != null)
            {
                options.Listen(IPAddress.Any, HttpsPort, listenOptions =>
                {
                    listenOptions.UseHttps(httpsOptions =>
                    {
                        httpsOptions.ServerCertificateSelector = (_, _) => leafProvider.Get();
                    });
                });
            }
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

    /// <summary>
    /// Serves the <see cref="LocalCaService"/> leaf certificate to Kestrel's TLS selector,
    /// re-resolving it on every handshake (so 90-day rotation needs no restart) while caching a
    /// SChannel-usable copy keyed on the leaf's thumbprint.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a transform is needed.</b> <see cref="LocalCaService.GetOrCreateLeafCertificate"/>
    /// attaches the leaf's private key as an <i>ephemeral</i> ECDsa key (via
    /// <c>CopyWithPrivateKey</c>). Windows SChannel — which Kestrel uses on Windows for the TLS
    /// handshake — refuses ephemeral keys: <c>AcquireCredentialsHandle</c> fails with "the platform
    /// does not support ephemeral keys". A PFX round-trip with <see cref="X509KeyStorageFlags.PersistKeySet"/>
    /// relocates the key into a CNG key container that SChannel can acquire credentials from. This
    /// happens here, at serve time, because <see cref="LocalCaService"/> itself is off-limits.</para>
    /// <para><b>Caching.</b> The transform is cached by thumbprint and re-run only when the leaf
    /// actually rotates (every ~90 days), so per-handshake cost is a cheap idempotent reload and we
    /// don't accumulate CNG key containers on the hot path. Thread-safe via <see cref="_gate"/>.</para>
    /// </remarks>
    private sealed class CachedLeafCert
    {
        private readonly LocalCaService _caService;
        private readonly object _gate = new();
        private X509Certificate2? _served;
        private string? _servedThumbprint;

        public CachedLeafCert(LocalCaService caService)
        {
            _caService = caService;
        }

        public X509Certificate2? Get()
        {
            // Fresh per handshake — GetOrCreateLeafCertificate reloads the on-disk cert and only
            // re-mints on expiry/SAN change, so this is cheap.
            using var fresh = _caService.GetOrCreateLeafCertificate();
            if (fresh is null)
            {
                return null;
            }

            lock (_gate)
            {
                if (_served is null || _servedThumbprint != fresh.Thumbprint)
                {
                    _served?.Dispose();
                    _served = ToSchannelUsable(fresh);
                    _servedThumbprint = fresh.Thumbprint;
                }
                return _served;
            }
        }

        /// <summary>
        /// Re-imports the cert through a PFX so its private key lands in a persisted CNG container
        /// SChannel can use. Returns null if the export/import fails (degraded: that handshake
        /// gets no cert and is rejected, but the listener stays up).
        /// </summary>
        private static X509Certificate2? ToSchannelUsable(X509Certificate2 cert)
        {
            try
            {
                var pfx = cert.Export(X509ContentType.Pfx);
                return new X509Certificate2(
                    pfx,
                    (string?)null,
                    X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
            }
            catch
            {
                return null;
            }
        }
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
    /// <param name="enableHttps">See <see cref="NodeFront.RegisterServices"/>.</param>
    /// <param name="dataPath">See <see cref="NodeFront.RegisterServices"/>.</param>
    public static NodeFront Build(
        WebApplicationBuilder builder,
        IReadOnlyDictionary<string, ReadyFileInfo> children,
        bool enableHttps = false,
        string? dataPath = null)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));
        if (children == null) throw new ArgumentNullException(nameof(children));

        var front = new NodeFront(children);
        front.RegisterServices(builder.Services, enableHttps, dataPath);
        builder.Services.AddSingleton(front);

        return front;
    }
}
