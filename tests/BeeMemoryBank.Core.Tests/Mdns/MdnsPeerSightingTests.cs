using BeeMemoryBank.Infrastructure.Mdns;
using BeeMemoryBank.Mdns;

namespace BeeMemoryBank.Core.Tests.Mdns;

/// <summary>
/// Which mDNS records the desktop node's peer browse notes (docs/design/lan-peer-sync.md 7, 14): a paired node's, serving its sync routes over HTTPS, at
/// an IP address of a LAN or VPN. A record anyone on the network sends that names a stranger, or a host name (which an ordinary certificate could stand
/// for), is not noted at all.
/// </summary>
public class MdnsPeerSightingTests
{
    private static readonly Guid Peer = Guid.NewGuid();
    private static readonly IReadOnlySet<Guid> Paired = new HashSet<Guid> { Peer };

    private static MdnsNodeRecord Record(string host, Guid? node = null, bool https = true, string? surface = "sync") =>
        new(node ?? Peer, "", "2.8.0", https, host, 5311, surface);

    [Fact]
    public void APairedNodeAtALanOrVpnAddress_IsNoted()
    {
        MdnsPeerBrowser.IsPeerSighting(Record("192.168.1.20"), Paired).Should().BeTrue();
        MdnsPeerBrowser.IsPeerSighting(Record("100.100.1.2"), Paired).Should().BeTrue();
    }

    [Theory]
    [InlineData("attacker.example.com")]   // the SRV target as the host: a name, not an address
    [InlineData("desktop.local")]
    [InlineData("8.8.8.8")]                // an internet address
    public void AHostThatIsNotALanOrVpnAddress_IsNotNoted(string host)
    {
        MdnsPeerBrowser.IsPeerSighting(Record(host), Paired).Should().BeFalse();
    }

    [Fact]
    public void AStrangerOrAPlainOrWebOnlyRecord_IsNotNoted()
    {
        MdnsPeerBrowser.IsPeerSighting(Record("192.168.1.20", node: Guid.NewGuid()), Paired).Should().BeFalse("not a paired node");
        MdnsPeerBrowser.IsPeerSighting(Record("192.168.1.20", https: false), Paired).Should().BeFalse();
        MdnsPeerBrowser.IsPeerSighting(Record("192.168.1.20", surface: "join"), Paired).Should().BeFalse("its sync routes are not served there");
    }
}
