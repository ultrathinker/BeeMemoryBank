using System;
using System.IO;
using System.Linq;
using System.Net;
using BeeMemoryBank.Infrastructure.Network;
using BeeMemoryBank.Node;

namespace BeeMemoryBank.Node.Tests;

/// <summary>Where the LAN listener binds (docs/design/lan-peer-sync.md section 6): the trusted addresses for the sync surface, every LAN one for the door
/// (D2), every interface for the web surface.</summary>
public sealed class TrustedNetworkBindPlannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_bindplanner_" + Guid.NewGuid().ToString("N"));
    private static readonly LanInterface HomeWifi =
        new("en0", "Wi-Fi", IPAddress.Parse("192.168.1.42"), 24, IPAddress.Parse("192.168.1.1"), LanInterfaceKind.Lan);
    private static readonly LanInterface Docker =
        new("bridge100", "bridge100", IPAddress.Parse("192.168.64.1"), 24, null, LanInterfaceKind.Virtual);

    public TrustedNetworkBindPlannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private TrustedNetworks Networks(params LanInterface[] interfaces) =>
        new(new Fixed(interfaces), new TrustedNetworkStore(_dir), null, _ => "00:11:22:33:44:55");

    [Fact]
    public void ThePlanner_BindsTheTrustedAddressesOnly_TheDoorEveryLanOne_AndTheWebSurfaceEverything()
    {
        var nets = Networks(interfaces: [HomeWifi, Docker]);
        var planner = new TrustedNetworkBindPlanner(nets);

        planner.Plan(sync: true, join: false, web: false).Bind.Should().BeEmpty("nothing is trusted yet");
        planner.Plan(sync: true, join: true, web: false).Bind.Should().Equal(HomeWifi.Address);
        planner.Plan(sync: true, join: true, web: false).SyncAddresses.Should().BeEmpty("the door's network is not trusted: the sync surface stays off there");

        var view = nets.Evaluate().First(v => v.Interface == HomeWifi);
        nets.Answer(view.Fingerprint!, view.Label, true, "asked");
        var plan = planner.Plan(sync: true, join: false, web: false);
        plan.Bind.Should().Equal(HomeWifi.Address);
        plan.SyncAddresses.Should().Equal(HomeWifi.Address);
        planner.Plan(sync: true, join: false, web: true).Bind.Should().Equal(IPAddress.Any);
        planner.Plan(sync: false, join: false, web: false).Bind.Should().BeEmpty();
    }

    [Fact]
    public void TheDoor_OpensOnAnUntrustedVpnToo_TheSyncSurfaceDoesNot()
    {
        // D2: the door opens on every usable network, trusted or not; a VPN is used for sync only with its opt-in (D7).
        var tailnet = new LanInterface("utun4", "utun4", IPAddress.Parse("100.100.1.2"), 32, null, LanInterfaceKind.Vpn);
        var planner = new TrustedNetworkBindPlanner(Networks(interfaces: [HomeWifi, tailnet, Docker]));

        var plan = planner.Plan(sync: true, join: true, web: false);

        plan.Bind.Should().BeEquivalentTo([HomeWifi.Address, tailnet.Address]);
        plan.SyncAddresses.Should().BeEmpty();
    }

    [Theory]
    [InlineData(LanInterfaceKind.Virtual, "192.168.1.42", "192.168.1.1", LanInterfaceKind.Lan)]    // vEthernet (External): the host's own network
    [InlineData(LanInterfaceKind.Virtual, "172.27.16.1", null, LanInterfaceKind.Virtual)]          // vEthernet (WSL), a Docker bridge
    [InlineData(LanInterfaceKind.Lan, "100.100.1.2", null, LanInterfaceKind.Vpn)]                   // a tailnet address on any adapter
    [InlineData(LanInterfaceKind.Lan, "192.168.1.42", "192.168.1.1", LanInterfaceKind.Lan)]
    public void AnAddressKind_FollowsItsAdapter_ButAVirtualOneWithTheGatewayIsTheLan(
        LanInterfaceKind adapter, string address, string? gateway, LanInterfaceKind expected)
    {
        LanInterfaceClassifier.KindOf(adapter, IPAddress.Parse(address), gateway is null ? null : IPAddress.Parse(gateway)).Should().Be(expected);
    }

    private sealed class Fixed(LanInterface[] interfaces) : ILanInterfaces
    {
        public System.Collections.Generic.IReadOnlyList<LanInterface> Current() => interfaces;
    }
}
