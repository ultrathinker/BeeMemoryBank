using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Hosting.AspNetCore;

/// <summary>
/// Extension methods for configuring loopback-only forwarded headers trust in ASP.NET Core applications.
/// </summary>
public static class ForwardedHeadersExtensions
{
    private sealed class LoopbackForwardedHeadersMarker { }

    /// <summary>
    /// Configures <see cref="ForwardedHeadersOptions"/> to trust X-Forwarded-For and X-Forwarded-Proto
    /// ONLY if they originate from loopback addresses (127.0.0.1, ::1, or IPv4-mapped loopback).
    /// Opt-in is controlled by configuration ("BeeMemoryBank:TrustLoopbackForwardedHeaders")
    /// or environment variable ("BMB_TRUST_LOOPBACK_FORWARDED_HEADERS" = "true").
    /// </summary>
    public static IServiceCollection AddLoopbackForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        bool isEnabled = configuration.GetValue<bool>("BeeMemoryBank:TrustLoopbackForwardedHeaders") ||
                         string.Equals(Environment.GetEnvironmentVariable("BMB_TRUST_LOOPBACK_FORWARDED_HEADERS"), "true", StringComparison.OrdinalIgnoreCase);

        if (isEnabled)
        {
            services.AddSingleton<LoopbackForwardedHeadersMarker>();

            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

                // Clear default values to ensure we ONLY trust loopback
                options.KnownProxies.Clear();
                options.KnownProxies.Add(IPAddress.Loopback);
                options.KnownProxies.Add(IPAddress.IPv6Loopback);
                options.KnownProxies.Add(IPAddress.Parse("::ffff:127.0.0.1"));

                // Clear obsolete KnownNetworks to avoid conflicts
#pragma warning disable CS0618
#pragma warning disable ASPDEPR005
                options.KnownNetworks.Clear();
#pragma warning restore ASPDEPR005
#pragma warning restore CS0618

                // Use the modern KnownIPNetworks collection introduced in .NET 8
                options.KnownIPNetworks.Clear();
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Loopback, 8));
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Parse("::ffff:127.0.0.0"), 104));
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
        }

        return app;
    }
}
