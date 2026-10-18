using System.Net;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// BMB-309: the sync loop of a blind node dials every whitelisted peer that has an address, once a minute, and a full node that does not let the blind
/// node's address in (a reverse proxy with an allow-list answers 403) drew a warning a minute for ever. A blind node now leaves a peer that answered 401
/// or 403 alone for an hour, with one line for the whole episode; it still calls a peer that answers (a hub it was paired from has to be called), and a
/// full node is as it was.
/// </summary>
public class SyncSchedulerBlindRoleTests : IAsyncLifetime
{
    private sealed class Fixture : SyncTestFixture { }

    private Fixture _node = null!;
    private SyncClient _client = null!;

    public async Task InitializeAsync()
    {
        _node = new Fixture();
        await _node.InitializeAsync();
        await _node.InitService.InitializeAsync("admin", "LocalNode", "pass");
        await _node.Session.UnlockAsync("pass");
        _client = new SyncClient(_node.NodeRepo, _node.EventLogRepo, new SyncPositionRepository(_node.Factory),
            new SyncPushPositionRepository(_node.Factory), _node.EventApplier, new SessionNodeAuthSigner(_node.Session),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncClient>.Instance, new PeerNewerProtocolState(),
            _node.QuarantineRepo, new BlobRepository(_node.Factory),
            blindState: new BlindState(_node.Factory));
    }

    public Task DisposeAsync() => _node.DisposeAsync();

    /// <summary>A peer that answers every request with one status, which a test can change (the full node behind a proxy that lets this address in, or not).</summary>
    private sealed class AnswerAll(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;

        /// <summary>The peer does not answer at all (a host that is down).</summary>
        public bool Unreachable { get; set; }

        /// <summary>What the network stack says about it: the message of the exception (a hostile or merely chatty one carries an address, a token, a proxy's words).</summary>
        public string UnreachableMessage { get; set; } = "refused";

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri!.AbsolutePath);
            if (Unreachable)
                throw new HttpRequestException(UnreachableMessage, new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));
            return Task.FromResult(new HttpResponseMessage(Status));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ListLogger : ILogger<SyncScheduler>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private async Task<(SyncScheduler Scheduler, AnswerAll Peer, ListLogger Log)> NodeAsync(INodeRole? role, HttpStatusCode answer)
    {
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = Guid.NewGuid(), DisplayName = "full", Ed25519PublicKey = new byte[32], ApiAddress = "http://full.local",
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var peer = new AnswerAll(answer);
        var services = new ServiceCollection().AddSingleton(new InvisibleModeService()).AddSingleton(_node.WhitelistRepo).AddSingleton(_client);
        if (role is not null) services.AddSingleton(role);
        var log = new ListLogger();
        var scheduler = new SyncScheduler(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), log, new SyncTrigger(), new Factory(peer));
        return (scheduler, peer, log);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_peer_that_is_closed_to_a_blind_node_costs_one_warning_and_one_call_an_hour_not_a_warning_a_minute(HttpStatusCode answer)
    {
        var (scheduler, peer, log) = await NodeAsync(new EnvironmentNodeRole("blind"), answer);

        for (var cycle = 0; cycle < 60; cycle++)   // an hour of cycles
            await scheduler.SyncAllAsync(CancellationToken.None);

        peer.Requests.Should().ContainSingle("the first cycle asks once; the other 59 leave the peer alone");
        var warnings = log.Entries.Where(e => e.Level >= LogLevel.Warning).ToList();
        warnings.Should().ContainSingle();
        warnings[0].Message.Should().Contain("full.local").And.Contain($"answers {(int)answer}").And.Contain("closed to this blind node").And.Contain("every 01:00:00");
    }

    [Fact]
    public async Task When_the_hour_has_passed_a_blind_node_asks_again_and_a_peer_that_is_still_closed_stays_one_line()
    {
        var (scheduler, peer, log) = await NodeAsync(new EnvironmentNodeRole("blind"), HttpStatusCode.Forbidden);
        scheduler.ClosedRetry = TimeSpan.Zero;   // "the hour has passed" at every cycle

        for (var cycle = 0; cycle < 5; cycle++)
            await scheduler.SyncAllAsync(CancellationToken.None);

        peer.Requests.Should().HaveCount(5, "it asked at every retry");
        log.Entries.Where(e => e.Level >= LogLevel.Warning).Should().ContainSingle("the repeats of one episode are debug lines");
    }

    [Fact]
    public async Task A_blind_node_still_calls_a_peer_that_answers_and_warns_about_a_peer_that_is_broken_in_another_way()
    {
        var (scheduler, peer, log) = await NodeAsync(new EnvironmentNodeRole("blind"), HttpStatusCode.InternalServerError);

        await scheduler.SyncAllAsync(CancellationToken.None);
        await scheduler.SyncAllAsync(CancellationToken.None);

        peer.Requests.Should().HaveCount(2, "a hub it was paired from has to be called, and a 500 is not 'closed to me'");
        log.Entries.Where(e => e.Level == LogLevel.Warning).Should().HaveCount(2);
    }

    [Fact]
    public async Task The_warning_about_an_unreachable_peer_says_the_kind_of_failure_and_the_type_and_never_the_exceptions_own_words()
    {
        // BMB-306/307 fix round 3 (G3): the exception's message carried an address, a token and a proxy's words straight into the log.
        var (scheduler, peer, log) = await NodeAsync(new EnvironmentNodeRole("full"), HttpStatusCode.OK);
        peer.Unreachable = true;
        peer.UnreachableMessage = "Could not reach https://evil.example/pay?token=tok_5c4b3a29 (192.0.2.7:443): the proxy said <html>Pay now</html>";

        await scheduler.SyncAllAsync(CancellationToken.None);

        var warning = log.Entries.Where(e => e.Level == LogLevel.Warning).Should().ContainSingle().Which.Message;
        warning.Should().Contain("full.local").And.Contain("is unreachable").And.Contain("connection refused").And.Contain("HttpRequestException");
        warning.Should().NotContain("tok_5c4b3a29").And.NotContain("evil.example").And.NotContain("192.0.2.7").And.NotContain("Pay now");
    }

    [Fact]
    public async Task A_full_node_is_unchanged_it_asks_every_cycle_and_warns_every_cycle_about_a_403()
    {
        var (scheduler, peer, log) = await NodeAsync(new EnvironmentNodeRole("full"), HttpStatusCode.Forbidden);

        for (var cycle = 0; cycle < 3; cycle++)
            await scheduler.SyncAllAsync(CancellationToken.None);

        peer.Requests.Should().HaveCount(3);
        log.Entries.Where(e => e.Level == LogLevel.Warning).Should().HaveCount(3, "this is what a blind node no longer does");
    }

    [Fact]
    public async Task A_node_with_no_declared_role_is_a_full_node()
    {
        var (scheduler, peer, _) = await NodeAsync(role: null, HttpStatusCode.Forbidden);

        await scheduler.SyncAllAsync(CancellationToken.None);
        await scheduler.SyncAllAsync(CancellationToken.None);

        peer.Requests.Should().HaveCount(2);
    }

    // ---- BMB-309 fix round: what an episode is, and what ends it ----

    private async Task MovePeerAsync(string address)
    {
        var row = (await _node.WhitelistRepo.GetAllActiveAsync()).Single();
        row.ApiAddress = address;
        await _node.WhitelistRepo.UpdateAsync(row);
    }

    private static int Episodes(ListLogger log) => log.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("closed to this blind node"));

    [Fact]
    public async Task A_closed_peer_that_is_moved_to_another_address_is_asked_at_once_and_a_refusal_there_is_a_new_episode()
    {
        var (scheduler, peer, log) = await NodeAsync(new EnvironmentNodeRole("blind"), HttpStatusCode.Forbidden);
        await scheduler.SyncAllAsync(CancellationToken.None);
        await scheduler.SyncAllAsync(CancellationToken.None);
        peer.Requests.Should().ContainSingle("closed for an hour at the old address");

        await MovePeerAsync("http://other.local");
        await scheduler.SyncAllAsync(CancellationToken.None);

        peer.Requests.Should().HaveCount(2, "the refusal was the old address's: the new one is asked in the next cycle, not after the hour");
        Episodes(log).Should().Be(2, "a refusal at another address is a new episode, with its own line");
        log.Entries.Last(e => e.Level == LogLevel.Warning).Message.Should().Contain("other.local");
        scheduler.ClosedEpisodes.Should().Be(1);
    }

    [Fact]
    public async Task An_answer_that_is_not_a_refusal_ends_the_episode_with_one_line_and_a_later_refusal_opens_another()
    {
        var (scheduler, peer, log) = await NodeAsync(new EnvironmentNodeRole("blind"), HttpStatusCode.Forbidden);
        scheduler.ClosedRetry = TimeSpan.Zero;
        await scheduler.SyncAllAsync(CancellationToken.None);
        scheduler.ClosedEpisodes.Should().Be(1);

        peer.Status = HttpStatusCode.BadGateway;   // the proxy lets this node in now, and the node behind it is not up
        await scheduler.SyncAllAsync(CancellationToken.None);

        scheduler.ClosedEpisodes.Should().Be(0, "it answered, and it was not a refusal");
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains("no longer answers 401/403") && e.Message.Contains("502"));

        peer.Status = HttpStatusCode.Forbidden;
        await scheduler.SyncAllAsync(CancellationToken.None);

        Episodes(log).Should().Be(2, "a refusal after the recovery is a new episode");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task An_answer_of_404_or_the_like_ends_the_episode_too_though_it_is_told_as_no_sync_server(HttpStatusCode answer)
    {
        var (scheduler, peer, log) = await NodeAsync(new EnvironmentNodeRole("blind"), HttpStatusCode.Forbidden);
        scheduler.ClosedRetry = TimeSpan.Zero;
        scheduler.Interval = TimeSpan.Zero;
        await scheduler.SyncAllAsync(CancellationToken.None);
        scheduler.ClosedEpisodes.Should().Be(1);

        peer.Status = answer;   // the proxy lets this node in now, and nothing that speaks sync stands behind the address
        await scheduler.SyncAllAsync(CancellationToken.None);

        scheduler.ClosedEpisodes.Should().Be(0, "it answered, and it was not a refusal");
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains("no longer answers 401/403") && e.Message.Contains("not a sync server"));
    }

    [Fact]
    public async Task A_peer_that_does_not_answer_at_all_does_not_end_the_episode()
    {
        var (scheduler, peer, log) = await NodeAsync(new EnvironmentNodeRole("blind"), HttpStatusCode.Forbidden);
        scheduler.ClosedRetry = TimeSpan.Zero;
        scheduler.Interval = TimeSpan.Zero;   // no pause for an unreachable peer in this test
        await scheduler.SyncAllAsync(CancellationToken.None);

        peer.Unreachable = true;   // its host is down: nothing is known about its door
        await scheduler.SyncAllAsync(CancellationToken.None);
        scheduler.ClosedEpisodes.Should().Be(1, "no answer is not an answer that says the door is open");

        peer.Unreachable = false;
        await Task.Delay(30);
        await scheduler.SyncAllAsync(CancellationToken.None);

        peer.Requests.Should().HaveCount(3, "the third cycle asked again and the refusal came back");
        Episodes(log).Should().Be(1, "a gap with no answer between two refusals is one episode, one line");
    }

    [Fact]
    public async Task A_revoked_peer_leaves_no_closed_episode_behind()
    {
        var (scheduler, _, _) = await NodeAsync(new EnvironmentNodeRole("blind"), HttpStatusCode.Forbidden);
        await scheduler.SyncAllAsync(CancellationToken.None);
        scheduler.ClosedEpisodes.Should().Be(1);

        await _node.WhitelistRepo.RevokeAsync((await _node.WhitelistRepo.GetAllActiveAsync()).Single().NodeId, new RowVersion(100, Guid.NewGuid()));
        await scheduler.SyncAllAsync(CancellationToken.None);

        scheduler.ClosedEpisodes.Should().Be(0, "the memory is the size of the whitelist");
    }

    [Fact]
    public void A_refusal_remembered_for_one_address_does_not_hold_back_another_and_a_new_address_opens_a_new_episode()
    {
        var closed = new ClosedPeers();
        var peer = Guid.NewGuid();
        var t0 = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

        closed.NoteClosed(peer, t0, ClosedPeers.DefaultRetry, "http://old.local").Should().BeTrue();

        closed.ShouldSkip(peer, t0.AddMinutes(1), "http://old.local").Should().BeTrue();
        closed.ShouldSkip(peer, t0.AddMinutes(1), "http://new.local").Should().BeFalse("the refusal came from the old address");
        closed.NoteClosed(peer, t0.AddMinutes(2), ClosedPeers.DefaultRetry, "http://new.local").Should().BeTrue("a refusal at a new address is a new episode");
        closed.ShouldSkip(peer, t0.AddMinutes(3), "http://old.local").Should().BeFalse("and the old address no longer holds it");
        closed.Count.Should().Be(1);
    }

    [Fact]
    public void The_memory_is_bounded_by_the_whitelist()
    {
        var closed = new ClosedPeers();
        var t0 = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        var kept = Guid.NewGuid();
        closed.NoteClosed(kept, t0, ClosedPeers.DefaultRetry);
        for (var i = 0; i < 100; i++) closed.NoteClosed(Guid.NewGuid(), t0, ClosedPeers.DefaultRetry);

        closed.Retain(new HashSet<Guid> { kept }).Should().Be(100);

        closed.Count.Should().Be(1);
        closed.ShouldSkip(kept, t0.AddMinutes(1)).Should().BeTrue();
    }

    [Fact]
    public async Task Refusals_that_arrive_together_open_exactly_one_episode()
    {
        var closed = new ClosedPeers();
        var peer = Guid.NewGuid();
        var t0 = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);
        using var start = new ManualResetEventSlim();

        var sessions = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return closed.NoteClosed(peer, t0, ClosedPeers.DefaultRetry, "http://x.local");
        })).ToList();
        start.Set();
        var opened = await Task.WhenAll(sessions);

        opened.Count(first => first).Should().Be(1, "one episode, one line, however many sessions report the refusal at the same moment");
    }

    [Fact]
    public void A_closed_peer_is_skipped_until_its_hour_is_up_and_the_line_is_for_the_first_refusal_only()
    {
        var closed = new ClosedPeers();
        var peer = Guid.NewGuid();
        var t0 = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

        closed.ShouldSkip(peer, t0).Should().BeFalse("nothing is known against it");
        closed.NoteClosed(peer, t0, ClosedPeers.DefaultRetry).Should().BeTrue("the first refusal opens the episode");
        closed.ShouldSkip(peer, t0.AddMinutes(59)).Should().BeTrue();
        closed.ShouldSkip(peer, t0.AddMinutes(61)).Should().BeFalse("an hour on, it is asked again");
        closed.NoteClosed(peer, t0.AddMinutes(61), ClosedPeers.DefaultRetry).Should().BeFalse("still the same episode: no second line");
        closed.ShouldSkip(peer, t0.AddMinutes(100)).Should().BeTrue("and the hour starts over");

        closed.NoteOpen(peer).Should().BeTrue("the peer answered again: the one line that says so");
        closed.NoteOpen(peer).Should().BeFalse();
        closed.ShouldSkip(peer, t0.AddMinutes(101)).Should().BeFalse();
        closed.NoteClosed(peer, t0.AddMinutes(102), ClosedPeers.DefaultRetry).Should().BeTrue("a later refusal is a new episode");
    }

    [Fact]
    public void Only_a_401_or_a_403_means_closed_to_me()
    {
        SyncScheduler.IsClosedToMe(new HttpRequestException("x", null, HttpStatusCode.Forbidden)).Should().BeTrue();
        SyncScheduler.IsClosedToMe(new HttpRequestException("x", null, HttpStatusCode.Unauthorized)).Should().BeTrue();
        SyncScheduler.IsClosedToMe(new HttpRequestException("x", null, HttpStatusCode.NotFound)).Should().BeFalse();
        SyncScheduler.IsClosedToMe(new HttpRequestException("x", null, HttpStatusCode.TooManyRequests)).Should().BeFalse();
        SyncScheduler.IsClosedToMe(new HttpRequestException("no answer")).Should().BeFalse("no answer at all is unreachable, not closed");
        SyncScheduler.IsClosedToMe(new InvalidOperationException("x")).Should().BeFalse();
    }
}
