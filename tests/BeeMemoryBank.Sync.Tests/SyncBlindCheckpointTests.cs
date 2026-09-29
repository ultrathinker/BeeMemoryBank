using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// A blind node seeded by this node keeps a log that starts at the seed's checkpoint (its last_compaction_cp), and
/// answers 410 SEQUENCE_TOO_OLD to a pull from below it. A full node that has no pull position for it (a re-key
/// clears them) must adopt that checkpoint and go on, or it never pushes anything to the blind node again. Only a
/// blind peer, and only through the full node's entry (<see cref="SyncClient.SyncWithPeerAsync"/>, the scheduler's and
/// the reseed's); a phone's <see cref="SyncClient.SyncWithAsync"/> and a full peer's 410 stay refusals.
/// </summary>
public class SyncBlindCheckpointTests : IAsyncLifetime
{
    private const long Checkpoint = 54;
    private SyncTestFixture _node = null!;
    private SyncClient _client = null!;
    private SyncPositionRepository _positions = null!;
    private BlindState _blindState = null!;
    private MockHandler _handler = null!;
    private HttpClient _http = null!;
    private Guid _peerId;

    public async Task InitializeAsync()
    {
        _node = new Fixture();
        await _node.InitializeAsync();
        await _node.InitService.InitializeAsync("admin", "LocalNode", "pass");
        await _node.Session.UnlockAsync("pass");
        await _node.ArticleService.CreateAsync("Test Article", "/Root", new List<string>(), "content");

        _positions = new SyncPositionRepository(_node.Factory);
        _blindState = new BlindState(_node.Factory);
        _client = new SyncClient(_node.NodeRepo, _node.EventLogRepo, _positions, new SyncPushPositionRepository(_node.Factory),
            _node.EventApplier, _node.Session, new SessionNodeAuthSigner(_node.Session),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncClient>.Instance, new PeerNewerProtocolState(),
            _node.QuarantineRepo, new BlobRepository(_node.Factory), blindState: _blindState);

        _peerId = BlindNodeId.NewId();
        _handler = new MockHandler();
        _http = new HttpClient(_handler) { BaseAddress = new Uri("http://remote.local") };
        _handler.MapRoute("/api/sync/sentinel", _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        _handler.MapRoute("/api/sync/challenge", _ => Json(new { challenge = Convert.ToBase64String(new byte[32]), serverNodeId = _peerId }));
        _handler.MapRoute("/api/sync/authenticate", _ => Json(new { token = "test-token" }));
        _handler.MapRoute("/api/sync/report-position", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        _handler.MapRoute("/api/sync/blobs/check", req =>
        {
            var hashes = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                .RootElement.GetProperty("hashes").EnumerateArray().Select(h => h.GetString()!).ToList();
            return Json(new { missing = hashes });
        });
        _handler.MapRoute("/api/sync/blobs", _ => Json(new { stored = 1, rejected = 0 }));
        MapIdentity();
        MapEvents(_ => Checkpoint);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _node.DisposeAsync();
    }

    private static HttpResponseMessage Json(object body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private void MapIdentity() =>
        _handler.MapRoute("/api/sync/identity", _ => Json(new
        {
            nodeId = _peerId, displayName = "Peer", ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
            protocolVersion = SyncProtocolVersion.Current
        }));

    /// <summary>
    /// The peer's pull: 410 with <paramref name="checkpointFor"/>(afterSequence) when below it, else an empty page. The
    /// 410 has the shape a real blind node gives right after a seed: its log is empty, so the head it reports (the
    /// highest row, not the counter) is 0 while the checkpoint is the old head (the crit line of stand B: cp=54, head=0).
    /// </summary>
    private void MapEvents(Func<long, long> checkpointFor) =>
        MapGone(after =>
        {
            var cp = checkpointFor(after);
            return after < cp ? GoneBody(cp, head: 0) : null;
        });

    /// <summary>The peer's pull, with a 410 body of the test's own making (null = an empty page).</summary>
    private void MapGone(Func<long, Dictionary<string, object?>?> bodyFor) =>
        _handler.MapRoute("/api/sync/events", req =>
        {
            if (req.Method == HttpMethod.Post)
                return Json(new { applied = 1, skipped = 0, lastAppliedSequence = 1, dropped = 0 });
            var after = long.Parse(System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query)["afterSequence"] ?? "0");
            return bodyFor(after) is { } body ? Json(body, System.Net.HttpStatusCode.Gone) : Json(Array.Empty<object>());
        });

    /// <summary>A 410 body as SyncEndpoints writes it; a null member is left out, as a peer that does not send it would.</summary>
    private static Dictionary<string, object?> GoneBody(long cp, long? head, string? error = "SEQUENCE_TOO_OLD")
    {
        var body = new Dictionary<string, object?> { ["last_compaction_cp"] = cp, ["message"] = "too old" };
        if (head is { } h) body["current_head_seq"] = h;
        if (error is not null) body["error"] = error;
        return body;
    }

    private async Task<long?> PositionAsync() => (await _positions.GetAsync(_peerId))?.LastSequenceNum;

    private List<string> Pulls => _handler.CallLog.Where(c => c == "GET /api/sync/events").ToList();

    [Fact]
    public async Task ABlindPeerBelowItsCheckpoint_IsPulledAgainFromThatCheckpoint()
    {
        var afterValues = new List<long>();
        MapEvents(after => { afterValues.Add(after); return Checkpoint; });

        await _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        afterValues.Should().Equal([0L, Checkpoint], "one pull from nothing, refused; one from the peer's checkpoint");
    }

    [Fact]
    public async Task ABlindPeerBelowItsCheckpoint_AdoptsItAsThePullPosition_NotThePackagesOrZero()
    {
        await _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        (await PositionAsync()).Should().Be(Checkpoint, "the blind node's own checkpoint, in its own sequence space");
        _handler.CallLog.Should().Contain("POST /api/sync/events", "the push that the 410 used to cut off happens now");
        Pulls.Should().HaveCount(2, "one refusal, one retry");
    }

    [Fact]
    public async Task ABlindPeerThatRefusesAgainAfterTheAdoption_StillThrows()
    {
        var cp = Checkpoint;
        MapEvents(after => cp += 10); // the peer's checkpoint keeps moving above whatever position is adopted

        var sync = () => _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        await sync.Should().ThrowAsync<SnapshotRequiredException>();
        (await PositionAsync()).Should().Be(Checkpoint + 10, "adopted once, not chased");
        Pulls.Should().HaveCount(2);
    }

    [Fact]
    public async Task APlainSyncWithABlindPeer_IsRefusedAsBefore_ForAPhone()
    {
        var sync = () => _client.SyncWithAsync(_http, "http://remote.local", _peerId);

        (await sync.Should().ThrowAsync<SnapshotRequiredException>()).Which.LastCompactionCp.Should().Be(Checkpoint);
        (await PositionAsync()).Should().BeNull("a phone that pulled below a blind node's checkpoint adopts nothing");
    }

    [Fact]
    public async Task AFullPeers410_IsNeverAdopted_EvenThroughTheFullNodesEntry()
    {
        _peerId = Guid.NewGuid();
        MapIdentity();

        var sync = () => _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        await sync.Should().ThrowAsync<SnapshotRequiredException>();
        (await PositionAsync()).Should().BeNull();
        Pulls.Should().HaveCount(1, "no retry");
    }

    /// <summary>
    /// The other direction: the blind peer is behind OUR compaction (the pull works, the push is refused). That is not
    /// a checkpoint to adopt, and it must reach the reseeder as the PushGapException it is.
    /// </summary>
    [Fact]
    public async Task APushGapOfABlindPeer_IsNeverAdopted_AndStaysAPushGap()
    {
        MapEvents(_ => 0); // the pull is fine
        using (var conn = _node.Factory.CreateConnection())
        {
            await conn.ExecuteAsync(
                "INSERT INTO tbl_compaction_log (compacted_at, cp_before, cp_after, events_removed, reason) VALUES ('2026-09-29T00:00:00Z', NULL, 5, 0, 'test')");
        }
        await new SyncPushPositionRepository(_node.Factory).UpsertAsync(
            new SyncPushPosition { RemoteNodeId = _peerId, LastPushedSeq = 0, PushedAt = DateTime.UtcNow });

        var sync = () => _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        await sync.Should().ThrowAsync<PushGapException>();
        // (Release A records the row on every pull, an empty one included, so "unchanged" is 0 rather than absent.)
        (await PositionAsync()).GetValueOrDefault().Should().Be(0, "a push gap is about the peer's copy of OUR log, not about our pull position");
    }

    [Fact]
    public async Task APositionIsNeverMovedBackwards_ByACheckpointBelowIt()
    {
        await _positions.UpsertAsync(new SyncPosition { RemoteNodeId = _peerId, LastSequenceNum = 200, UpdatedAt = DateTime.UtcNow });
        // A 410 whose checkpoint (100) is below the stored position (200): inconsistent, and never a reason to go back.
        _handler.MapRoute("/api/sync/events", req => req.Method == HttpMethod.Post
            ? Json(new { applied = 1, skipped = 0, lastAppliedSequence = 1, dropped = 0 })
            : Json(new { error = "SEQUENCE_TOO_OLD", last_compaction_cp = 100L, current_head_seq = 100L, message = "odd" },
                System.Net.HttpStatusCode.Gone));

        var sync = () => _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        await sync.Should().ThrowAsync<SnapshotRequiredException>();
        (await PositionAsync()).Should().Be(200);
    }

    // ---------------------------------------------------------------------------- what a 410 may and may not adopt

    /// <summary>
    /// Only the code SyncEndpoints writes for "your position is older than my log" is that refusal. Any other 410 (a
    /// proxy's, a bug's, a peer's own idea) says nothing about a cursor, and must not become one.
    /// </summary>
    [Theory]
    [InlineData("SOMETHING_ELSE")]
    [InlineData("sequence_too_old")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A410WithoutTheSequenceTooOldCode_IsNeverAdopted(string? code)
    {
        MapGone(_ => GoneBody(Checkpoint, head: 0, error: code));

        var sync = () => _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        await sync.Should().ThrowAsync<SnapshotRequiredException>();
        (await PositionAsync()).Should().BeNull();
        Pulls.Should().HaveCount(1, "no retry");
    }

    /// <summary>
    /// A cursor that exists is the node's own record of what it has read, and a peer's 410 never moves it: that is how a
    /// peer could push it past events it has never served. Release A stores a row for every pull, an empty one (0)
    /// included, and that counts too.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(0)]
    public async Task APeerWeHoldAPositionFor_IsNeverAdvancedByA410(long stored)
    {
        await _positions.UpsertAsync(new SyncPosition { RemoteNodeId = _peerId, LastSequenceNum = stored, UpdatedAt = DateTime.UtcNow });

        var sync = () => _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        await sync.Should().ThrowAsync<SnapshotRequiredException>();
        (await PositionAsync()).Should().Be(stored);
        Pulls.Should().HaveCount(1, "no retry");
        _handler.CallLog.Should().NotContain("POST /api/sync/events", "the pull failed before the push, as for any 410");
    }

    /// <summary>One adoption per pairing: after it a cursor exists, so a later 410 (the peer's log now starts higher) is a refusal.</summary>
    [Fact]
    public async Task ASecond410_AfterAnAdoption_IsNotAdoptedAgain()
    {
        var cp = Checkpoint;
        MapGone(after => after < cp ? GoneBody(cp, head: 0) : null);
        await _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);
        (await PositionAsync()).Should().Be(Checkpoint);

        cp = Checkpoint + 26; // the peer's log now starts above what was adopted
        var pulls = Pulls.Count;
        var sync = () => _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        await sync.Should().ThrowAsync<SnapshotRequiredException>();
        (await PositionAsync()).Should().Be(Checkpoint, "an existing cursor is never advanced by a 410");
        Pulls.Count.Should().Be(pulls + 1, "and there is no retry");
    }

    /// <summary>
    /// The checkpoint has to fit inside the log the peer itself describes in the same answer: a claim above the head it
    /// reports is not a checkpoint of that log. (An empty log reports head 0 while its checkpoint is the old head: see
    /// the next tests.)
    /// </summary>
    [Theory]
    [InlineData(54L, 10L)]
    [InlineData(long.MaxValue, 100L)]
    public async Task ACheckpointAboveTheHeadThePeerReports_IsRefused(long cp, long head)
    {
        MapGone(_ => GoneBody(cp, head));

        var sync = () => _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        await sync.Should().ThrowAsync<SnapshotRequiredException>();
        (await PositionAsync()).Should().BeNull();
        Pulls.Should().HaveCount(1, "no retry");
    }

    [Fact]
    public async Task A410ThatReportsNoHead_IsRefused()
    {
        MapGone(_ => GoneBody(Checkpoint, head: null));

        var sync = () => _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        await sync.Should().ThrowAsync<SnapshotRequiredException>();
        (await PositionAsync()).Should().BeNull();
        Pulls.Should().HaveCount(1, "no retry");
    }

    /// <summary>The honest shapes: an empty log (head 0, the counter and the checkpoint at the old head), rows above the checkpoint, none above.</summary>
    [Theory]
    [InlineData(54L, 0L)]
    [InlineData(54L, 54L)]
    [InlineData(54L, 60L)]
    public async Task AnHonestBlindNodesCheckpoint_IsAdopted(long cp, long head)
    {
        MapGone(after => after < cp ? GoneBody(cp, head) : null);

        await _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        (await PositionAsync()).Should().Be(cp);
        Pulls.Should().HaveCount(2, "one refusal, one retry from the checkpoint");
        _handler.CallLog.Should().Contain("POST /api/sync/events");
    }

    /// <summary>
    /// The accepted limit, written down so that changing it is a decision: a peer that says its log is empty cannot be
    /// checked against a head, so its checkpoint is taken at its word. It can claim what it likes inside "nothing", which
    /// is withholding events, and what it withholds a phone still has and pushes to this node directly.
    /// </summary>
    [Fact]
    public async Task AnEmptyLogsCheckpoint_CannotBeChecked_AndIsTakenAtItsWord()
    {
        MapGone(after => after < 1_000_000 ? GoneBody(1_000_000, head: 0) : null);

        await _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        (await PositionAsync()).Should().Be(1_000_000);
    }

    /// <summary>The Blind nodes page shows "adopted checkpoint N" from this note, so the owner can see what was taken.</summary>
    [Fact]
    public async Task AnAdoption_IsNotedForTheBlindNodesPage_AndARefusalLeavesNoNote()
    {
        await _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);
        (await _blindState.GetAdoptedCheckpointsAsync()).Should().Equal(new Dictionary<Guid, long> { [_peerId] = Checkpoint });

        await _blindState.ClearAdoptedCheckpointAsync(_peerId);
        (await _blindState.GetAdoptedCheckpointsAsync()).Should().BeEmpty("a new pairing or a reseed clears it");

        await _positions.UpsertAsync(new SyncPosition { RemoteNodeId = _peerId, LastSequenceNum = 3, UpdatedAt = DateTime.UtcNow });
        var refused = () => _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);
        await refused.Should().ThrowAsync<SnapshotRequiredException>();
        (await _blindState.GetAdoptedCheckpointsAsync()).Should().BeEmpty("nothing was adopted");
    }

    // ---------------------------------------------------------------------------- the scheduler's cycle

    private async Task<(SyncScheduler Scheduler, CapturingLogger<SyncScheduler> Log, SnapshotRequiredState State)> SchedulerAsync()
    {
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = _peerId, DisplayName = "Peer", Ed25519PublicKey = new byte[32], ApiAddress = "http://remote.local",
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var services = new ServiceCollection()
            .AddSingleton(new InvisibleModeService())
            .AddSingleton(_node.WhitelistRepo)
            .AddSingleton(_client)
            .BuildServiceProvider();
        var log = new CapturingLogger<SyncScheduler>();
        var state = new SnapshotRequiredState();
        var scheduler = new SyncScheduler(services.GetRequiredService<IServiceScopeFactory>(), log, new SyncTrigger(),
            new FixedHttpClientFactory(_handler), snapshotRequiredState: state);
        return (scheduler, log, state);
    }

    [Fact]
    public async Task TheSchedulersCycle_WithABlindPeer_AdoptsItsCheckpoint_AndPushes()
    {
        var (scheduler, log, state) = await SchedulerAsync();

        await scheduler.SyncAllAsync(CancellationToken.None);

        (await PositionAsync()).Should().Be(Checkpoint);
        _handler.CallLog.Should().Contain("POST /api/sync/events");
        log.Entries.Should().NotContain(e => e.Level == LogLevel.Critical, "there is nothing to be alarmed about any more");
        state.LastException.Should().BeNull();
    }

    [Fact]
    public async Task ABlindRefusalThatSurvivesTheAdoption_SaysTheBlindNodeRefused_AndDoesNotAskForAWipe()
    {
        var cp = Checkpoint;
        MapEvents(_ => cp += 10);
        var (scheduler, log, state) = await SchedulerAsync();

        await scheduler.SyncAllAsync(CancellationToken.None);

        var critical = log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical).Subject.Message;
        critical.Should().Contain("Blind node").And.Contain("refused our pull").And.Contain("not to be wiped");
        critical.Should().NotContain("Manual wipe");
        state.LastException.Should().BeNull("the banner that tells a node to wipe itself is not raised for a blind node's refusal");
    }

    [Fact]
    public async Task AForgedCheckpoint_IsNotAdopted_AndTheCriticalSaysTheBlindNodeRefused()
    {
        MapGone(_ => GoneBody(long.MaxValue, head: 100));
        var (scheduler, log, state) = await SchedulerAsync();

        await scheduler.SyncAllAsync(CancellationToken.None);

        (await PositionAsync()).Should().BeNull();
        _handler.CallLog.Should().NotContain("POST /api/sync/events");
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical).Which.Message.Should().Contain("Blind node").And.Contain("refused our pull");
        state.LastException.Should().BeNull();
    }

    [Fact]
    public async Task AFullPeersRefusal_StaysACritical_AndKeepsAskingForTheWipe()
    {
        _peerId = Guid.NewGuid();
        MapIdentity();
        var (scheduler, log, state) = await SchedulerAsync();

        await scheduler.SyncAllAsync(CancellationToken.None);

        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical).Which.Message.Should().Contain("Manual wipe & rejoin required");
        state.LastException.Should().NotBeNull();
        (await PositionAsync()).Should().BeNull();
    }

    private sealed class Fixture : SyncTestFixture { }

    /// <summary>Hands the scheduler a client on the same fake peer; the scheduler disposes what it gets, not the handler.</summary>
    private sealed class FixedHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://remote.local") };
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception) + (exception is null ? "" : " [" + exception + "]")));
    }

    private sealed class MockHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();
        public List<string> CallLog { get; } = [];

        public void MapRoute(string path, Func<HttpRequestMessage, HttpResponseMessage> handler) => _routes[path] = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("No URI");
            CallLog.Add($"{request.Method} {uri.AbsolutePath}");
            foreach (var (path, handler) in _routes)
                if (uri.AbsolutePath.EndsWith(path, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(handler(request));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
