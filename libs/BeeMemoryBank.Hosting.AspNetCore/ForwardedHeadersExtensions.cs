using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Hosting.AspNetCore;

/// <summary>
/// Extension methods for configuring forwarded-headers trust in ASP.NET Core applications.
/// </summary>
public static class ForwardedHeadersExtensions
{
    private sealed class LoopbackForwardedHeadersMarker { }

    /// <summary>
    /// Configures <see cref="ForwardedHeadersOptions"/> to trust X-Forwarded-For and
    /// X-Forwarded-Proto from the hops this deployment declares trustworthy. Two independent
    /// opt-ins, either or both:
    /// <list type="bullet">
    /// <item><description>
    /// Loopback (127.0.0.1, ::1, IPv4-mapped loopback) — configuration
    /// "BeeMemoryBank:TrustLoopbackForwardedHeaders" or "BMB_TRUST_LOOPBACK_FORWARDED_HEADERS=true".
    /// For a reverse proxy sharing the host with the node, e.g. the desktop Node front.
    /// </description></item>
    /// <item><description>
    /// An explicit address/CIDR list — "BMB_TRUSTED_PROXIES" (or "BeeMemoryBank:TrustedProxies"),
    /// e.g. <c>172.16.0.0/12</c>. Required under Docker with a published port: proxied traffic
    /// arrives from the bridge gateway, never from loopback, so loopback trust alone leaves every
    /// external client sharing one apparent IP — and therefore one rate-limit bucket, which turns
    /// per-IP throttling into a global denial-of-service lever (one client can exhaust the sync
    /// challenge budget for the whole mesh, or the login budget for every user).
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// Trust here is transitive: anything that can reach the port from a trusted address can claim
    /// any client IP and shed its own limits. Name the proxy (or the bridge network it arrives
    /// from) and nothing wider. ForwardLimit is 1 — exactly one hop is believed, so a client that
    /// pre-seeds its own X-Forwarded-For cannot prepend a fake chain through the real proxy.
    /// </para>
    /// </summary>
    public static IServiceCollection AddLoopbackForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        bool trustLoopback = configuration.GetValue<bool>("BeeMemoryBank:TrustLoopbackForwardedHeaders") ||
                         string.Equals(Environment.GetEnvironmentVariable("BMB_TRUST_LOOPBACK_FORWARDED_HEADERS"), "true", StringComparison.OrdinalIgnoreCase);

        var trustedProxiesRaw = Environment.GetEnvironmentVariable("BMB_TRUSTED_PROXIES")
                                ?? configuration["BeeMemoryBank:TrustedProxies"];
        var trustedProxies = TrustedProxyParser.Parse(trustedProxiesRaw, out var invalidProxies);

        foreach (var bad in invalidProxies)
        {
            // Console rather than ILogger: this runs during service registration, before any
            // logging provider is built. A typo must be visible but must not fail startup —
            // dropping an entry only ever narrows trust.
            Console.Error.WriteLine(
                $"[BMB_TRUSTED_PROXIES] Ignoring unparsable entry '{bad}'. Expected an IP address or CIDR network, e.g. 172.16.0.0/12.");
        }

        if (trustLoopback || trustedProxies.Count > 0)
        {
            services.AddSingleton<LoopbackForwardedHeadersMarker>();

            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

                // Exactly one hop. This is also the framework default, but stating it makes the
                // intent explicit: we believe the nearest trusted proxy about who the client is,
                // and nothing it forwards on behalf of a further hop.
                options.ForwardLimit = 1;

                // Clear defaults so trust is exactly what this deployment declared, nothing more.
                options.KnownProxies.Clear();

                // Clear obsolete KnownNetworks to avoid conflicts
#pragma warning disable CS0618
#pragma warning disable ASPDEPR005
                options.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005
#pragma warning restore CS0618

                // Use the modern KnownIPNetworks collection introduced in .NET 8
                options.KnownIPNetworks.Clear();

                if (trustLoopback)
                {
                    options.KnownProxies.Add(IPAddress.Loopback);
                    options.KnownProxies.Add(IPAddress.IPv6Loopback);
                    options.KnownProxies.Add(IPAddress.Parse("::ffff:127.0.0.1"));

                    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Loopback, 8));
                    options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("::ffff:127.0.0.0"), 104));
                }

                // Each IPv4 entry is registered twice: once as itself, and once as its IPv4-mapped
                // IPv6 form. A dual-stack listener (ASPNETCORE_URLS on "*" or "[::]") reports an
                // incoming IPv4 connection as ::ffff:a.b.c.d, and a KnownIPNetworks entry of a
                // different address family does not match it — so a deployment that correctly
                // declared its proxy as 172.16.0.0/12 would silently have its X-Forwarded-For
                // ignored and fall right back into the one-bucket-for-everyone failure this
                // variable exists to fix. The mapped form covers exactly the same hosts, so this
                // widens nothing. (The loopback branch above already does the same thing, which is
                // where the pattern comes from.)
                foreach (var entry in trustedProxies)
                {
                    if (entry.Address is { } addr)
                    {
                        options.KnownProxies.Add(addr);
                        if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            options.KnownProxies.Add(addr.MapToIPv6());
                    }

                    if (entry.Network is { } net)
                    {
                        options.KnownIPNetworks.Add(net);
                        if (net.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            options.KnownIPNetworks.Add(new System.Net.IPNetwork(
                                net.BaseAddress.MapToIPv6(), net.PrefixLength + 96));
                    }
                }
            });
        }

        return services;
    }

    /// <summary>
    /// Registers the <see cref="ForwardedHeadersMiddleware"/> in the pipeline if loopback forwarded headers
    /// support has been explicitly enabled via <see cref="AddLoopbackForwardedHeaders"/>.
    /// </summary>
    public static IApplicationBuilder UseLoopbackForwardedHeaders(this IApplicationBuilder app)
    {
        if (app.ApplicationServices.GetService<LoopbackForwardedHeadersMarker>() != null)
        {
            app.UseForwardedHeaders();

            // Say out loud whose word we take for the client IP. Every per-IP rate limit in the
            // product depends on this being right, and getting it wrong is silent in both
            // directions: too narrow and all clients share one bucket, too wide and anyone can
            // forge their way out of one.
            var opts = app.ApplicationServices
                .GetService<Microsoft.Extensions.Options.IOptions<ForwardedHeadersOptions>>()?.Value;
            var proxies = opts is null
                ? []
                : opts.KnownProxies.Select(p => p.ToString())
                      .Concat(opts.KnownIPNetworks.Select(n => $"{n.BaseAddress}/{n.PrefixLength}"))
                      .ToList();
            Console.WriteLine(proxies.Count > 0
                ? $"[forwarded-headers] Trusting X-Forwarded-For from: {string.Join(", ", proxies)}"
                : "[forwarded-headers] Enabled but no trusted hop configured — per-IP rate limits key on the direct peer.");
        }
        else
        {
            Console.WriteLine(
                "[forwarded-headers] Disabled — per-IP rate limits key on the direct peer. Behind a reverse " +
                "proxy (including Docker port publishing) set BMB_TRUSTED_PROXIES, or every client shares one bucket.");
        }

        return app;
    }
}
