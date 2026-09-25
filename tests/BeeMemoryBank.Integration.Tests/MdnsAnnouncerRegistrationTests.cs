using BeeMemoryBank.Infrastructure.Mdns;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The LAN announcer is on by default (servers, Docker) and off when bmbd reports that nothing it
/// serves is reachable from the network — the default desktop install. There the multicast socket
/// only buys a Windows Firewall prompt right after installation, for a port no peer can reach.
/// </summary>
public class MdnsAnnouncerRegistrationTests
{
    private sealed class MdnsOffFactory : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("BMB_MDNS_ENABLED", "false");
        }
    }

    [Fact]
    public void Announcer_RunsByDefault()
    {
        using var factory = new BmbWebApplicationFactory();
        factory.Services.GetServices<IHostedService>().Should().ContainSingle(s => s is MdnsAnnouncer);
    }

    [Fact]
    public void Announcer_DoesNotRun_WhenBmbdSaysNothingIsReachableFromTheNetwork()
    {
        using var factory = new MdnsOffFactory();
        factory.Services.GetServices<IHostedService>().Should().NotContain(s => s is MdnsAnnouncer);
    }
}
