using System.Net;
using BeeMemoryBank.Infrastructure.Network;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Which networks the LAN sync surface answers on (docs/design/lan-peer-sync.md section 6, decisions D1 and D7): Windows' category decides on Windows
/// (Private and domain yes, Public no), the person's answer for the network's fingerprint elsewhere (and where Windows could not be asked); the network a
/// device was paired on is trusted by itself, a "not this network" is not overruled by a pairing; a VPN only with its opt-in; a virtual adapter never.
/// </summary>
public sealed class TrustedNetworksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_trustednets_" + Guid.NewGuid().ToString("N"));
    private const string Mac = "00:11:22:33:44:55";

    public TrustedNetworksTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static readonly LanInterface HomeWifi =
        new("{6A1D6C0E-1B2C-4D5E-8F90-0123456789AB}", "Wi-Fi", IPAddress.Parse("192.168.1.42"), 24, IPAddress.Parse("192.168.1.1"), LanInterfaceKind.Lan);
    private static readonly LanInterface Tailnet =
        new("utun4", "utun4", IPAddress.Parse("100.100.1.2"), 32, null, LanInterfaceKind.Vpn);
    private static readonly LanInterface Docker =
        new("bridge100", "bridge100", IPAddress.Parse("192.168.64.1"), 24, null, LanInterfaceKind.Virtual);

    private TrustedNetworks Networks(IWindowsNetworkCategories? windows = null, string? mac = Mac, params LanInterface[] interfaces) =>
        new(new Fixed(interfaces), new TrustedNetworkStore(_dir), windows, _ => mac);

    [Fact]
    public void OnAMac_AnUnknownPrivateNetwork_IsUntrusted_AndMayBeAsked_ThenYesTrustsIt()
    {
        var nets = Networks(interfaces: HomeWifi);
        var view = nets.Evaluate().Single();
        view.Trusted.Should().BeFalse();
        view.Reason.Should().Be(LanTrustReasons.Unknown);
        view.CanAsk.Should().BeTrue();

        nets.Answer(view.Fingerprint!, view.Label, true, "asked");

        nets.Evaluate().Single().Should().Match<LanNetworkView>(v => v.Trusted && v.Reason == LanTrustReasons.Trusted);
        nets.Answer(view.Fingerprint!, view.Label, null, "asked");
        nets.Evaluate().Single().Trusted.Should().BeFalse("forgotten: asked again");
    }

    [Fact]
    public void ThePairingNetwork_IsTrustedByItself_ButNotOverAPersonsNo()
    {
        var nets = Networks(interfaces: HomeWifi);
        nets.TrustNetworkOf(IPAddress.Parse("192.168.1.220"), "paired");   // the other side of the join, on the same subnet
        nets.Evaluate().Single().Trusted.Should().BeTrue();
        nets.Answers().Single().How.Should().Be("paired");

        var view = nets.Evaluate().Single();
        nets.Answer(view.Fingerprint!, view.Label, false, "asked");
        nets.TrustNetworkOf(IPAddress.Parse("192.168.1.220"), "paired");
        nets.Evaluate().Single().Reason.Should().Be(LanTrustReasons.Refused, "a pairing does not overrule 'Not this network'");
    }

    [Fact]
    public void AnotherRouterOnTheSameAddresses_IsAnotherNetwork()
    {
        var home = Networks(interfaces: HomeWifi);
        var view = home.Evaluate().Single();
        home.Answer(view.Fingerprint!, view.Label, true, "asked");

        // A café whose Wi-Fi uses the same 192.168.1.0/24 and the same router address, with another router.
        Networks(mac: "aa:bb:cc:dd:ee:ff", interfaces: HomeWifi).Evaluate().Single().Trusted.Should().BeFalse();
    }

    [Fact]
    public void ARouterWhoseHardwareAddressIsNotKnownYet_IsPending_NeverTrusted()
    {
        var view = Networks(mac: null, interfaces: HomeWifi).Evaluate().Single();

        view.Reason.Should().Be(LanTrustReasons.Pending);
        view.Trusted.Should().BeFalse();
        view.Fingerprint.Should().BeNull();
    }

    [Theory]
    [InlineData(WindowsNetworkCategory.Private, true, LanTrustReasons.WindowsPrivate)]
    [InlineData(WindowsNetworkCategory.DomainAuthenticated, true, LanTrustReasons.WindowsDomain)]
    [InlineData(WindowsNetworkCategory.Public, false, LanTrustReasons.WindowsPublic)]
    public void OnWindows_TheCategoryDecides_AndNoAnswerIsAsked(WindowsNetworkCategory category, bool trusted, string reason)
    {
        var windows = new Categories(new Dictionary<string, WindowsNetworkCategory>
        {
            [TrustedNetworks.NormalizeAdapterId(HomeWifi.Id)] = category,
        });

        var view = Networks(windows, interfaces: HomeWifi).Evaluate().Single();

        view.Trusted.Should().Be(trusted);
        view.Reason.Should().Be(reason);
        view.CanAsk.Should().BeFalse("Windows Settings is where the category is changed");
    }

    [Fact]
    public void OnWindows_WhenTheCategoriesCannotBeRead_ThePersonsAnswerDecides()
    {
        var nets = Networks(new Categories(null), interfaces: HomeWifi);

        var view = nets.Evaluate().Single();

        view.Reason.Should().Be(LanTrustReasons.Unknown);
        view.CanAsk.Should().BeTrue();
    }

    [Fact]
    public void AVpn_IsUsedOnlyWithItsOptIn_AndAVirtualAdapterNever()
    {
        var nets = Networks(interfaces: [HomeWifi, Tailnet, Docker]);
        var views = nets.Evaluate();
        views.Single(v => v.Interface == Tailnet).Should().Match<LanNetworkView>(v => !v.Trusted && v.Reason == LanTrustReasons.VpnOff && v.CanAsk);
        views.Single(v => v.Interface == Docker).Should().Match<LanNetworkView>(v => !v.Trusted && v.Reason == LanTrustReasons.Virtual && !v.CanAsk);

        var vpn = views.Single(v => v.Interface == Tailnet);
        nets.Answer(vpn.Fingerprint!, vpn.Label, true, "vpn");

        nets.Evaluate().Single(v => v.Interface == Tailnet).Trusted.Should().BeTrue();
    }

    private sealed class Fixed(LanInterface[] interfaces) : ILanInterfaces
    {
        public IReadOnlyList<LanInterface> Current() => interfaces;
    }

    private sealed class Categories(Dictionary<string, WindowsNetworkCategory>? map) : IWindowsNetworkCategories
    {
        public IReadOnlyDictionary<string, WindowsNetworkCategory> ByAdapterId() =>
            map ?? throw new System.Runtime.InteropServices.COMException("netprofm is not available");
    }
}
