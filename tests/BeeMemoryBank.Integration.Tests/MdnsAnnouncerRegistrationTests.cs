using BeeMemoryBank.Infrastructure.Mdns;
using BeeMemoryBank.Infrastructure.Network;
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

    // What bmbd tells the Api of a desktop node: announce only while the profile's "Devices on my network" setting is on.
    private sealed class FollowTheSettingFactory : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("BMB_MDNS_FOLLOWS_NETWORK_SETTING", "1");
        }
    }

    [Fact]
    public void Announcer_RunsByDefault()
    {
        using var factory = new BmbWebApplicationFactory();
        factory.Services.GetServices<IHostedService>().Should().ContainSingle(s => s is MdnsAnnouncer);
    }

    // Since LAN sync (docs/design/lan-peer-sync.md 5, D5): the desktop node's record follows what its LAN listener REALLY serves (network.listening.json,
    // written by the node), not a setting: the sync surface on trusted networks and/or the web surface, on the port the listener holds.
    [Fact]
    public void Announcer_OfADesktopNode_FollowsTheListenersState_WithoutARestart()
    {
        using var factory = new FollowTheSettingFactory();
        factory.Services.GetServices<IHostedService>().Should().ContainSingle(s => s is MdnsAnnouncer);
        var options = factory.Services.GetRequiredService<MdnsAnnouncerOptions>();
        var listening = new LanListenerState(factory.DataPath);

        options.Announcement.Should().NotBeNull();
        options.RefreshInterval.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(10), "a switch must show on the network within seconds");
        options.Announcement!().Should().BeNull("nothing listens: nothing is announced");

        listening.Set(new LanListenerSnapshot(true, 6000, Sync: true, Web: false, Join: false, SyncAddresses: ["192.168.1.42"]));
        options.Announcement!().Should().Be(new MdnsAnnouncement(6000, Sync: true, Web: false), "the port the listener holds, the sync surface only");

        listening.Set(new LanListenerSnapshot(true, 6000, Sync: true, Web: true, Join: false, SyncAddresses: ["192.168.1.42"]));
        options.Announcement!().Should().Be(new MdnsAnnouncement(6000, Sync: true, Web: true));

        listening.Set(false);
        options.Announcement!().Should().BeNull("the record is withdrawn as soon as the listener is gone");
    }

    // INT-05: "Devices on my network" is on, but the node could not open the port (another program has it): the node did not mark a listener as up, so the
    // announcer must not publish an address nothing of ours serves.
    [Fact]
    public void Announcer_OfADesktopNode_AnnouncesNothing_WhileNoListenerIsUp_EvenWithTheSettingOn()
    {
        using var factory = new FollowTheSettingFactory();
        var options = factory.Services.GetRequiredService<MdnsAnnouncerOptions>();
        new NodeNetworkSettingsStore(factory.DataPath).Save(new NodeNetworkSettings(DevicesOnMyNetwork: true));

        options.Announcement!().Should().BeNull("the setting is on but nothing is listening: a dead endpoint must not be announced");
    }

    [Fact]
    public void Announcer_OfADesktopNode_ASyncSurfaceWithNoTrustedAddress_IsNotAnnounced()
    {
        using var factory = new FollowTheSettingFactory();
        var options = factory.Services.GetRequiredService<MdnsAnnouncerOptions>();
        new LanListenerState(factory.DataPath).Set(new LanListenerSnapshot(true, 5311, Sync: false, Web: false, Join: true, SyncAddresses: []));

        options.Announcement!().Should().BeNull("only the join door is open: it is joined by its code, not found on the network");
    }

    [Fact]
    public void Announcer_OfADesktopNode_TheStateOfAnOlderBuild_IsTheWebSurfaceOnTheDefaultPort()
    {
        using var factory = new FollowTheSettingFactory();
        var options = factory.Services.GetRequiredService<MdnsAnnouncerOptions>();
        File.WriteAllText(Path.Combine(factory.DataPath, LanListenerState.FileName), "{\"listening\":true}");

        options.Announcement!().Should().Be(new MdnsAnnouncement(LanPorts.Default, Sync: false, Web: true));
    }

    [Fact]
    public void Announcer_OfAServerOrDocker_HasNoGate_AndIsDecidedByTheDeploymentAlone()
    {
        using var factory = new BmbWebApplicationFactory();

        factory.Services.GetRequiredService<MdnsAnnouncerOptions>().AnnounceGate.Should().BeNull();
        factory.Services.GetRequiredService<MdnsAnnouncerOptions>().Announcement.Should().BeNull("a server has no LAN listener");
    }

    [Fact]
    public void Announcer_DoesNotRun_WhenBmbdSaysNothingIsReachableFromTheNetwork()
    {
        using var factory = new MdnsOffFactory();
        factory.Services.GetServices<IHostedService>().Should().NotContain(s => s is MdnsAnnouncer);
    }
}
