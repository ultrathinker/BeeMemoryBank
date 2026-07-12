using System.Net;
using BeeMemoryBank.Core.Services;
using Makaretu.Dns;

namespace BeeMemoryBank.Core.Tests.Mdns;

/// <summary>
/// REAL announce + browse round-trip over actual mDNS multicast sockets, in-process.
///
/// Advertises a profile built by <see cref="MdnsAnnouncer.BuildProfile"/> (the exact shape the
/// announcer produces) under a unique per-run service type, then browses for it with
/// <see cref="MdnsBrowser"/> and asserts the announced node is discovered with the right metadata.
///
/// See TASK_BRIEF "Definition of done" item 2: this is a genuine exercise of the multicast network
/// path. mDNS requires multicast-capable sockets (UDP 224.0.0.251:5353); some sandboxed/CI
/// environments restrict these. The test isolates itself with a unique service type so it never
/// collides with real nodes on the LAN.
/// </summary>
public class MdnsRoundTripTests
{
    [Fact]
    public async Task Announce_Then_Browse_FindsTheAnnouncedNode()
    {
        // Unique service type per run so we never collide with real BeeMemoryBank nodes (or parallel
        // test runs). A short, DNS-safe label derived from a GUID.
        var testService = "_bmbtest" + Guid.NewGuid().ToString("N")[..8] + "._tcp";
        var nodeId = Guid.NewGuid();
        const string name = "RoundTrip Test Node";
        const string version = "9.9.9-roundtrip";
        const int port = 5301;

        var profile = MdnsAnnouncer.BuildProfile(nodeId, name, version, https: true, port, serviceType: testService);

        using var advertiser = new ServiceDiscovery();
        advertiser.Advertise(profile);

        // Proactively broadcast so a browser that is already listening sees us without needing to
        // query first (and to exercise the passive path, not just query/answer).
        advertiser.Announce(profile);

        // Give the announcement a moment to propagate before browsing.
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        var browser = new MdnsBrowser();
        var discovered = await browser.DiscoverAsync(
            scanTime: TimeSpan.FromSeconds(8),
            serviceType: testService);

        var match = discovered.SingleOrDefault(n => n.NodeId == nodeId);
        match.Should().NotBeNull(
            "the announced node should be discoverable in-process via mDNS multicast loopback");

        match!.Name.Should().Be(name);
        match.Version.Should().Be(version);
        match.Https.Should().BeTrue();
        match.Port.Should().Be(port);
        match.Url.Should().StartWith("https://");

        // Host is either the resolved link-local IP or the .local target hostname — both are valid;
        // just assert it is non-empty.
        match.Host.Should().NotBeNullOrWhiteSpace();
    }
}
