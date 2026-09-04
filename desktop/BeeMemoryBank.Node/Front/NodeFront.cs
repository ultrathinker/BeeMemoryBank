using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
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
using Yarp.ReverseProxy.Model;
using Yarp.ReverseProxy.Transforms;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.Acme;
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
        // When HTTPS is active, also create a challenge persister so the cert selector can read
        // the shared challenge file written by the Api process during a TLS-ALPN-01 validation.
        var challengePersister = (leafProvider != null && dataPath != null)
            ? new AcmeChallengePersister(dataPath)
            : null;

        // The ALPN protocol "acme-tls/1" must be included in ApplicationProtocols so that Kestrel
        // will negotiate it when the Let's Encrypt CA probes the listener during TLS-ALPN-01
        // validation (RFC 8737 §4). Without it the TLS handshake will reject the protocol.
        // This is deliberately added to every HTTPS handshake (not just challenge ones) because
        // SslClientHelloInfo does not expose the client's offered ALPN list so we cannot filter
        // at the protocol-offer stage; selection falls back to SNI matching in the cert selector.
        var acmeTlsAlpnProtocol = new SslApplicationProtocol(
            TlsAlpn01CertificateBuilder.AcmeTlsAlpnProtocol);

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
                        // ALPN: advertise acme-tls/1 so the ACME CA can negotiate it during
                        // TLS-ALPN-01 validation probes. HTTP/1.1 and h2 must stay in the list
                        // so normal browser/client traffic continues to work.
                        httpsOptions.SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                            | System.Security.Authentication.SslProtocols.Tls13;
                        httpsOptions.OnAuthenticate = (_, sslOptions) =>
                        {
                            // Add acme-tls/1 ahead of the standard protocols so ACME probes can
                            // negotiate it while normal clients pick HTTP/1.1 or h2 instead.
                            var protocols = new List<SslApplicationProtocol>
                            {
                                acmeTlsAlpnProtocol,
                                SslApplicationProtocol.Http11,
                                SslApplicationProtocol.Http2,
                            };
                            sslOptions.ApplicationProtocols = protocols;
                        };

                        // Cert selector: check the shared challenge file first (cross-process
                        // TLS-ALPN-01 hand-off), then fall back to the normal LocalCa leaf.
                        httpsOptions.ServerCertificateSelector = (_, serverName) =>
                        {
                            // Check whether a TLS-ALPN-01 challenge is currently in flight for
                            // this SNI. The persister reads the shared file fresh on every call
                            // (cheap: just a file read + small JSON parse) so no restart is needed
                            // when a challenge starts or ends.
                            if (challengePersister != null)
                            {
                                var challengeCert = challengePersister.TryReadChallengeCert(serverName);
                                if (challengeCert != null) return challengeCert;
                            }
                            return leafProvider.Get();
                        };
                    });
                });
            }
        });

        // Which paths reach the API rather than the Web UI. This is a ROUTING table, and routing
        // is a different question from authorisation: "which of the two processes serves this
        // path" versus "may a caller without the internal key reach it". They were briefly the
        // same list, built from PublicSurface, and the moment PublicSurface got more precise —
        // listing the peer sync routes individually instead of publishing /api/sync/** wholesale —
        // the front stopped routing /api/sync/status and its neighbours to the API at all. They
        // fell through to the web catch-all and 404'd, with nothing in the security model asking
        // for that.
        //
        // So the front routes by ownership, which is a fact about the deployment and does not
        // move: the API process owns everything under /api, /mcp and /health, plus /node/update.
        // The API's own PublicSurfaceMiddleware then decides who may actually reach each path, in
        // the one place that decision belongs. This is still not a hand-maintained copy of a
        // security list — it is four prefixes that change only if a whole surface moves between
        // processes.
        //
        // (What the shared list DID fix stays fixed: the earlier hand-written table missed
        // /api/snapshots/restore, so a network-wide restore could not seed a desktop node. A
        // prefix cannot miss a route inside it.)
        var apiOwnedPrefixes = new[]
        {
            "/api/{**rest}",
            "/mcp",
            "/mcp/{**rest}",
            "/health",
            // Requires the internal key, so it is deliberately NOT in PublicSurface — and is
            // exactly why routing cannot be derived from that list. See the transform in
            // RegisterServices and the loopback check in MapEndpoints.
            "/node/update/{**rest}",
        };

        var routes = apiOwnedPrefixes
            .Select((pattern, index) => new RouteConfig
            {
                RouteId = pattern.StartsWith("/node/update", StringComparison.Ordinal)
                    ? NodeUpdateRouteId
                    : $"api-owned-{index}",
                ClusterId = "Api",
                Match = new RouteMatch { Path = pattern },
                Order = 1
            })
            .Append(new RouteConfig
            {
                RouteId = "web-catchall",
                ClusterId = "Web",
                Match = new RouteMatch { Path = "{**catchall}" },
                Order = 1000 // Lowest priority
            })
            .ToArray();

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
            .LoadFromMemory(routes, clusters)
            // Strip the node's own trust headers off every INBOUND request, on every route, before
            // it is forwarded. Nothing outside this process may set them: X-Internal-Key is the
            // secret that makes a caller "the node itself", and the API honours X-User-Id /
            // X-User-Role / X-User-DisplayName only from a caller that presented it. Until now the
            // front passed request headers through untouched, so a client's own copies travelled to
            // the API and were rejected there and only there — the entire defence was that the key
            // is random and required.
            //
            // That is one check away from a full authentication bypass, and it is precisely the
            // shape that made InternalKeyValidator's deleted "trust any loopback caller" fallback
            // dangerous: restore that fallback and every proxied request arrives at the API from
            // 127.0.0.1, carrying whatever role the client asked for. Removing the headers here
            // means the front never forwards the question at all.
            //
            // Only the Api cluster needs this in principle — but it is applied to every route
            // deliberately: the Web layer reads no such header today, and a rule with an exception
            // is a rule someone has to re-derive when a route is added.
            .AddTransforms(context =>
            {
                // ONE exception, and it is the reason the rule above says "every route" rather
                // than "the Api cluster": /node/update/* is the desktop tray talking to its own
                // node, and the tray authenticates by sending the internal key it read from the
                // key file next to the database. Stripping it there did not close a hole, it
                // removed the tray's only credential -- the update check answered 404 (the key is
                // gone, so PublicSurface sees an anonymous caller and the path is not public) and
                // ProfileSwitchService.IsUpdateApplyingAsync, which fails open on any non-success,
                // stopped guarding profile switches during an update apply.
                //
                // Safe because the route is loopback-only (see the check in MapEndpoints): a
                // caller who can reach it can already read the key file itself, so accepting the
                // header adds no authority it does not have. Every other route keeps the strip,
                // including everything reachable from outside this machine.
                if (string.Equals(context.Route.RouteId, NodeUpdateRouteId, StringComparison.Ordinal))
                    return;

                foreach (var header in StrippedInboundIdentityHeaders)
                    context.AddRequestHeaderRemove(header);
            });
    }

    /// <summary>
    /// Headers the front removes from every inbound request before proxying. See the transform in
    /// <see cref="RegisterServices"/> for why. Public so the test that asserts the stripping walks
    /// the same list the proxy does, rather than a hand-copied one that can drift.
    /// </summary>
    /// <summary>
    /// Route id of the tray's own <c>/node/update/*</c> passthrough. Named rather than repeated as
    /// a literal because three places have to agree on it: the route, the transform that does NOT
    /// strip identity headers from it, and the loopback check that makes that exemption safe.
    /// </summary>
    public const string NodeUpdateRouteId = "api-node-update";

    public static readonly string[] StrippedInboundIdentityHeaders =
    [
        "X-Internal-Key",
        "X-User-Id",
        "X-User-Role",
        "X-User-DisplayName",
    ];

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
        endpoints.MapReverseProxy(proxyPipeline =>
        {
            // The half of the /node/update/* exemption that makes it safe. That route is the one
            // place the front forwards a caller's own X-Internal-Key and X-User-Role instead of
            // removing them, so it must not be reachable from anywhere but this machine. 404
            // rather than 403: an off-machine caller learns nothing about whether the route exists,
            // matching what PublicSurface answers for everything else it does not publish.
            proxyPipeline.Use(async (context, next) =>
            {
                var routeId = context.GetReverseProxyFeature().Route.Config.RouteId;
                if (string.Equals(routeId, NodeUpdateRouteId, StringComparison.Ordinal)
                    && !LoopbackIpMatcher.IsLoopback(context.Connection.RemoteIpAddress))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await next();
            });
        });
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
                    // Deliberately NOT disposing the retired cert here: Kestrel may still be
                    // mid-handshake with a client holding a reference to it (ServerCertificateSelector
                    // runs outside this lock's critical section once it returns). Rotation happens
                    // only ~every 90 days, so leaving the old instance for the GC/finalizer to reclaim
                    // is a negligible cost next to the risk of disposing a cert an in-flight TLS
                    // handshake is still reading.
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
