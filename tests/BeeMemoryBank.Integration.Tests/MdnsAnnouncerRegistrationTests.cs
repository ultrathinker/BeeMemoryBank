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

    [Fact]
    public void Announcer_OfADesktopNode_FollowsTheProfilesNetworkSettingFile_WithoutARestart()
    {
        if (Environment.GetEnvironmentVariable(NetworkExposure.EnvironmentVariable) == "1") return; // this machine forces it on

        using var factory = new FollowTheSettingFactory();
        factory.Services.GetServices<IHostedService>().Should().ContainSingle(s => s is MdnsAnnouncer);
        var options = factory.Services.GetRequiredService<MdnsAnnouncerOptions>();
        var store = new NodeNetworkSettingsStore(factory.DataPath);
        var listening = new LanListenerState(factory.DataPath);

        options.AnnounceGate.Should().NotBeNull();
        options.RefreshInterval.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(10), "a switch must show on the network within seconds");
        options.AnnounceGate!().Should().BeFalse("a node nobody opened announces nothing");

        store.Save(new NodeNetworkSettings(DevicesOnMyNetwork: true));
        listening.Set(true); // what the node writes once its listener is really up
        options.AnnounceGate!().Should().BeTrue("the gate reads the files the node wrote, on every call");

        store.Save(new NodeNetworkSettings(DevicesOnMyNetwork: false));
        options.AnnounceGate!().Should().BeFalse();
    }

    [Fact]
    public void Announcer_OfADesktopNode_FollowsTheListenersState_ComesUpWhenItDoes_AndWithdrawsWhenItStops()
    {
        if (Environment.GetEnvironmentVariable(NetworkExposure.EnvironmentVariable) == "1") return; // this machine forces it on

        using var factory = new FollowTheSettingFactory();
        var options = factory.Services.GetRequiredService<MdnsAnnouncerOptions>();
        var listening = new LanListenerState(factory.DataPath);
        new NodeNetworkSettingsStore(factory.DataPath).Save(new NodeNetworkSettings(DevicesOnMyNetwork: true));

        options.AnnounceGate!().Should().BeFalse("port taken: the node never marked a listener as up");

        listening.Set(true); // the person pressed Try again after closing the other program, and the bind worked
        options.AnnounceGate!().Should().BeTrue();

        listening.Set(false); // the listener stopped or failed
        options.AnnounceGate!().Should().BeFalse("the record is withdrawn as soon as the listener is gone, the setting still being on");
    }

    [Fact]
    public void Announcer_OfADesktopNode_UnderTheEnvironmentOverride_AlsoFollowsTheListener()
    {
        var previous = Environment.GetEnvironmentVariable(NetworkExposure.EnvironmentVariable);
        Environment.SetEnvironmentVariable(NetworkExposure.EnvironmentVariable, "1");
        try
        {
            using var factory = new FollowTheSettingFactory();
            var options = factory.Services.GetRequiredService<MdnsAnnouncerOptions>();
            var listening = new LanListenerState(factory.DataPath);

            options.AnnounceGate!().Should().BeFalse("BMB_HTTPS_ENABLED=1 but the front could not bind 5311: nothing is announced");
            listening.Set(true);
            options.AnnounceGate!().Should().BeTrue();
        }
        finally { Environment.SetEnvironmentVariable(NetworkExposure.EnvironmentVariable, previous); }
    }

    // INT-05: the setting says "on", but the node's listener could not open port 5311 (another program has it). The node did
    // not mark a listener as up, so the announcer must not publish https://host:5311 for a port nothing of ours serves.
    [Fact]
    public void Announcer_OfADesktopNode_AnnouncesNothing_WhileNoListenerIsUp_EvenWithTheSettingOn()
    {
        if (Environment.GetEnvironmentVariable(NetworkExposure.EnvironmentVariable) == "1") return; // this machine forces it on

        using var factory = new FollowTheSettingFactory();
        var options = factory.Services.GetRequiredService<MdnsAnnouncerOptions>();
        new NodeNetworkSettingsStore(factory.DataPath).Save(new NodeNetworkSettings(DevicesOnMyNetwork: true));

        options.AnnounceGate!().Should().BeFalse("the setting is on but nothing is listening: a dead endpoint must not be announced");
    }

    [Fact]
    public void Announcer_OfAServerOrDocker_HasNoGate_AndIsDecidedByTheDeploymentAlone()
    {
        using var factory = new BmbWebApplicationFactory();

        factory.Services.GetRequiredService<MdnsAnnouncerOptions>().AnnounceGate.Should().BeNull();
    }

    [Fact]
    public void Announcer_DoesNotRun_WhenBmbdSaysNothingIsReachableFromTheNetwork()
    {
        using var factory = new MdnsOffFactory();
        factory.Services.GetServices<IHostedService>().Should().NotContain(s => s is MdnsAnnouncer);
    }
}
