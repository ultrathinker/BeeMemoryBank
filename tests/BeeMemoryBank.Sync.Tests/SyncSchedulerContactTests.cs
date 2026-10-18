using System.Net;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// BMB-313: the loop leaves a trace of every call it makes - a session that ended well is contact "we -> it", one that did not is a failure with its
/// reason and the start of its streak - and one session can be run on request (a double-click on a node of the sync status).
/// </summary>
public class SyncSchedulerContactTests : IAsyncLifetime
{
    private sealed class Fixture : SyncTestFixture { }

    private Fixture _node = null!;
    private SyncClient _client = null!;
    private readonly Guid _peerId = Guid.NewGuid();
    private readonly SyncTrigger _trigger = new();

    public async Task InitializeAsync()
    {
        _node = new Fixture();
        await _node.InitializeAsync();
        await _node.InitService.InitializeAsync("admin", "LocalNode", "pass");
        await _node.Session.UnlockAsync("pass");
        _client = new SyncClient(_node.NodeRepo, _node.EventLogRepo, new SyncPositionRepository(_node.Factory),
            new SyncPushPositionRepository(_node.Factory), _node.EventApplier, new SessionNodeAuthSigner(_node.Session),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncClient>.Instance, new PeerNewerProtocolState(),
            _node.QuarantineRepo, new BlobRepository(_node.Factory), blindState: new BlindState(_node.Factory));
    }

    public Task DisposeAsync() => _node.DisposeAsync();

    private enum Answer { Good, NoAnswer, Forbidden }

    /// <summary>One fake peer that speaks the sync protocol when it is well, and is refused or silent when it is not.</summary>
    private sealed class Peer(Guid id) : HttpMessageHandler
    {
        public Answer Mode { get; set; } = Answer.Good;

        public int Requests { get; private set; }

        /// <summary>How many sessions were begun: every one starts with the identity request.</summary>
        public int Sessions { get; private set; }

        /// <summary>While set, the identity request waits for it: a session that is in flight.</summary>
        public TaskCompletionSource? Hold { get; set; }

        /// <summary>The addresses that were called, in order.</summary>
        public List<string> Hosts { get; } = [];

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            Hosts.Add(request.RequestUri!.Host);
            if (request.RequestUri.AbsolutePath.EndsWith("/api/sync/identity"))
            {
                Sessions++;
                if (Hold is { } gate) await gate.Task.WaitAsync(ct);
            }
            if (Mode == Answer.NoAnswer)
                throw new HttpRequestException("refused", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
            if (Mode == Answer.Forbidden) return new HttpResponseMessage(HttpStatusCode.Forbidden);
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage r =
                path.EndsWith("/api/sync/sentinel") ? new(HttpStatusCode.NotFound)
                : path.EndsWith("/api/sync/identity") ? Json(new { nodeId = id, displayName = "peer", ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]), protocolVersion = SyncProtocolVersion.Current })
                : path.EndsWith("/api/sync/challenge") ? Json(new { challenge = Convert.ToBase64String(new byte[32]), serverNodeId = id })
                : path.EndsWith("/api/sync/authenticate") ? Json(new { token = "t" })
                : path.EndsWith("/api/sync/report-position") ? new(HttpStatusCode.OK)
                : path.EndsWith("/api/sync/events") && request.Method == HttpMethod.Get ? Json(Array.Empty<object>())
                : path.EndsWith("/api/sync/events") ? Json(new { applied = 0, skipped = 0, lastAppliedSequence = (long?)null, dropped = 0 })
                : new(HttpStatusCode.NotFound);
            return r;
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private async Task<(SyncScheduler Scheduler, Peer Peer, PeerContactLog Log)> NodeAsync(string? address = "http://peer.local")
    {
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = _peerId, DisplayName = "peer", Ed25519PublicKey = new byte[32], ApiAddress = address,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var peer = new Peer(_peerId);
        var log = new PeerContactLog();
        var services = new ServiceCollection().AddSingleton(new InvisibleModeService()).AddSingleton(_node.WhitelistRepo).AddSingleton(_client).AddSingleton(log)
            .BuildServiceProvider();
        var scheduler = new SyncScheduler(services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncScheduler>.Instance, _trigger, new Factory(peer))
        {
            CheckMinGap = TimeSpan.Zero,   // these tests ask again at once; the rate limit has its own test
        };
        return (scheduler, peer, log);
    }

    [Fact]
    public async Task A_cycle_that_ends_well_is_contact_we_to_it_even_when_no_event_moved()
    {
        var (scheduler, _, log) = await NodeAsync();
        var before = DateTime.UtcNow;

        await scheduler.SyncAllAsync(CancellationToken.None);

        var contact = log.Get(_peerId, "http://peer.local");
        contact.OutboundOkAt.Should().BeOnOrAfter(before, "an empty pull and an empty push are still a session");
        contact.OutboundFailingSince.Should().BeNull();
        contact.InboundAt.Should().BeNull("nobody called this node");
    }

    [Fact]
    public async Task A_peer_that_does_not_answer_starts_a_streak_with_its_reason_and_one_good_call_ends_it()
    {
        var (scheduler, peer, log) = await NodeAsync();
        await scheduler.SyncAllAsync(CancellationToken.None);
        var lastGood = log.Get(_peerId).OutboundOkAt;
        peer.Mode = Answer.NoAnswer;

        await scheduler.SyncAllAsync(CancellationToken.None);

        var failing = log.Get(_peerId);
        failing.OutboundFailingSince.Should().NotBeNull();
        failing.OutboundFailure.Should().Be("no answer: connection refused (nothing listens at that address)");
        failing.OutboundOkAt.Should().Be(lastGood, "a failed call does not move the last good one");

        peer.Mode = Answer.Good;
        var result = await scheduler.CheckPeerNowAsync(_peerId);   // the pause of the loop for an unreachable peer does not hold a request of a person back
        result.Answered.Should().BeTrue();
        var healed = log.Get(_peerId);
        healed.OutboundFailingSince.Should().BeNull();
        healed.OutboundOkAt.Should().BeAfter(lastGood!.Value);
    }

    [Fact]
    public async Task Checking_a_node_now_runs_one_session_and_says_what_it_found()
    {
        var (scheduler, peer, log) = await NodeAsync();

        var good = await scheduler.CheckPeerNowAsync(_peerId);

        good.Should().Be(new SyncScheduler.PeerCheck(Called: true, Answered: true, Busy: false, Applied: 0, Reason: null));
        log.Get(_peerId).OutboundOkAt.Should().NotBeNull();

        peer.Mode = Answer.Forbidden;
        var closed = await scheduler.CheckPeerNowAsync(_peerId);

        closed.Called.Should().BeTrue();
        closed.Answered.Should().BeFalse();
        closed.Reason.Should().Contain("answered 403").And.Contain("closed to this node");
        log.Get(_peerId).OutboundFailure.Should().Be(closed.Reason);

        peer.Mode = Answer.NoAnswer;
        (await scheduler.CheckPeerNowAsync(_peerId)).Reason.Should().StartWith("no answer");
    }

    [Fact]
    public async Task A_node_without_an_address_is_not_called_it_calls_this_node()
    {
        var (scheduler, peer, log) = await NodeAsync(address: null);

        var check = await scheduler.CheckPeerNowAsync(_peerId);

        check.Should().Be(new SyncScheduler.PeerCheck(Called: false, Answered: false, Busy: false, Applied: 0, Reason: null));
        peer.Requests.Should().Be(0);
        log.Get(_peerId).Should().Be(new PeerContactLog.Contact());
        (await scheduler.CheckPeerNowAsync(Guid.NewGuid())).Called.Should().BeFalse("a node that is not in the whitelist is not called either");
    }

    private async Task MoveThePeerToAsync(string address)
    {
        var entry = (await _node.WhitelistRepo.GetByNodeIdAsync(_peerId))!;
        entry.ApiAddress = address;
        await _node.WhitelistRepo.UpdateAsync(entry);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the condition never came true");
            await Task.Delay(10);
        }
    }

    // ---- BMB-313 fix round, F2/F4/F5: a peer that is replaced, moved or revoked is not remembered as it was; a check cannot be stacked ----

    [Fact]
    public async Task A_peer_moved_to_another_address_starts_again_the_new_address_is_called_at_once_and_the_old_failure_is_not_its_own()
    {
        var (scheduler, peer, log) = await NodeAsync();
        peer.Mode = Answer.NoAnswer;
        await scheduler.SyncAllAsync(CancellationToken.None);
        scheduler.UnreachableSince().Should().ContainKey(_peerId, "the old address failed and the loop now leaves it alone for a while");
        var asked = peer.Requests;
        await scheduler.SyncAllAsync(CancellationToken.None);
        peer.Requests.Should().Be(asked, "the pause holds while nothing changed");

        await MoveThePeerToAsync("http://moved.local");
        peer.Mode = Answer.Good;
        await scheduler.SyncAllAsync(CancellationToken.None);

        peer.Hosts.Last().Should().Be("moved.local", "the pause belonged to the old address: the new one is called in the next cycle, not after the pause");
        scheduler.UnreachableSince().Should().NotContainKey(_peerId);
        var atNew = log.Get(_peerId, "http://moved.local");
        atNew.OutboundOkAt.Should().NotBeNull();
        atNew.OutboundFailingSince.Should().BeNull();
        log.Get(_peerId, "http://peer.local").OutboundFailure.Should().BeNull("what the old address earned does not outlive the move");
    }

    [Fact]
    public async Task A_failure_remembered_for_one_address_is_gone_after_a_move_even_when_the_new_address_is_not_called()
    {
        var (scheduler, peer, log) = await NodeAsync();
        peer.Mode = Answer.NoAnswer;
        await scheduler.SyncAllAsync(CancellationToken.None);
        var oldStreak = log.Get(_peerId, "http://peer.local").OutboundFailingSince;
        oldStreak.Should().NotBeNull();
        await Task.Delay(30);

        await MoveThePeerToAsync("http://moved.local");
        var check = await scheduler.CheckPeerNowAsync(_peerId);   // it reads the whitelist first: it sees the move

        check.Answered.Should().BeFalse("the fake peer still does not answer; the point is whose streak this is");
        log.Get(_peerId, "http://moved.local").OutboundFailingSince.Should().BeAfter(oldStreak!.Value, "a new streak starts at the new address");
    }

    [Fact]
    public async Task Telling_the_scheduler_a_peer_changed_forgets_its_state_and_asks_for_a_cycle()
    {
        var (scheduler, peer, log) = await NodeAsync();
        peer.Mode = Answer.NoAnswer;
        await scheduler.SyncAllAsync(CancellationToken.None);
        scheduler.UnreachableSince().Should().ContainKey(_peerId);
        (await _trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None)).Should().BeFalse("nothing has signalled yet");

        scheduler.PeerChanged(_peerId);

        scheduler.UnreachableSince().Should().NotContainKey(_peerId);
        log.Get(_peerId, "http://peer.local").OutboundFailure.Should().BeNull();
        (await _trigger.WaitAsync(TimeSpan.Zero, CancellationToken.None)).Should().BeTrue("the new state is tried at once, not at the next minute");
    }

    [Fact]
    public async Task A_peer_that_was_revoked_is_forgotten_by_the_next_cycle()
    {
        var (scheduler, peer, log) = await NodeAsync();
        await scheduler.SyncAllAsync(CancellationToken.None);
        log.Get(_peerId).OutboundOkAt.Should().NotBeNull();
        log.NoteInbound(_peerId, DateTime.UtcNow);

        await _node.WhitelistRepo.RevokeAsync(_peerId, new RowVersion(100, Guid.NewGuid()));
        await scheduler.SyncAllAsync(CancellationToken.None);

        log.Get(_peerId).Should().Be(new PeerContactLog.Contact(), "a node that is no longer on the whitelist leaves nothing behind");
        log.Count.Should().Be(0);
    }

    [Fact]
    public async Task Checks_of_one_node_started_together_run_ONE_session_and_the_others_are_told_busy_at_once()
    {
        var (scheduler, peer, _) = await NodeAsync();
        peer.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = scheduler.CheckPeerNowAsync(_peerId);
        await UntilAsync(() => peer.Sessions == 1);   // the first is inside the peer's session now
        var others = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => scheduler.CheckPeerNowAsync(_peerId)));

        others.Should().OnlyContain(c => c.Busy && !c.Called, "a second request does not queue behind the first and does not start a session of its own");
        first.IsCompleted.Should().BeFalse();
        peer.Hold.SetResult();
        (await first).Answered.Should().BeTrue();
        peer.Sessions.Should().Be(1);
    }

    [Fact]
    public async Task A_check_is_not_started_again_within_the_gap_and_is_again_after_it()
    {
        var (scheduler, peer, _) = await NodeAsync();
        scheduler.CheckMinGap = TimeSpan.FromMilliseconds(400);

        (await scheduler.CheckPeerNowAsync(_peerId)).Answered.Should().BeTrue();
        var soon = await scheduler.CheckPeerNowAsync(_peerId);

        soon.Busy.Should().BeTrue("one start per gap per node: a script cannot make this node call out in a loop");
        peer.Sessions.Should().Be(1);
        await Task.Delay(500);
        (await scheduler.CheckPeerNowAsync(_peerId)).Answered.Should().BeTrue();
        peer.Sessions.Should().Be(2);
    }

    [Fact]
    public async Task A_check_of_a_node_that_is_not_called_does_not_leave_a_busy_mark_behind()
    {
        var (scheduler, peer, _) = await NodeAsync(address: null);

        (await scheduler.CheckPeerNowAsync(_peerId)).Called.Should().BeFalse();
        (await scheduler.CheckPeerNowAsync(_peerId)).Busy.Should().BeFalse("the first one finished: nothing is in flight");
        peer.Requests.Should().Be(0);
    }

    [Fact]
    public async Task While_a_wipe_holds_the_loop_off_a_check_asks_nobody_and_says_it_is_busy()
    {
        var (scheduler, peer, _) = await NodeAsync();

        using (await scheduler.PauseAsync())
        {
            var check = await scheduler.CheckPeerNowAsync(_peerId);

            check.Busy.Should().BeTrue();
            check.Called.Should().BeFalse();
        }

        peer.Requests.Should().Be(0);
    }
}
