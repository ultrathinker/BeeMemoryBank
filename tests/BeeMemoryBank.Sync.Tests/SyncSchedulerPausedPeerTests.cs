using System.Net;
using System.Text;
using System.Text.Json;
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
/// Release 2.7, the full node's side of a server blind node's limits: a push answered 503 + <c>X-BMB-Paused</c> is a PAUSE of that peer, not a failure. It costs
/// one information line for the whole episode, the peer is asked again when it said (an hour at most) and not in between, the unreachable back-off does not
/// grow, the cycle does not report a problem, and the Blind nodes page gets "paused by the blind node's owner". When the node takes data again the episode ends
/// with one line and the pause is gone from the contact record.
/// </summary>
public class SyncSchedulerPausedPeerTests : IAsyncLifetime
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
            blindState: new BlindState(_node.Factory),
            sentinelVerifier: new RemoteSentinelVerifier(_node.Session,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RemoteSentinelVerifier>.Instance));
    }

    public Task DisposeAsync() => _node.DisposeAsync();

    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    /// <summary>A server blind node: it identifies itself, lets this node in, serves the pull, and answers the push as the test says.</summary>
    private sealed class BlindPeer(Guid id) : HttpMessageHandler
    {
        /// <summary>What the push is answered with: the reason in <c>X-BMB-Paused</c>, or null for a node that takes data.</summary>
        public string? Paused { get; set; } = "traffic";

        /// <summary>The <c>Retry-After</c> of the paused answer, in seconds.</summary>
        public int RetryAfterSeconds { get; set; } = 7200;

        public int Pushes;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage r;
            if (path.EndsWith("/api/sync/sentinel")) r = new(HttpStatusCode.NotFound);
            else if (path.EndsWith("/api/sync/identity"))
                r = Json(new { nodeId = id, displayName = "friend's node", ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]), protocolVersion = SyncProtocolVersion.Current });
            else if (path.EndsWith("/api/sync/challenge")) r = Json(new { challenge = Convert.ToBase64String(new byte[32]), serverNodeId = id });
            else if (path.EndsWith("/api/sync/authenticate")) r = Json(new { token = "t" });
            else if (path.EndsWith("/api/sync/report-position")) r = new(HttpStatusCode.OK);
            else if (path.EndsWith("/api/sync/events") && request.Method == HttpMethod.Get) r = Json(Array.Empty<object>());
            else if (path.EndsWith("/api/sync/events"))
            {
                Interlocked.Increment(ref Pushes);
                if (Paused is { } reason)
                {
                    r = new(HttpStatusCode.ServiceUnavailable);
                    r.Headers.TryAddWithoutValidation(PeerPausedException.HeaderName, reason);
                    r.Headers.RetryAfter = new(TimeSpan.FromSeconds(RetryAfterSeconds));
                }
                else r = Json(new { applied = 1, skipped = 0, lastAppliedSequence = (long?)null, dropped = 0 });
            }
            else r = new(HttpStatusCode.NotFound);
            return Task.FromResult(r);
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

    /// <summary>The full node's reseeder, which finds the blind node's own limit in the way of the seed.</summary>
    private sealed class HeldBackReseeder(PeerPausedException pause) : IBlindPeerReseeder
    {
        public int Calls;

        public Task AfterSyncAsync(WhitelistEntry peer, HttpClient http, Exception? failure, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw pause;
        }
    }

    private async Task<(SyncScheduler Scheduler, BlindPeer Peer, ListLogger Log, PeerContactLog Contacts, Guid Id)> NodeAsync(IBlindPeerReseeder? reseeder = null)
    {
        var id = BlindNodeId.NewId();
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = "friend's node", Ed25519PublicKey = new byte[32], ApiAddress = "http://friend.local",
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        // Something to push: the loop only asks a peer for a push when this node has an event it has not sent.
        await _node.EventLogger.LogFolderCreateAsync(new Folder
        {
            Id = Guid.NewGuid(), Path = "/Pause/" + Guid.NewGuid().ToString("N"), Name = "Pause",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var peer = new BlindPeer(id);
        var contacts = new PeerContactLog();
        var collection = new ServiceCollection().AddSingleton(new InvisibleModeService()).AddSingleton(_node.WhitelistRepo).AddSingleton(_client).AddSingleton(contacts);
        if (reseeder is not null) collection.AddSingleton(reseeder);
        var services = collection.BuildServiceProvider();
        var log = new ListLogger();
        var scheduler = new SyncScheduler(services.GetRequiredService<IServiceScopeFactory>(), log, new SyncTrigger(), new Factory(peer));
        return (scheduler, peer, log, contacts, id);
    }

    [Fact]
    public async Task APausedBlindNode_CostsOneLineAndOnePushForTheEpisode_NotAFailureAndNoBackoff()
    {
        var (scheduler, peer, log, contacts, id) = await NodeAsync();

        SyncCycleResult last = null!;
        for (var cycle = 0; cycle < 30; cycle++)
            last = await scheduler.SyncAllAsync(CancellationToken.None);

        peer.Pushes.Should().Be(1, "the first cycle asks; the node said 'not for two hours', so the other 29 leave it alone");
        scheduler.PausedEpisodes.Should().Be(1);
        scheduler.UnreachableSince().Should().BeEmpty("a node that answers 'paused' is reachable: no streak, no back-off");
        last.Problem.Should().BeNull("a pause is not a problem of the cycle");
        last.PeerFailures.Should().BeNullOrEmpty();
        log.Entries.Where(e => e.Level >= LogLevel.Warning).Should().BeEmpty("nothing is wrong: no warning, no alarm");
        var lines = log.Entries.Where(e => e.Level >= LogLevel.Information && e.Message.Contains("is paused by its owner")).ToList();
        lines.Should().ContainSingle("one line for the whole episode");
        lines[0].Level.Should().Be(LogLevel.Information);
        lines[0].Message.Should().Contain("traffic").And.Contain("friend.local");

        var contact = contacts.Get(id, "http://friend.local");
        contact.PausedReason.Should().Be("traffic");
        contact.PausedByNode.Should().BeTrue();
        contact.PausedUntil.Should().NotBeNull("a traffic pause lifts at the node's next midnight, which it told");
        contact.OutboundFailingSince.Should().BeNull();
    }

    [Fact]
    public async Task ABusyBlindNode_IsSaidToBeBusyAndAskedAgainInAMinute_NotPausedByItsOwner_InTheLogAndInTheContact()
    {
        // srvfix8 (RV6-03): "busy" is the node's room held by other transfers, not a limit its owner set.
        var (scheduler, peer, log, contacts, id) = await NodeAsync();
        peer.Paused = "busy";
        peer.RetryAfterSeconds = 60;
        scheduler.PausedMinRetry = TimeSpan.Zero;

        await scheduler.SyncAllAsync(CancellationToken.None);
        await scheduler.SyncAllAsync(CancellationToken.None);

        log.Entries.Should().NotContain(e => e.Message.Contains("owner"), "nobody paused it: it is busy with other transfers");
        var lines = log.Entries.Where(e => e.Level == LogLevel.Information && e.Message.Contains("busy")).ToList();
        lines.Should().ContainSingle("one information line for the episode");
        lines[0].Message.Should().Contain("friend.local").And.Contain("minute");
        log.Entries.Where(e => e.Level >= LogLevel.Warning).Should().BeEmpty();
        var contact = contacts.Get(id, "http://friend.local");
        contact.PausedReason.Should().Be("busy");
        PeerPauseReport.Sentence(contact.PausedReason!, contact.PausedUntil, contact.PausedUntilOffsetMinutes, DateTime.UtcNow, contact.PausedByNode)
            .Should().NotContain("owner").And.Contain("Busy");

        peer.Paused = null;
        var check = await scheduler.CheckPeerNowAsync(id);   // asked by hand: a node that is waiting is asked anyway

        check.Answered.Should().BeTrue();
        log.Entries.Should().NotContain(e => e.Message.Contains("paused by its owner"), "the end of a busy episode does not claim an owner either");
        log.Entries.Count(e => e.Level == LogLevel.Information && e.Message.Contains("no longer busy")).Should().Be(1);
    }

    [Fact]
    public async Task ASeedTheBlindNodeHoldsBackForItsLimit_IsAPause_NotAFailedReseed()
    {
        // The push goes through; the reseed that follows it is stopped by the node's storage limit (503 + X-BMB-Paused on /api/blind/seed).
        var reseeder = new HeldBackReseeder(new PeerPausedException("disk", TimeSpan.FromHours(1)));
        var (scheduler, peer, log, contacts, id) = await NodeAsync(reseeder);
        peer.Paused = null;

        var cycle = await scheduler.SyncAllAsync(CancellationToken.None);

        reseeder.Calls.Should().Be(1);
        log.Entries.Where(e => e.Level >= LogLevel.Warning).Should().BeEmpty("a seed held back by the owner's limit is not an error and not a warning");
        scheduler.PausedEpisodes.Should().Be(1);
        cycle.Problem.Should().BeNull();
        var contact = contacts.Get(id, "http://friend.local");
        contact.PausedReason.Should().Be("disk");
        contact.PausedByNode.Should().BeTrue();
    }

    [Fact]
    public async Task AStoragePauseHasNoEndToShow_AndIsStillAPause()
    {
        var (scheduler, peer, _, contacts, id) = await NodeAsync();
        peer.Paused = "disk";

        await scheduler.SyncAllAsync(CancellationToken.None);

        var contact = contacts.Get(id, "http://friend.local");
        contact.PausedReason.Should().Be("disk");
        contact.PausedUntil.Should().BeNull("a storage limit lifts when its owner changes it, not at a time");
    }

    [Fact]
    public async Task TheNodeIsAskedAgainWhenItSaid_AndTheRepeatIsTheSameEpisode_ThenTheEpisodeEndsWithOneLine()
    {
        var (scheduler, peer, log, contacts, id) = await NodeAsync();
        scheduler.PausedMinRetry = TimeSpan.Zero;   // the node's own time is honoured below the quarter-minute floor, so a test need not wait for it
        peer.RetryAfterSeconds = 1;
        var now = DateTime.UtcNow;
        scheduler.PauseClock = () => now;           // the retry time runs on this clock: the test steps it, it does not sleep

        await scheduler.SyncAllAsync(CancellationToken.None);
        await scheduler.SyncAllAsync(CancellationToken.None);
        peer.Pushes.Should().Be(1, "still inside the second the node named");

        now += TimeSpan.FromMilliseconds(1200);
        await scheduler.SyncAllAsync(CancellationToken.None);
        peer.Pushes.Should().Be(2, "the time the node named has passed: asked again");
        log.Entries.Count(e => e.Level >= LogLevel.Information && e.Message.Contains("is paused by its owner")).Should().Be(1, "the repeat is the same episode (a debug line)");

        peer.Paused = null;   // the owner raised the limit
        now += TimeSpan.FromMilliseconds(1200);
        var resumed = await scheduler.SyncAllAsync(CancellationToken.None);

        resumed.Problem.Should().BeNull();
        scheduler.PausedEpisodes.Should().Be(0);
        log.Entries.Count(e => e.Message.Contains("no longer paused by its owner")).Should().Be(1);
        contacts.Get(id, "http://friend.local").PausedReason.Should().BeNull("a session that went through ends the pause");
    }

    [Fact]
    public async Task ACheckByHand_AsksAPausedNodeAnyway_AndSaysItAnsweredAndIsPaused()
    {
        var (scheduler, peer, _, contacts, id) = await NodeAsync();
        await scheduler.SyncAllAsync(CancellationToken.None);
        peer.Pushes.Should().Be(1);

        var check = await scheduler.CheckPeerNowAsync(id);

        check.Called.Should().BeTrue();
        check.Answered.Should().BeTrue("it answered: its door is open");
        check.Reason.Should().Be("it is paused by its owner (daily traffic limit)");
        peer.Pushes.Should().Be(2, "the person asked, so the pause is not a reason to stay away");
        contacts.Get(id, "http://friend.local").PausedReason.Should().Be("traffic");
    }

    [Fact]
    public async Task ARevokedNode_LeavesNoPauseBehind()
    {
        var (scheduler, _, _, _, id) = await NodeAsync();
        await scheduler.SyncAllAsync(CancellationToken.None);
        scheduler.PausedEpisodes.Should().Be(1);

        using (var conn = _node.Factory.CreateConnection())
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE tbl_whitelist SET status = 'R' WHERE node_id = @id COLLATE NOCASE", new { id = id.ToString() });
        await scheduler.SyncAllAsync(CancellationToken.None);

        scheduler.PausedEpisodes.Should().Be(0);
    }
}
