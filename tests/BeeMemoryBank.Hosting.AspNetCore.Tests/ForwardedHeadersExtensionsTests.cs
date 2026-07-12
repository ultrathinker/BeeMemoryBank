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
}
