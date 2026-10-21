using BeeMemoryBank.Sync;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Release 2.7: what the Blind nodes page and the network map say about a server blind node whose owner's limit stopped this node's push: "paused by the blind
/// node's owner", in the hub's own words, and never as a failure. It is contact (the node answered), so the failure streak ends; a good session ends the pause.
/// </summary>
public class PeerContactLogNodePauseTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Peer = Guid.Parse("00000000-0000-4000-8000-0000000000b1");

    [Theory]
    [InlineData("disk", "Paused by the blind node's owner: storage limit")]
    [InlineData("traffic", "Paused by the blind node's owner: daily traffic limit")]
    [InlineData("other", "Paused by the blind node's owner: a limit set on the node")]
    [InlineData("whatever the peer wrote", "Paused by the blind node's owner: a limit set on the node")]
    public void TheSentenceIsTheHubs_AndSaysTheBlindNodesOwner(string reason, string expected)
    {
        var log = new PeerContactLog();
        log.NotePausedByNode(Peer, Now, reason, null);

        var contact = log.Get(Peer);

        contact.PausedByNode.Should().BeTrue();
        PeerPauseReport.Sentence(contact.PausedReason!, contact.PausedUntil, contact.PausedUntilOffsetMinutes, Now, contact.PausedByNode).Should().Be(expected);
    }

    [Fact]
    public void ABusyNode_IsSaidToBeBusy_NotToBePausedByItsOwner()
    {
        var log = new PeerContactLog();
        log.NotePausedByNode(Peer, Now, "busy", null);

        var contact = log.Get(Peer);

        PeerPauseReport.Sentence(contact.PausedReason!, contact.PausedUntil, contact.PausedUntilOffsetMinutes, Now, contact.PausedByNode)
            .Should().Be("Busy with other transfers right now; asked again in about a minute");
    }

    [Fact]
    public void ATrafficPauseTellsWhenItEnds_InUtc_BecauseTheHubDoesNotKnowTheNodesZone()
    {
        var log = new PeerContactLog();
        log.NotePausedByNode(Peer, Now, "traffic", Now.AddHours(5));

        var contact = log.Get(Peer);

        PeerPauseReport.Sentence(contact.PausedReason!, contact.PausedUntil, contact.PausedUntilOffsetMinutes, Now, byNode: true)
            .Should().Be("Paused by the blind node's owner: daily traffic limit (until 17:00 UTC)");
    }

    [Fact]
    public void TheDevicesOwnWordsAreUnchanged()
    {
        PeerPauseReport.Sentence("traffic", Now.AddHours(1), 180, Now).Should().Be("Paused by the device's owner: daily traffic limit (until 16:00 device time)");
    }

    [Fact]
    public void ThePausedNodeAnswered_SoItIsContactAndNotAFailure()
    {
        var log = new PeerContactLog();
        log.NoteOutboundFailure(Peer, Now.AddMinutes(-3), "no answer", "http://blind.local");

        log.NotePausedByNode(Peer, Now, "disk", null, "http://blind.local");

        var contact = log.Get(Peer, "http://blind.local");
        contact.OutboundOkAt.Should().Be(Now);
        contact.OutboundFailingSince.Should().BeNull("a node that says 'paused' is not failing");
        contact.OutboundFailure.Should().BeNull();
    }

    [Fact]
    public void AGoodSessionEndsThePause()
    {
        var log = new PeerContactLog();
        log.NotePausedByNode(Peer, Now, "disk", null, "http://blind.local");

        log.NoteOutboundOk(Peer, Now.AddHours(1), "http://blind.local");

        var contact = log.Get(Peer, "http://blind.local");
        contact.PausedReason.Should().BeNull();
        contact.PausedByNode.Should().BeFalse();
        contact.PausedReportedAt.Should().BeNull();
    }

    [Fact]
    public void AnotherAddressEndsThePause_ItWasTheOldAddressesAnswer()
    {
        var log = new PeerContactLog();
        log.NotePausedByNode(Peer, Now, "disk", null, "http://old.local");

        log.Get(Peer, "http://new.local").PausedReason.Should().BeNull();
    }

    [Fact]
    public void ACopysOwnReport_IsNotEndedByAGoodSession_AndIsNotTheNodesWords()
    {
        var log = new PeerContactLog();
        log.NotePauseReport(Peer, Now, "traffic", null);

        log.NoteOutboundOk(Peer, Now.AddMinutes(5), "http://phone.local");

        var contact = log.Get(Peer);
        contact.PausedReason.Should().Be("traffic", "what a copy reports about itself is ended by its own next report");
        contact.PausedByNode.Should().BeFalse();
    }

    [Fact]
    public void ACopysReportTakesOverFromTheNodesPause_NotTheOtherWayAround()
    {
        var log = new PeerContactLog();
        log.NotePausedByNode(Peer, Now, "disk", null);

        log.NotePauseReport(Peer, Now.AddMinutes(1), "traffic", null);

        log.Get(Peer).PausedByNode.Should().BeFalse();
    }

    [Fact]
    public void ThePauseIsKeptBoundedLikeTheRestOfTheLog()
    {
        var log = new PeerContactLog();
        var active = new HashSet<Guid> { Peer };
        log.NotePausedByNode(Peer, Now, "disk", null);
        for (var i = 0; i < 100; i++) log.NotePausedByNode(Guid.NewGuid(), Now, "traffic", null);

        log.Reconcile(active, Now);

        log.Count.Should().Be(1);
    }
}
