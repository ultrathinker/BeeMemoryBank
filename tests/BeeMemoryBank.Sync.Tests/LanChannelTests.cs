using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Channels;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The order in which a session tries a peer's addresses (docs/design/lan-peer-sync.md section 7): the one that worked last, the LAN ones of a
/// network this computer is on (in parallel), the internet ones, an explicit LAN address of another network last; an announced or mDNS address of a
/// network this computer is not on is not dialled; an announced or mDNS address that showed a wrong peer is left alone for an hour; an address
/// nobody gave by hand is a candidate only where it can be held to the peer's own TLS keys.
/// </summary>
public class ChannelPlannerTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 18, 0, 0, DateTimeKind.Utc);
    private static readonly LocalSubnet HomeWifi = new(IPAddress.Parse("192.168.1.42"), 24, false);
    private static readonly string DesktopPin = System.Buffers.Text.Base64Url.EncodeToString(new byte[32]);
    private static readonly WhitelistEntry Desktop = new()
    {
        NodeId = Guid.NewGuid(), DisplayName = "Desktop", ApiAddress = "https://192.168.1.220:5311", TlsTrust = BlindTrust.Pin, TlsSpki = DesktopPin,
    };

    /// <summary>What a node announces about itself: its endpoints and the leaf key it serves.</summary>
    private static PeerEndpointRecord Announced(Guid node, params (string Url, string Scope)[] endpoints) =>
        new(node, endpoints.Select(e => new NodeEndpointEntry(e.Url, e.Scope)).ToList(), DesktopPin, null, 5311, 10, Now);

    private static PeerChannelRow Row(Guid peer, string address, string source, DateTime? ok = null, DateTime? fail = null, string? kind = null) =>
        new(peer, address, source, ChannelScopes.Of(address), Now.AddDays(-1), null, ok, fail, kind, kind is null ? 0 : 1);

    [Fact]
    public void AtHome_TheLanAddressesAreDialledInParallel_AndTheInternetOnesAfter()
    {
        var row = new WhitelistEntry { NodeId = Desktop.NodeId, ApiAddress = "https://desktop.example.org" };
        var plan = ChannelPlanner.Plan(row, Announced(row.NodeId, ("https://192.168.1.220:5311", "lan"), ("https://192.168.1.228:5311", "lan")),
            [], [], [HomeWifi], Now);

        plan.Parallel.Select(c => c.Url).Should().BeEquivalentTo(["https://192.168.1.220:5311", "https://192.168.1.228:5311"]);
        plan.Sequential.Select(c => c.Url).Should().Equal("https://desktop.example.org");
        plan.First.Should().BeEmpty();
        plan.AnyLanOnCurrentNetwork.Should().BeTrue();
        plan.OnlyLan.Should().BeFalse();
    }

    [Fact]
    public void TheAddressThatWorkedLast_IsTriedFirst_WhileItsNetworkIsHere()
    {
        var rows = new[] { Row(Desktop.NodeId, "https://192.168.1.228:5311", ChannelSources.Mdns, ok: Now.AddMinutes(-3)) };
        var plan = ChannelPlanner.Plan(Desktop, null, [new DiscoveredEndpoint(Desktop.NodeId, "https://192.168.1.228:5311", Now.AddMinutes(-1))], rows,
            [HomeWifi], Now);

        plan.First.Should().ContainSingle().Which.Url.Should().Be("https://192.168.1.228:5311");
        plan.Parallel.Select(c => c.Url).Should().Equal("https://192.168.1.220:5311");

        // In a café the home address that worked last is not tried first, and the mDNS one of home is not tried at all.
        var cafe = ChannelPlanner.Plan(Desktop, null, [], rows, [new LocalSubnet(IPAddress.Parse("10.20.0.7"), 22, false)], Now);
        cafe.First.Should().BeEmpty();
        cafe.Parallel.Should().BeEmpty();
    }

    [Fact]
    public void AwayFromHome_AnAnnouncedLanAddressIsNotDialled_ThePrimaryOneLastAndShort()
    {
        var plan = ChannelPlanner.Plan(Desktop, Announced(Desktop.NodeId, ("https://192.168.1.228:5311", "lan")), [], [],
            [new LocalSubnet(IPAddress.Parse("10.20.0.7"), 22, false)], Now);

        plan.Skipped.Select(c => c.Url).Should().Equal("https://192.168.1.228:5311");
        plan.Sequential.Should().ContainSingle().Which.Url.Should().Be("https://192.168.1.220:5311", "a person gave the primary address: it is still tried, last");
        plan.Parallel.Should().BeEmpty();
        plan.OnlyLan.Should().BeTrue();
        plan.AnyLanOnCurrentNetwork.Should().BeFalse("that is the 'away' state: no alarm for a laptop on a trip");
    }

    [Fact]
    public void ARowWithoutAnAddress_IsCallable_AsSoonAsItHasAChannel()
    {
        var mac = new WhitelistEntry { NodeId = Guid.NewGuid(), DisplayName = "Mac", ApiAddress = null };
        ChannelPlanner.Plan(mac, null, [], [], [HomeWifi], Now).HasDialable.Should().BeFalse("it only calls this node");

        var plan = ChannelPlanner.Plan(mac, Announced(mac.NodeId, ("https://192.168.1.42:5311", "lan")), [], [], [HomeWifi], Now);

        plan.HasDialable.Should().BeTrue();
        plan.Dialable.Single().Source.Should().Be(ChannelSources.Announced);
    }

    [Fact]
    public void TheSameAddressFromSeveralSources_IsOneCandidate_UnderTheMostExplicitSource()
    {
        var rows = new[] { Row(Desktop.NodeId, "https://192.168.1.220:5311", ChannelSources.Manual) };
        var plan = ChannelPlanner.Plan(Desktop, Announced(Desktop.NodeId, ("https://192.168.1.220:5311", "lan")),
            [new DiscoveredEndpoint(Desktop.NodeId, "https://192.168.1.220:5311", Now)], rows, [HomeWifi], Now);

        plan.All.Should().ContainSingle().Which.Source.Should().Be(ChannelSources.Manual);
    }

    [Fact]
    public void AnAnnouncedAddressThatShowedAnotherPeer_IsLeftAloneForAnHour_ButNotWhenAPersonChecks()
    {
        var rows = new[] { Row(Desktop.NodeId, "https://192.168.1.228:5311", ChannelSources.Announced, fail: Now.AddMinutes(-10), kind: ChannelFailures.WrongPeer) };
        var announced = Announced(Desktop.NodeId, ("https://192.168.1.228:5311", "lan"));

        ChannelPlanner.Plan(Desktop, announced, [], rows, [HomeWifi], Now).Skipped.Select(c => c.Url).Should().Equal("https://192.168.1.228:5311");
        ChannelPlanner.Plan(Desktop, announced, [], rows, [HomeWifi], Now.AddHours(1)).Parallel.Select(c => c.Url).Should().Contain("https://192.168.1.228:5311");
        ChannelPlanner.Plan(Desktop, announced, [], rows, [HomeWifi], Now, ignorePause: true).Dialable.Select(c => c.Url).Should().Contain("https://192.168.1.228:5311");
    }

    [Fact]
    public void APrimaryAddressThatShowedAnotherPeer_IsStillDialled_Last()
    {
        // One address, a passing TLS failure (a captive portal, a certificate being replaced): the peer is not left undialled for an hour.
        var rows = new[] { Row(Desktop.NodeId, "https://192.168.1.220:5311", ChannelSources.Primary, fail: Now.AddMinutes(-10), kind: ChannelFailures.WrongPeer) };

        var alone = ChannelPlanner.Plan(Desktop, null, [], rows, [HomeWifi], Now);
        alone.Dialable.Should().ContainSingle().Which.Url.Should().Be("https://192.168.1.220:5311");

        var withAnother = ChannelPlanner.Plan(Desktop, Announced(Desktop.NodeId, ("https://192.168.1.228:5311", "lan")), [], rows, [HomeWifi], Now);
        withAnother.Dialable.Select(c => c.Url).Should().Equal(["https://192.168.1.228:5311", "https://192.168.1.220:5311"], "the one that failed goes last");
    }

    [Fact]
    public void AnAutomaticAddress_OfAPeerWithNoKeyToHoldItTo_IsNoCandidate()
    {
        // A hub behind a public CA, not pinned, that announced no keys: an mDNS record or an announcement naming an address of it could only be checked
        // by the public CAs there, which anyone's certificate passes. Only its own address is dialled.
        var hub = new WhitelistEntry { NodeId = Guid.NewGuid(), ApiAddress = "https://hub.example.org" };
        var keyless = new PeerEndpointRecord(hub.NodeId, [new NodeEndpointEntry("https://192.168.1.30:5311", "lan")], null, null, 5311, 10, Now);

        var plan = ChannelPlanner.Plan(hub, keyless, [new DiscoveredEndpoint(hub.NodeId, "https://192.168.1.31:5311", Now)], [], [HomeWifi], Now);

        plan.All.Select(c => c.Url).Should().Equal("https://hub.example.org");
    }

    [Fact]
    public void AnAnnouncedOrMdnsNameOnTheInternet_IsNeverACandidate()
    {
        var plan = ChannelPlanner.Plan(Desktop, Announced(Desktop.NodeId, ("https://desktop.example.org", "internet")),
            [new DiscoveredEndpoint(Desktop.NodeId, "https://attacker.example.com:5311", Now)], [], [HomeWifi], Now);

        plan.All.Select(c => c.Url).Should().Equal("https://192.168.1.220:5311");
    }

    [Fact]
    public void WithoutAutomatic_KeepsOnlyTheAddressesAPersonGave()
    {
        var rows = new[] { Row(Desktop.NodeId, "https://10.0.0.5:5311", ChannelSources.Manual) };
        var plan = ChannelPlanner.Plan(Desktop, Announced(Desktop.NodeId, ("https://192.168.1.228:5311", "lan")),
            [new DiscoveredEndpoint(Desktop.NodeId, "https://192.168.1.229:5311", Now)], rows, [HomeWifi], Now);

        var given = plan.WithoutAutomatic();

        given.Dialable.Select(c => c.Source).Should().OnlyContain(s => s == ChannelSources.Primary || s == ChannelSources.Manual);
        given.Dialable.Should().HaveCount(2);
        given.All.Should().HaveCount(plan.All.Count, "the rest is still known, not dialled");
    }

    [Fact]
    public void AnOldMdnsSighting_IsForgotten_AndAnotherPeersIsNotThisPeers()
    {
        var other = Guid.NewGuid();
        var plan = ChannelPlanner.Plan(Desktop, null,
            [new DiscoveredEndpoint(Desktop.NodeId, "https://192.168.1.99:5311", Now - IPeerDiscovery.Freshness - TimeSpan.FromMinutes(1)),
             new DiscoveredEndpoint(other, "https://192.168.1.98:5311", Now)], [], [HomeWifi], Now);

        plan.All.Select(c => c.Url).Should().Equal("https://192.168.1.220:5311");
    }

    [Fact]
    public void AVpnAddress_IsDialledOnlyWhileAVpnIsUp()
    {
        var plan = ChannelPlanner.Plan(Desktop, Announced(Desktop.NodeId, ("https://100.64.0.1:5311", "vpn")), [], [], [HomeWifi], Now);
        plan.Skipped.Select(c => c.Url).Should().Equal("https://100.64.0.1:5311");

        var withTailnet = ChannelPlanner.Plan(Desktop, Announced(Desktop.NodeId, ("https://100.64.0.1:5311", "vpn")), [], [],
            [HomeWifi, new LocalSubnet(IPAddress.Parse("100.100.1.2"), 32, true)], Now);
        withTailnet.Parallel.Select(c => c.Url).Should().Contain("https://100.64.0.1:5311");
    }
}

/// <summary>The sightings of LAN discovery: bounded, and a full cache forgets the nodes it has not seen for a while before it refuses a new one.</summary>
public class PeerDiscoveryCacheTests
{
    [Fact]
    public void AFullCache_ForgetsStaleNodes_BeforeItRefusesANewOne()
    {
        var cache = new PeerDiscoveryCache();
        var then = DateTime.UtcNow - IPeerDiscovery.Freshness - TimeSpan.FromMinutes(5);
        for (var i = 0; i < 256; i++) cache.Note(Guid.NewGuid(), $"https://192.168.1.{i % 250 + 1}:5311", then);

        var peer = Guid.NewGuid();
        cache.Note(peer, "https://192.168.1.20:5311", DateTime.UtcNow);
        cache.Recent(peer).Should().ContainSingle("the stale nodes made room");

        for (var i = 0; i < 255; i++) cache.Note(Guid.NewGuid(), "https://192.168.1.30:5311", DateTime.UtcNow);
        var late = Guid.NewGuid();
        cache.Note(late, "https://192.168.1.40:5311", DateTime.UtcNow);
        cache.Recent(late).Should().BeEmpty("full of fresh sightings: a new node is not noted");
        cache.Recent(peer).Should().ContainSingle("a known node keeps its place");
    }
}

/// <summary>
/// The TLS anchor of a peer (decision D4): its pinned leaf keys, its local CA, and - only for a peer that is not pinned - an ordinary certificate.
/// An address is held to the anchor of the peer it is dialled FOR: peer A's address never passes with peer B's anchor.
/// </summary>
public class PeerTlsAnchorTests
{
    private static (X509Certificate2 Ca, ECDsa Key) NewCa(string name)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return (req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddYears(10)), key);
    }

    private static X509Certificate2 Leaf(X509Certificate2 ca, DateTimeOffset? notAfter = null, ECDsa? key = null)
    {
        key ??= ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=node", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var serial = RandomNumberGenerator.GetBytes(10);
        serial[0] &= 0x7F;
        return req.Create(ca, DateTimeOffset.UtcNow.AddDays(-2), notAfter ?? DateTimeOffset.UtcNow.AddDays(90), serial);
    }

    private static string CaKey(X509Certificate2 ca) => System.Buffers.Text.Base64Url.EncodeToString(ca.PublicKey.ExportSubjectPublicKeyInfo());

    [Fact]
    public void ALeafOfThePinnedCa_IsAccepted_AnotherCasLeafIsNot_NorAnExpiredOne()
    {
        var (caA, _) = NewCa("A");
        var (caB, _) = NewCa("B");
        var anchor = new PeerTlsAnchor([], caA.PublicKey.ExportSubjectPublicKeyInfo(), AllowPublicCa: false);

        anchor.Accepts(Leaf(caA), DateTime.UtcNow).Should().BeTrue("a re-issued leaf with a new key passes without re-pinning (ca-pin)");
        anchor.Accepts(Leaf(caB), DateTime.UtcNow).Should().BeFalse();
        anchor.Accepts(Leaf(caA, notAfter: DateTimeOffset.UtcNow.AddDays(-1)), DateTime.UtcNow).Should().BeFalse("an old leaf is not accepted forever");
        anchor.Accepts(null, DateTime.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void ThePeersAnchor_IsItsRowsPin_PlusWhatItAnnouncedAboutItself()
    {
        var (ca, _) = NewCa("A");
        var leaf = Leaf(ca);
        var announcedLeaf = Spki.Of(leaf);
        var row = new WhitelistEntry { NodeId = Guid.NewGuid(), TlsSpki = new string('A', 43), TlsTrust = BlindTrust.Pin };
        var announced = new PeerEndpointRecord(row.NodeId, [], announcedLeaf, CaKey(ca), 5311, 3, DateTime.UtcNow);

        var anchor = PeerTlsAnchor.For(row, announced)!;

        anchor.LeafPins.Should().Equal(row.TlsSpki, announcedLeaf);
        anchor.AllowPublicCa.Should().BeFalse("a pinned row never falls back to the public CAs");
        anchor.CaKey.Should().Equal(ca.PublicKey.ExportSubjectPublicKeyInfo());
        PeerTlsAnchor.For(new WhitelistEntry { NodeId = Guid.NewGuid() }, null).Should().BeNull("nothing to pin: the ordinary check, as before");
    }

    [Fact]
    public void AnUnpinnedPeersPublicCa_PassesOnlyAtItsOwnAddress()
    {
        // A hub behind a public CA that announced its leaf key: at its own host an ordinary certificate passes, as before; at any other address (an
        // announced one, an mDNS record anyone can send) only its own key does.
        var (ca, _) = NewCa("A");
        var row = new WhitelistEntry { NodeId = Guid.NewGuid(), ApiAddress = "https://hub.example.org" };
        var announced = new PeerEndpointRecord(row.NodeId, [], Spki.Of(Leaf(ca)), null, 5311, 3, DateTime.UtcNow);

        var anchor = PeerTlsAnchor.For(row, announced)!;

        anchor.AtHost(new Uri("https://HUB.example.org/api/sync/identity")).AllowPublicCa.Should().BeTrue();
        anchor.AtHost(new Uri("https://192.168.1.30:5311/")).AllowPublicCa.Should().BeFalse();
        anchor.AtHost(new Uri("https://attacker.example.com/")).AllowPublicCa.Should().BeFalse();
        anchor.AtHost(new Uri("https://192.168.1.30:5311/")).LaneKey.Should().NotBe(anchor.LaneKey, "the narrowed anchor has a lane of its own");

        var registry = new SpkiPinRegistry(new NoScopes());
        using var elsewhere = new HttpRequestMessage(HttpMethod.Get, "https://attacker.example.com/api/sync/identity");
        elsewhere.Options.Set(SpkiPinRegistry.ExplicitAnchor, anchor);
        registry.IsPinned(elsewhere).Should().BeTrue("held to the peer's key there");
        using var home = new HttpRequestMessage(HttpMethod.Get, "https://hub.example.org/api/sync/identity");
        home.Options.Set(SpkiPinRegistry.ExplicitAnchor, anchor);
        registry.IsPinned(home).Should().BeFalse("its own address keeps the ordinary check");

        PeerTlsAnchor.For(new WhitelistEntry { NodeId = Guid.NewGuid() }, announced)!.AllowPublicCa.Should().BeFalse(
            "a row with no address of its own has no host where a public CA could stand for it");
    }

    [Fact]
    public void PeerAsAddress_IsNeverAcceptedWithPeerBsAnchor()
    {
        var (caA, _) = NewCa("A");
        var (caB, _) = NewCa("B");
        var certOfA = Leaf(caA);
        var anchorOfB = new PeerTlsAnchor([Spki.Of(Leaf(caB))], caB.PublicKey.ExportSubjectPublicKeyInfo(), AllowPublicCa: false);
        var registry = new SpkiPinRegistry(new NoScopes());

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://192.168.1.50:5311/api/sync/identity");
        request.Options.Set(SpkiPinRegistry.ExplicitAnchor, anchorOfB);

        registry.Validate(request, certOfA, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse(
            "an mDNS or typed address of peer A is checked against B's anchor when it is dialled for B, and A's key is not B's");
        registry.IsPinned(request).Should().BeTrue("a pinned anchor needs HTTPS");
        var anchorOfA = new PeerTlsAnchor([Spki.Of(certOfA)], null, false);
        request.Options.Set(SpkiPinRegistry.ExplicitAnchor, anchorOfA);
        registry.Validate(request, certOfA, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue();
    }

    private sealed class NoScopes : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() => throw new InvalidOperationException("no whitelist is read when the request carries its anchor");
    }
}

/// <summary>The reachability states (docs/design/lan-peer-sync.md section 10): no false "not answering" for a laptop that is simply elsewhere.</summary>
public class ReachabilityStateTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 18, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(2.5);
    private static readonly PeerReachability.PlanSummary Home = new(true, true, OnlyLan: true, AnyLanOnCurrentNetwork: true, Now);
    private static readonly PeerReachability.PlanSummary Elsewhere = new(true, true, OnlyLan: true, AnyLanOnCurrentNetwork: false, Now);

    private static string State(DateTime? direct = null, string? scope = null, DateTime? inbound = null, DateTime? failing = null,
        PeerReachability.RelaySighting? relay = null, PeerReachability.PlanSummary? plan = null, bool? lanSync = true, DateTime? older = null) =>
        PeerReachability.Compute(new PeerReachability.Inputs(Window, direct, scope, inbound, failing, relay, null, plan, lanSync, older), Now);

    [Fact]
    public void ADirectSession_IsDirectLan_OrDirectInternet_ByTheScopeOfItsChannel()
    {
        State(direct: Now.AddMinutes(-1), scope: ChannelScopes.Lan, plan: Home).Should().Be(ReachabilityStates.DirectLan);
        State(direct: Now.AddMinutes(-1), scope: ChannelScopes.Internet, plan: Home).Should().Be(ReachabilityStates.DirectInternet);
    }

    [Fact]
    public void APeerThatCallsIn_IsCallsUs()
    {
        State(inbound: Now.AddSeconds(-30)).Should().Be(ReachabilityStates.CallsUs);
    }

    [Fact]
    public void ALaptopOnATrip_IsAway_OrReachableThroughARelay_NeverNotAnswering()
    {
        State(failing: Now.AddHours(-2), plan: Elsewhere, older: Now.AddHours(-3)).Should().Be(ReachabilityStates.Away);
        State(failing: Now.AddHours(-2), plan: Elsewhere, relay: new(Guid.NewGuid(), "Home-Server", Now.AddMinutes(-5)))
            .Should().Be(ReachabilityStates.ViaRelay);
    }

    [Fact]
    public void AnOlderDesktopOnThisNetwork_NeedsAnUpdate_ItIsNotBroken()
    {
        State(failing: Now.AddMinutes(-30), plan: Home, lanSync: false, relay: new(Guid.NewGuid(), "Home-Server", Now.AddMinutes(-1)))
            .Should().Be(ReachabilityStates.OldVersion);
        State(failing: Now.AddMinutes(-30), plan: Home, lanSync: null).Should().Be(ReachabilityStates.NotAnswering, "a peer never seen cannot be called old");
    }

    [Fact]
    public void AFailureOnThisNetwork_WithNoOtherEvidence_IsNotAnswering()
    {
        State(failing: Now.AddMinutes(-30), plan: Home, older: Now.AddHours(-1)).Should().Be(ReachabilityStates.NotAnswering);
    }

    [Fact]
    public void NothingForThreeDays_ByAnyRoute_IsSilent()
    {
        State(failing: Now.AddDays(-4), plan: Elsewhere, older: Now.AddDays(-4)).Should().Be(ReachabilityStates.Silent);
        State(failing: Now.AddDays(-4), plan: Elsewhere, older: Now.AddDays(-4), relay: new(Guid.NewGuid(), "hub", Now.AddMinutes(-2)))
            .Should().Be(ReachabilityStates.ViaRelay, "another peer saw it two minutes ago");
    }

    [Fact]
    public void AStanding_RecordsTheNewestSightingOfEachPeer_ButNotOfItselfOrOfThisNode()
    {
        var reachability = new PeerReachability();
        var self = Guid.NewGuid();
        var hub = Guid.NewGuid();
        var laptop = Guid.NewGuid();
        var standing = new MyStanding(hub, 4, true, false,
        [
            new PeerProtocolSeen(laptop, "Laptop", 4, Now.AddMinutes(-3), Now.AddDays(-10), CapabilitiesSeenAt: Now.AddMinutes(-2)),
            new PeerProtocolSeen(self, "Me", 4, Now, Now.AddDays(-10)),
        ]);

        reachability.NoteStanding(hub, "Home-Server", standing, self, Now);

        reachability.RelayOf(laptop)!.SeenAt.Should().Be(Now.AddMinutes(-2));
        reachability.RelayOf(laptop)!.ViaName.Should().Be("Home-Server");
        reachability.RelayOf(self).Should().BeNull();
        reachability.ShouldAskStanding(hub, Now).Should().BeTrue();
        reachability.ShouldAskStanding(hub, Now.AddMinutes(1)).Should().BeFalse("asked every few minutes, not every cycle");
    }
}
