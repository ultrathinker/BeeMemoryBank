using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-313 fix round, F2/F3/F4/F9: the one projection every status of the mesh reads (the header, Trusted Nodes, the Blind nodes page, the host status).
/// It is pure, so the rules are pinned here without a node: how long "online" lasts for the interval the loop really runs at, what a clock that is ahead
/// is worth, what "we do not know yet" is after a restart, and that a blind node's own my-standing call is never mistaken for it calling in.
/// </summary>
public sealed class PeerContactViewTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static WhitelistEntry Node(string? address, Guid? id = null, DateTime? protocolSeenAt = null) => new()
    {
        NodeId = id ?? Guid.NewGuid(), DisplayName = "peer", Ed25519PublicKey = new byte[32], ApiAddress = address, Status = "A",
        CreatedAt = Now.AddDays(-30), UpdatedAt = Now.AddDays(-30), LastProtocolSeenAt = protocolSeenAt,
    };

    private static DeliveryNodeStatus Build(WhitelistEntry node, PeerContactLog.Contact? memory = null, DateTime? pulledAt = null, TimeSpan? interval = null) =>
        PeerContactView.Build(node, memory ?? new PeerContactLog.Contact(), pulledAt, push: null, headSeq: 0, unsyncedCount: 0, Now, interval);

    // ---- the window of "online" ----

    [Theory]
    [InlineData(null, false, 150)]       // the default 60 s: two missed cycles and half of a third
    [InlineData(600, false, 1500)]       // a loop that runs every ten minutes is not "offline" after three
    [InlineData(5, false, 120)]          // a very short interval does not make a peer flicker
    [InlineData(null, true, 750)]        // a phone calls every five minutes whatever this node's interval is
    [InlineData(600, true, 1500)]        // a slower loop of this node does not shorten it
    public void The_window_follows_the_interval_the_loop_runs_at_and_the_cadence_of_a_node_that_calls_in(int? intervalSeconds, bool callsUs, int expectedSeconds)
    {
        var window = PeerContactView.OnlineWindowFor(intervalSeconds is { } s ? TimeSpan.FromSeconds(s) : null, callsUs);

        window.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void A_node_that_is_called_is_online_for_as_long_as_the_effective_interval_says_not_for_a_fixed_five_minutes()
    {
        var node = Node("http://peer");
        var heardTenMinutesAgo = new PeerContactLog.Contact(OutboundOkAt: Now.AddMinutes(-10), OutboundEndpoint: "http://peer");

        Build(node, heardTenMinutesAgo).Online.Should().BeFalse("with the default 60 s loop ten quiet minutes are four missed cycles");
        Build(node, heardTenMinutesAgo, interval: TimeSpan.FromMinutes(10)).Online.Should().BeTrue("a loop that runs every ten minutes has not missed one yet");
    }

    [Fact]
    public void A_node_that_calls_in_is_online_for_a_phones_cadence_and_not_beyond_it()
    {
        var node = Node(address: null);

        Build(node, new PeerContactLog.Contact(InboundAt: Now.AddMinutes(-11))).Online.Should().BeTrue("11 minutes is inside 2.5 phone cycles of 5 minutes");
        Build(node, new PeerContactLog.Contact(InboundAt: Now.AddMinutes(-14))).Online.Should().BeFalse();
    }

    // ---- a clock that is ahead ----

    [Fact]
    public void A_time_a_little_ahead_of_this_clock_is_taken_as_now_and_one_far_ahead_is_not_a_contact()
    {
        var node = Node("http://peer");

        var small = Build(node, new PeerContactLog.Contact(OutboundOkAt: Now.AddSeconds(90), OutboundEndpoint: "http://peer"));
        var large = Build(node, new PeerContactLog.Contact(OutboundOkAt: Now.AddHours(3), OutboundEndpoint: "http://peer"));

        small.LastContactAt.Should().Be(Now, "a peer's clock a minute fast is not a reason to show the future");
        large.LastContactAt.Should().BeNull("a clock set back by hours says nothing about when the peer was last heard");
        large.Online.Should().BeFalse();
    }

    [Fact]
    public void A_peer_whose_clock_is_far_ahead_does_not_hide_a_real_contact()
    {
        var node = Node(address: null, protocolSeenAt: Now.AddDays(2));

        var view = Build(node, new PeerContactLog.Contact(InboundAt: Now.AddMinutes(-1)));

        view.LastInboundAt.Should().Be(Now.AddMinutes(-1), "the future stamp on disk is left out, the real one in memory stands");
    }

    // ---- not known yet ----

    [Fact]
    public void After_a_restart_a_node_that_is_called_is_unknown_not_answering_and_shows_what_the_disk_remembers()
    {
        var node = Node("http://peer");

        var view = Build(node, memory: null, pulledAt: Now.AddMinutes(-7));

        view.State.Should().Be(PeerContactView.Unknown, "this process has not called it yet: it cannot say that it answers");
        view.LastContactAt.Should().Be(Now.AddMinutes(-7));
        view.LastContactDirection.Should().Be("outbound");
        Build(node, new PeerContactLog.Contact(OutboundOkAt: Now.AddSeconds(-5), OutboundEndpoint: "http://peer"), pulledAt: Now.AddMinutes(-7))
            .State.Should().Be(PeerContactView.Answering, "one call that ended well is what the state needed");
    }

    [Fact]
    public void A_node_that_has_never_been_heard_from_is_unknown_with_no_time_and_a_node_without_an_address_is_calls_us()
    {
        Build(Node("http://peer")).Should().Match<DeliveryNodeStatus>(s => s.State == PeerContactView.Unknown && s.LastContactAt == null && !s.Online);
        Build(Node(address: null)).State.Should().Be(PeerContactView.CallsUs);
    }

    // ---- the failure ----

    [Fact]
    public void A_failing_node_is_not_answering_since_the_streak_began_unless_it_called_in_after_that()
    {
        var node = Node("http://peer");
        var streak = new PeerContactLog.Contact(
            OutboundOkAt: Now.AddHours(-1), OutboundFailingSince: Now.AddMinutes(-20), OutboundFailure: "no answer", OutboundFailureAt: Now.AddMinutes(-1), OutboundEndpoint: "http://peer");

        var failing = Build(node, streak);
        var heardSince = Build(node, streak with { InboundAt = Now.AddMinutes(-5) });

        failing.State.Should().Be(PeerContactView.NotAnswering);
        failing.NotAnsweringSince.Should().Be(Now.AddMinutes(-20));
        failing.NotAnsweringReason.Should().Be("no answer");
        failing.Online.Should().BeFalse();
        heardSince.State.Should().Be(PeerContactView.Answering, "something was heard from it after the failures began: it is alive");
        heardSince.NotAnsweringSince.Should().BeNull();
    }

    [Fact]
    public void A_node_without_an_address_is_never_not_answering()
    {
        var node = Node(address: null);

        var view = Build(node, new PeerContactLog.Contact(OutboundFailingSince: Now.AddMinutes(-20), OutboundFailure: "stale", OutboundFailureAt: Now.AddMinutes(-1)));

        view.NotAnsweringSince.Should().BeNull("nobody calls it, so nobody failed to reach it");
        view.State.Should().Be(PeerContactView.CallsUs);
    }

    // ---- a blind node ----

    [Fact]
    public void The_my_standing_call_of_this_pc_to_a_blind_node_is_not_the_node_calling_in()
    {
        var blind = Node("https://blind.test", BlindNodeId.NewId(), protocolSeenAt: Now.AddMinutes(-1));   // this PC stamped it when the node answered

        var view = Build(blind, pulledAt: Now.AddMinutes(-10));

        view.LastInboundAt.Should().BeNull("the column records the blind node's answer to a call of THIS PC");
        view.LastContactDirection.Should().Be("outbound");
        view.LastContactAt.Should().Be(Now.AddMinutes(-10));
    }

    [Fact]
    public void A_blind_copy_without_an_address_that_calls_this_pc_is_inbound_contact()
    {
        var copy = Node(address: null, BlindNodeId.NewId(), protocolSeenAt: Now.AddMinutes(-2));

        var view = Build(copy);

        view.LastInboundAt.Should().Be(Now.AddMinutes(-2));
        view.LastContactDirection.Should().Be("inbound");
    }

    // ---- an address that changed ----

    [Fact]
    public void What_a_log_learned_at_another_address_does_not_describe_the_node_at_this_one()
    {
        var log = new PeerContactLog();
        var node = Node("http://new.local");
        log.NoteOutboundFailure(node.NodeId, Now.AddMinutes(-20), "no answer", "http://old.local");
        log.NoteOutboundOk(node.NodeId, Now.AddMinutes(-30), "http://old.local");

        var view = PeerContactView.Build(node, log.Get(node.NodeId, node.ApiAddress), pulledAt: null, push: null, headSeq: 0, unsyncedCount: 0, Now);

        view.State.Should().Be(PeerContactView.Unknown);
        view.NotAnsweringSince.Should().BeNull("the streak was the old address's");
        view.LastContactAt.Should().BeNull();
    }
}
