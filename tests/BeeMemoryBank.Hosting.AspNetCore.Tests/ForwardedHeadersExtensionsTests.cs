using System.Net;
using BeeMemoryBank.Hosting.AspNetCore;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeeMemoryBank.Hosting.AspNetCore.Tests;

public class ForwardedHeadersExtensionsTests
{
    [Fact]
    public void AddLoopbackForwardedHeaders_WhenDisabled_ShouldNotRegisterMarkerOrConfigureOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build(); // No config value set

        // Act
        services.AddLoopbackForwardedHeaders(config);

        // Assert
        var serviceProvider = services.BuildServiceProvider();
        var markerType = typeof(ForwardedHeadersExtensions).GetNestedType("LoopbackForwardedHeadersMarker", System.Reflection.BindingFlags.NonPublic);
        markerType.Should().NotBeNull();
        
        serviceProvider.GetService(markerType!).Should().BeNull();
        
        var options = serviceProvider.GetService<IOptions<ForwardedHeadersOptions>>();
        if (options != null)
        {
            // Default options shouldn't be altered
            options.Value.ForwardedHeaders.Should().Be(ForwardedHeaders.None);
        }
    }

    [Fact]
    public void AddLoopbackForwardedHeaders_WhenEnabledViaConfig_ShouldRegisterMarkerAndConfigureOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "BeeMemoryBank:TrustLoopbackForwardedHeaders", "true" }
            })
            .Build();

        // Act
        services.AddLoopbackForwardedHeaders(config);

        // Assert
        var serviceProvider = services.BuildServiceProvider();
        var markerType = typeof(ForwardedHeadersExtensions).GetNestedType("LoopbackForwardedHeadersMarker", System.Reflection.BindingFlags.NonPublic);
        markerType.Should().NotBeNull();
        
        serviceProvider.GetService(markerType!).Should().NotBeNull();

        var options = serviceProvider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        options.ForwardedHeaders.Should().Be(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
        
        options.KnownProxies.Should().Contain(IPAddress.Loopback);
        options.KnownProxies.Should().Contain(IPAddress.IPv6Loopback);
        options.KnownProxies.Should().Contain(IPAddress.Parse("::ffff:127.0.0.1"));

        options.KnownIPNetworks.Should().Contain(new System.Net.IPNetwork(IPAddress.Loopback, 8));
        options.KnownIPNetworks.Should().Contain(new System.Net.IPNetwork(IPAddress.Parse("::ffff:127.0.0.0"), 104));
    }

    [Fact]
    public void AddLoopbackForwardedHeaders_WhenEnabledViaEnvVar_ShouldRegisterMarkerAndConfigureOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        Environment.SetEnvironmentVariable("BMB_TRUST_LOOPBACK_FORWARDED_HEADERS", "true");

        try
        {
            // Act
            services.AddLoopbackForwardedHeaders(config);

            // Assert
            var serviceProvider = services.BuildServiceProvider();
            var markerType = typeof(ForwardedHeadersExtensions).GetNestedType("LoopbackForwardedHeadersMarker", System.Reflection.BindingFlags.NonPublic);
            markerType.Should().NotBeNull();
            
            serviceProvider.GetService(markerType!).Should().NotBeNull();

            var options = serviceProvider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
            options.ForwardedHeaders.Should().Be(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BMB_TRUST_LOOPBACK_FORWARDED_HEADERS", null);
        }
    }

    // ─── BMB_TRUSTED_PROXIES ────────────────────────────────────────────────
    //
    // Loopback trust alone is useless under Docker with a published port: the proxy's traffic
    // arrives on the container's bridge interface, so X-Forwarded-For is never believed and every
    // external client shares the bridge gateway's address — one rate-limit bucket for the entire
    // internet, which turns per-IP throttling into a global availability lever.

    [Fact]
    public void TrustedProxies_EnableForwardedHeaders_EvenWithoutLoopbackTrust()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        Environment.SetEnvironmentVariable("BMB_TRUSTED_PROXIES", "172.16.0.0/12");

        try
        {
            services.AddLoopbackForwardedHeaders(config);

            var sp = services.BuildServiceProvider();
            var markerType = typeof(ForwardedHeadersExtensions)
                .GetNestedType("LoopbackForwardedHeadersMarker", System.Reflection.BindingFlags.NonPublic)!;
            sp.GetService(markerType).Should().NotBeNull("the list alone must switch the middleware on");

            var options = sp.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
            options.ForwardedHeaders.Should().Be(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
            options.ForwardLimit.Should().Be(1, "only the nearest trusted hop is believed");
            options.KnownIPNetworks.Should().Contain(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));

            options.KnownProxies.Should().NotContain(IPAddress.Loopback,
                "loopback was not requested — trust must be exactly what was declared");
        }
        finally
        {
            Environment.SetEnvironmentVariable("BMB_TRUSTED_PROXIES", null);
        }
    }

    [Fact]
    public void TrustedProxies_AndLoopback_CanBeCombined()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        Environment.SetEnvironmentVariable("BMB_TRUST_LOOPBACK_FORWARDED_HEADERS", "true");
        Environment.SetEnvironmentVariable("BMB_TRUSTED_PROXIES", "10.9.9.9");

        try
        {
            services.AddLoopbackForwardedHeaders(config);

            var options = services.BuildServiceProvider()
                .GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

            options.KnownProxies.Should().Contain(IPAddress.Loopback);
            options.KnownProxies.Should().Contain(IPAddress.Parse("10.9.9.9"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BMB_TRUST_LOOPBACK_FORWARDED_HEADERS", null);
            Environment.SetEnvironmentVariable("BMB_TRUSTED_PROXIES", null);
        }
    }

    [Fact]
    public void AnIPv4TrustedProxy_IsAlsoRegisteredInItsIPv4MappedForm()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        Environment.SetEnvironmentVariable("BMB_TRUSTED_PROXIES", "172.16.0.0/12, 10.0.0.7");

        try
        {
            services.AddLoopbackForwardedHeaders(config);

            var options = services.BuildServiceProvider()
                .GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

            // A dual-stack listener reports an incoming IPv4 connection as ::ffff:a.b.c.d, and a
            // KnownIPNetworks entry of a different address family does not match it — so without
            // the mapped twin, a correctly-configured deployment would silently ignore
            // X-Forwarded-For and fall back to one bucket for every client.
            options.KnownIPNetworks.Should().Contain(new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
            options.KnownIPNetworks.Should().Contain(
                new System.Net.IPNetwork(IPAddress.Parse("172.16.0.0").MapToIPv6(), 108));

            options.KnownProxies.Should().Contain(IPAddress.Parse("10.0.0.7"));
            options.KnownProxies.Should().Contain(IPAddress.Parse("10.0.0.7").MapToIPv6());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BMB_TRUSTED_PROXIES", null);
        }
    }

    [Fact]
    public void AnUnparsableTrustedProxy_IsIgnoredWithoutFailingStartup()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        Environment.SetEnvironmentVariable("BMB_TRUSTED_PROXIES", "nonsense");

        try
        {
            // A typo in a deployment variable must not take the node down; it just narrows trust,
            // which is the safe direction. With nothing left to trust, the middleware stays off.
            services.AddLoopbackForwardedHeaders(config);

            var sp = services.BuildServiceProvider();
            var markerType = typeof(ForwardedHeadersExtensions)
                .GetNestedType("LoopbackForwardedHeadersMarker", System.Reflection.BindingFlags.NonPublic)!;
            sp.GetService(markerType).Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("BMB_TRUSTED_PROXIES", null);
        }
    }
}
