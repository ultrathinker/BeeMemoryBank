using System.Net;
using BeeMemoryBank.Sync;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// BMB-313: when this node last made contact with a peer, in either direction, and how it is going. Contact is a completed session, not an event: it
/// is kept apart from the position tables, which say how far events went.
/// </summary>
public class PeerContactLogTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_peer_nobody_has_heard_of_has_no_contact_at_all()
    {
        var contact = new PeerContactLog().Get(Guid.NewGuid());

        contact.Should().Be(new PeerContactLog.Contact());
    }

    [Fact]
    public void The_two_directions_are_kept_apart()
    {
        var log = new PeerContactLog();
        var peer = Guid.NewGuid();

        log.NoteOutboundOk(peer, T0);
        log.NoteInbound(peer, T0.AddMinutes(3));

        var contact = log.Get(peer);
        contact.OutboundOkAt.Should().Be(T0, "this node called at T0");
        contact.InboundAt.Should().Be(T0.AddMinutes(3), "the peer called three minutes later");
        contact.OutboundFailingSince.Should().BeNull();
    }

    [Fact]
    public void A_streak_of_failed_calls_keeps_its_start_and_names_the_last_reason_until_a_call_ends_well()
    {
        var log = new PeerContactLog();
        var peer = Guid.NewGuid();
        log.NoteOutboundOk(peer, T0);

        log.NoteOutboundFailure(peer, T0.AddMinutes(1), "no answer: connection refused");
        log.NoteOutboundFailure(peer, T0.AddMinutes(2), "no answer: it did not answer in time");

        var failing = log.Get(peer);
        failing.OutboundFailingSince.Should().Be(T0.AddMinutes(1), "the streak began with the first failure after the good call");
        failing.OutboundFailure.Should().Be("no answer: it did not answer in time");
        failing.OutboundOkAt.Should().Be(T0, "the last good call is still the last good call");

        log.NoteOutboundOk(peer, T0.AddMinutes(3));
        var healed = log.Get(peer);
        healed.OutboundFailingSince.Should().BeNull();
        healed.OutboundFailure.Should().BeNull();
        healed.OutboundOkAt.Should().Be(T0.AddMinutes(3));
    }

    [Fact]
    public void A_gap_longer_than_a_computer_sleeping_starts_the_streak_again()
    {
        var log = new PeerContactLog();
        var peer = Guid.NewGuid();
        log.NoteOutboundFailure(peer, T0, "no answer");

        log.NoteOutboundFailure(peer, T0 + PeerContactLog.StreakBreak + TimeSpan.FromMinutes(1), "no answer");

        log.Get(peer).OutboundFailingSince.Should().Be(T0 + PeerContactLog.StreakBreak + TimeSpan.FromMinutes(1),
            "the hours this computer slept through are not hours the peer was down");
    }

    [Fact]
    public void A_forgotten_peer_is_a_stranger_again_and_one_peer_does_not_touch_another()
    {
        var log = new PeerContactLog();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        log.NoteInbound(a, T0);
        log.NoteOutboundFailure(b, T0, "no answer");

        log.Forget(a);

        log.Get(a).Should().Be(new PeerContactLog.Contact());
        log.Get(b).OutboundFailingSince.Should().Be(T0);
    }

    [Fact]
    public void A_failure_is_described_in_words_a_person_can_act_on()
    {
        PeerContactLog.Describe(new HttpRequestException("x", null, HttpStatusCode.Forbidden))
            .Should().Contain("answered 403").And.Contain("closed to this node");
        PeerContactLog.Describe(new HttpRequestException("x", null, HttpStatusCode.Unauthorized)).Should().Contain("answered 401");
        PeerContactLog.Describe(new HttpRequestException("x", null, HttpStatusCode.InternalServerError)).Should().Be("it answered 500 InternalServerError");
        PeerContactLog.Describe(new HttpRequestException("boom", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused)))
            .Should().Be("no answer: connection refused (nothing listens at that address)");
        PeerContactLog.Describe(new HttpRequestException("boom")).Should().Be("no answer");
        PeerContactLog.Describe(new TaskCanceledException()).Should().Be("no answer: it did not answer in time");
        PeerContactLog.Describe(new PushGapException("http://x", Guid.NewGuid(), 1, 2, 3)).Should().Contain("rejoin");
        PeerContactLog.Describe(new PeerIdentityMismatchException(Guid.NewGuid(), Guid.NewGuid()))
            .Should().Contain("another node answers at this address").And.NotContain("http");
    }

    /// <summary>
    /// BMB-313 fix round, F1: what a person sees as the reason is a CLOSED set. The text of an exception - an address with a user name in it, a token in a
    /// header, a path of this computer, a parser's message - never crosses to the status page, whatever kind of exception it is.
    /// </summary>
    [Fact]
    public void The_text_of_an_exception_never_becomes_the_reason()
    {
        var leaks = new Exception[]
        {
            new InvalidOperationException("Peer at http://user:s3cret@10.0.0.5:5300/api declares NodeId a, but we dialed it as b."),
            new IOException(@"The process cannot access the file 'C:\Users\owner\BeeMemoryBank\data\beememorybank.db' because it is being used"),
            new Exception("Authorization: Bearer eyJhbGciOi.payload.signature was rejected"),
            new System.Text.Json.JsonException("'<' is invalid at $.token LineNumber: 0 | BytePositionInLine: 3, in /srv/bmb/secret.json"),
            new HttpRequestException("Connection to https://10.1.2.3:5300/api/sync/events?key=abc failed"),
            new ArgumentException("param 'sk-live-0123456789'"),
        };

        foreach (var ex in leaks)
        {
            var reason = PeerContactLog.Describe(ex);
            reason.Should().NotContain("http").And.NotContain("s3cret").And.NotContain("Bearer").And.NotContain("C:\\").And.NotContain("/srv").And.NotContain("key=")
                .And.NotContain("sk-live").And.NotContain("10.").And.NotContain(ex.Message, ex.GetType().Name);
        }
        PeerContactLog.Describe(new ArgumentException("anything")).Should().Be("the call failed (the details are in this node's log)", "an exception nobody classified is one fixed sentence");
        PeerContactLog.Describe(new InvalidOperationException(new string('x', 400))).Should().Be("the call failed (the details are in this node's log)");
    }

    // ---- BMB-313 fix round, F4: the lifecycle of what is remembered ----

    [Fact]
    public void What_was_learned_at_another_address_is_not_shown_for_the_new_one_and_the_peers_own_calls_stay()
    {
        var log = new PeerContactLog();
        var peer = Guid.NewGuid();
        log.NoteOutboundFailure(peer, T0, "no answer", "http://old.local:5300/");
        log.NoteInbound(peer, T0.AddMinutes(1));

        var atNew = log.Get(peer, "http://new.local:5300");
        var atOld = log.Get(peer, "HTTP://OLD.local:5300");

        atNew.OutboundFailingSince.Should().BeNull("the streak was earned at the old address");
        atNew.OutboundFailure.Should().BeNull();
        atNew.InboundAt.Should().Be(T0.AddMinutes(1), "the peer's own call is a fact about the peer, not about an address of ours");
        atOld.OutboundFailingSince.Should().Be(T0, "the same address in another spelling is the same address");
    }

    [Fact]
    public void A_failure_at_a_new_address_starts_a_new_streak_and_a_good_call_there_does_not_inherit_the_old_failure()
    {
        var log = new PeerContactLog();
        var peer = Guid.NewGuid();
        log.NoteOutboundFailure(peer, T0, "no answer", "http://old.local");

        log.NoteOutboundFailure(peer, T0.AddMinutes(2), "it answered 403", "http://new.local");
        log.Get(peer, "http://new.local").OutboundFailingSince.Should().Be(T0.AddMinutes(2), "not T0: that failure was the old address's");

        log.NoteOutboundOk(peer, T0.AddMinutes(3), "http://other.local");
        var contact = log.Get(peer, "http://other.local");
        contact.OutboundFailure.Should().BeNull();
        contact.OutboundOkAt.Should().Be(T0.AddMinutes(3));
    }

    [Fact]
    public void Forgetting_the_outbound_side_keeps_the_inbound_side_and_forgetting_a_peer_keeps_nothing()
    {
        var log = new PeerContactLog();
        var peer = Guid.NewGuid();
        log.NoteOutboundOk(peer, T0, "http://a");
        log.NoteInbound(peer, T0.AddMinutes(1));

        log.ForgetOutbound(peer);
        log.Get(peer).Should().Be(new PeerContactLog.Contact(InboundAt: T0.AddMinutes(1)));

        log.Forget(peer);
        log.Get(peer).Should().Be(new PeerContactLog.Contact());
        log.Count.Should().Be(0);
    }

    [Fact]
    public void The_log_is_bounded_it_keeps_the_peers_on_the_whitelist_and_drops_the_stale_ones()
    {
        var log = new PeerContactLog();
        var kept = Guid.NewGuid();
        var revoked = Guid.NewGuid();
        var stale = Guid.NewGuid();
        log.NoteOutboundOk(kept, T0, "http://a");
        log.NoteOutboundOk(revoked, T0, "http://b");
        log.NoteInbound(stale, T0 - PeerContactLog.Retention - TimeSpan.FromDays(1));
        for (var i = 0; i < 200; i++) log.NoteInbound(Guid.NewGuid(), T0);   // nodes a vault once met and no longer has

        log.Reconcile(new HashSet<Guid> { kept, stale }, T0);

        log.Get(kept).OutboundOkAt.Should().Be(T0);
        log.Get(revoked).Should().Be(new PeerContactLog.Contact(), "a peer that is no longer on the whitelist is forgotten");
        log.Get(stale).Should().Be(new PeerContactLog.Contact(), "and one nobody has touched within the retention is too, though it is on the whitelist");
        log.Count.Should().Be(1, "the memory is the size of the whitelist, not of every node ever met");
    }
}
