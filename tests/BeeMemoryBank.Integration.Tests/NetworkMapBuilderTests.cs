using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-322: the network map the page draws, made of what this node knows. What matters is what is MEASURED (the calls this node makes and receives) and what
/// is only the network's rule (the calls between two other nodes): the two are never given as one thing.
/// </summary>
public class NetworkMapBuilderTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid SelfId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static NetworkMapBuilder.SelfInfo Self(string? address = "https://self.example:5610", bool blind = false) => new(
        SelfId, "This PC", blind,
        new WhitelistEntry { NodeId = SelfId, DisplayName = "This PC", ApiAddress = address, Ed25519PublicKey = new byte[32], CreatedAt = Now.AddDays(-60), CanGenerateEmbeddings = true },
        NodePlatform.Windows, InContainer: false, "2.6.4", 4, Notes: 482, Folders: 37, Changes: 1204);

    private static NetworkMapBuilder.Peer Peer(Guid id, string name, string? address, PeerContactLog.Contact? contact = null, int unsynced = 0)
    {
        var memory = contact ?? new PeerContactLog.Contact();
        var row = new WhitelistEntry
        {
            NodeId = id, DisplayName = name, ApiAddress = address, Ed25519PublicKey = new byte[32], CreatedAt = Now.AddDays(-30),
            LastProtocolVersion = 4, LastAppVersion = "2.6.4", IsSuperadmin = true,
        };
        return new NetworkMapBuilder.Peer(row, PeerContactView.Build(row, memory, null, null, 10, unsynced, Now), memory);
    }

    private static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static NetworkResponse Build(NetworkMapBuilder.SelfInfo self, params NetworkMapBuilder.Peer[] peers) => NetworkMapBuilder.Build(self, peers, isInvisible: false, Now);

    [Fact]
    public void ANodeIsAFullNodeWhenItHasAnAddress_ADeviceWhenItHasNone_AndBlindWhenItIsABlindCopy()
    {
        var blind = BlindNodeId.NewId();
        var map = Build(Self(),
            Peer(Id(2), "server", "https://server.example:443"),
            Peer(Id(3), "phone", null),
            Peer(blind, "copy", "https://45.9.9.9:5610"));

        map.Peers.Single(n => n.NodeId == Id(2)).Kind.Should().Be("full");
        map.Peers.Single(n => n.NodeId == Id(3)).Kind.Should().Be("device");
        map.Peers.Single(n => n.NodeId == blind).Kind.Should().Be("blind");
        map.Self.IsSelf.Should().BeTrue();
        map.Peers.Should().OnlyContain(n => !n.IsSelf);
    }

    [Fact]
    public void WhatANodeSaidItRunsOn_IsOnItsNode_AndANodeThatSaidNothingHasNone()
    {
        var map = Build(Self(),
            Peer(Id(2), "linux box", "https://a.example", new PeerContactLog.Contact(Platform: "linux", InContainer: true)),
            Peer(Id(3), "old node", "https://b.example"));

        var linux = map.Peers.Single(n => n.NodeId == Id(2));
        linux.Platform.Should().Be("linux");
        linux.InContainer.Should().BeTrue();
        var old = map.Peers.Single(n => n.NodeId == Id(3));
        old.Platform.Should().BeNull("a build before 2.6.4 says nothing");
        old.InContainer.Should().BeFalse();
        map.Self.Platform.Should().Be("windows");
    }

    [Fact]
    public void ThisNodeSaysWhatItHolds_AndWhereItCanBeCalled()
    {
        var map = Build(Self());

        map.Self.Notes.Should().Be(482);
        map.Self.Folders.Should().Be(37);
        map.Self.Changes.Should().Be(1204);
        map.Self.Scheme.Should().Be("https");
        map.Self.Host.Should().Be("self.example");
        map.Self.Port.Should().Be(5610);
        map.Self.Kind.Should().Be("full");
        map.Self.CanGenerateEmbeddings.Should().BeTrue();
        map.Links.Should().BeEmpty();
    }

    [Fact]
    public void ANodeWithNoAddressOfItsOwn_IsADevice_AndABlindRoleIsBlind()
    {
        Build(Self(address: null)).Self.Kind.Should().Be("device");
        Build(Self(blind: true)).Self.Kind.Should().Be("blind");
    }

    [Fact]
    public void ACallThisNodeMakes_IsMeasured_AndSaysWhereItGoes()
    {
        var contact = new PeerContactLog.Contact(OutboundOkAt: Now.AddSeconds(-20));

        var map = Build(Self(), Peer(Id(2), "server", "https://server.example:8443", contact));

        var link = map.Links.Single();
        (link.From, link.To).Should().Be((SelfId, Id(2)));
        link.Basis.Should().Be("observed");
        link.State.Should().Be("answering");
        link.Scheme.Should().Be("https", "the arrow points at the address of the node it calls");
        link.Host.Should().Be("server.example");
        link.Port.Should().Be(8443);
        link.LastOkAt.Should().Be(Now.AddSeconds(-20));
    }

    [Fact]
    public void ACallThatFails_SaysSinceWhenAndWhy()
    {
        var contact = new PeerContactLog.Contact(OutboundFailingSince: Now.AddMinutes(-25), OutboundFailure: "no answer: connection refused", OutboundFailureAt: Now.AddSeconds(-30));

        var map = Build(Self(), Peer(Id(2), "server", "https://server.example", contact));

        var link = map.Links.Single();
        link.State.Should().Be("not_answering");
        link.FailingSince.Should().Be(Now.AddMinutes(-25));
        link.Reason.Should().Be("no answer: connection refused");
    }

    [Fact]
    public void ACallNothingHasBeenSaidAboutYet_IsUnknown_NotAnInventedAnswer()
    {
        Build(Self(), Peer(Id(2), "server", "https://server.example")).Links.Single().State.Should().Be("unknown");
    }

    [Fact]
    public void ADeviceThatCallsIn_IsAnArrowToThisNode_FromWhereItCameFrom_AndNoCallIsMadeToIt()
    {
        var contact = new PeerContactLog.Contact(InboundAt: Now.AddMinutes(-3), InboundFrom: "203.0.113.7");

        var map = Build(Self(), Peer(Id(3), "phone", null, contact));

        var link = map.Links.Single();
        (link.From, link.To).Should().Be((Id(3), SelfId), "this node cannot call a node that has no address");
        link.Basis.Should().Be("observed");
        link.State.Should().Be("heard");
        link.SeenFrom.Should().Be("203.0.113.7");
        link.Host.Should().Be("self.example", "it calls this node at this node's address");
        link.Port.Should().Be(5610);
        map.Peers.Single().SeenFrom.Should().Be("203.0.113.7");
    }

    [Fact]
    public void ACallerNotHeardForLongerThanItsCadence_IsStale_NotDropped()
    {
        // a phone calls about every five minutes: 12.5 minutes is the longest it may be quiet and still be online
        var contact = new PeerContactLog.Contact(InboundAt: Now.AddMinutes(-30));

        var map = Build(Self(), Peer(Id(3), "phone", null, contact));

        map.Links.Single().State.Should().Be("stale");
    }

    [Fact]
    public void ANodeThatIsCalledAndCallsBack_IsATwoWayConnection()
    {
        var contact = new PeerContactLog.Contact(OutboundOkAt: Now.AddSeconds(-10), InboundAt: Now.AddSeconds(-40));

        var map = Build(Self(), Peer(Id(2), "server", "https://server.example", contact));

        map.Links.Should().HaveCount(2);
        map.Links.Should().Contain(l => l.From == SelfId && l.To == Id(2) && l.Basis == "observed");
        map.Links.Should().Contain(l => l.From == Id(2) && l.To == SelfId && l.Basis == "observed");
    }

    [Fact]
    public void WhatCannotBeMeasured_IsTheNetworksRule_NeverGivenAsSeen()
    {
        var blind = BlindNodeId.NewId();
        var map = Build(Self(),
            Peer(Id(2), "server one", "https://one.example"),
            Peer(Id(3), "server two", "https://two.example"),
            Peer(Id(4), "phone", null),
            Peer(blind, "copy", "https://copy.example"));

        var expected = map.Links.Where(l => l.Basis == "expected").ToList();
        expected.Should().OnlyContain(l => l.From != SelfId && l.To != SelfId, "this node measures its own calls");
        expected.Should().OnlyContain(l => l.State == "expected");
        expected.Select(l => (l.From, l.To)).Should().BeEquivalentTo(new[]
        {
            (Id(2), Id(3)), (Id(3), Id(2)),             // every node calls every node that has an address
            (Id(2), blind), (Id(3), blind),             // and a blind copy is called too
            (Id(4), Id(2)), (Id(4), Id(3)), (Id(4), blind),   // a phone calls out
        });
        expected.Select(l => l.From).Should().NotContain(blind, "a blind node calls only where nobody can call it");
        expected.Select(l => l.To).Should().NotContain(Id(4), "a phone has no address to be called at");
    }

    [Fact]
    public void AnAddressIsGivenAsSchemeHostAndPortOnly()
    {
        AssertAddress("https://user:secret@host.example:8443/path/to?token=abc#frag", "https", "host.example", 8443);
        AssertAddress("http://10.0.0.8", "http", "10.0.0.8", 80);
        AssertAddress("https://host.example", "https", "host.example", 443);
    }

    private static void AssertAddress(string? address, string? scheme, string? host, int? port)
    {
        var (s, h, p) = NetworkMapBuilder.Address(address);
        s.Should().Be(scheme);
        h.Should().Be(host);
        p.Should().Be(port);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://host.example/x")]
    [InlineData("file:///etc/passwd")]
    public void ASpoiledOrForeignAddress_GivesNothing_InsteadOfAGuess(string? address)
    {
        AssertAddress(address, null, null, null);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]   // what a dual-stack socket says of an IPv4 caller
    [InlineData("2001:db8::1", "2001:db8::1")]
    public void ACallersAddress_IsGivenAsItsOwnFamilyWouldWriteIt(string reported, string expected)
    {
        NetworkMapBuilder.CallerAddress(System.Net.IPAddress.Parse(reported)).Should().Be(expected);
        NetworkMapBuilder.CallerAddress(null).Should().BeNull("a connection with no address says nothing");
    }

    [Fact]
    public void NothingSecretOfAnAddress_ReachesTheWire()
    {
        var map = Build(Self(), Peer(Id(2), "server", "https://user:secret@server.example:443/private/path?token=abc"));

        var json = JsonSerializer.Serialize(map, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.Should().NotContain("secret").And.NotContain("token").And.NotContain("/private/path");
        json.Should().Contain("server.example");
    }

    [Fact]
    public void WhatANodeHoldsIsToldFromWhatIsDelivered()
    {
        var map = Build(Self(), Peer(Id(2), "behind", "https://behind.example", unsynced: 7), Peer(Id(3), "level", "https://level.example"));

        var behind = map.Peers.Single(n => n.NodeId == Id(2));
        behind.IsSynced.Should().BeFalse();
        behind.UnsyncedCount.Should().Be(7);
        map.Peers.Single(n => n.NodeId == Id(3)).IsSynced.Should().BeTrue();
    }
}
