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
/// A node that joined the network from a snapshot of ONE node (its host) holds a pull position for the host only: the
/// package carries no positions, and the host's numbers are in the host's sequence space anyway. Every other full peer
/// it imported from the host's whitelist is first asked for its log from 0. A peer that has compacted answers that with
/// 410 SEQUENCE_TOO_OLD, on every cycle, and the node said "wipe this node and join again" - which made the same node
/// again (a macOS node joined through a Windows PC, while the server had compacted long before).
/// <para>What the peer compacted, every peer it had then had received (its compaction refuses otherwise), and this node
/// holds the full tail of such a peer (the host): so for a peer it has never pulled from, the peer's own checkpoint is
/// where to start, once. Only then: a cursor that exists is never moved by a 410, a node with no other full peer to stand
/// on (a recovery restore keeps only the blind node's position) still gets the refusal, and so does a 410 whose body
/// cannot be read or contradicts itself.</para>
/// </summary>
public class SyncFullPeerCheckpointTests : IAsyncLifetime
{
    private const long Checkpoint = 6104;
    private const long Head = 7604;
    private SyncTestFixture _node = null!;
    private SyncClient _client = null!;
    private SyncPositionRepository _positions = null!;
    private MockHandler _handler = null!;
    private HttpClient _http = null!;
    private Guid _peerId;
    private readonly Guid _hostId = Guid.NewGuid();
    private readonly List<long> _pulledAfter = [];
    private readonly List<string> _reported = [];

    public async Task InitializeAsync()
    {
        _node = new Fixture();
        await _node.InitializeAsync();
        await _node.InitService.InitializeAsync("admin", "LocalNode", "pass");
        await _node.Session.UnlockAsync("pass");
        await _node.ArticleService.CreateAsync("Test Article", "/Root", new List<string>(), "content");

        _positions = new SyncPositionRepository(_node.Factory);
        _client = NewClient(Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncClient>.Instance);

        _peerId = Guid.NewGuid();
        _handler = new MockHandler();
        _http = new HttpClient(_handler) { BaseAddress = new Uri("http://remote.local") };
        _handler.MapRoute("/api/sync/sentinel", _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        _handler.MapRoute("/api/sync/challenge", _ => Json(new { challenge = Convert.ToBase64String(new byte[32]), serverNodeId = _peerId }));
        _handler.MapRoute("/api/sync/authenticate", _ => Json(new { token = "test-token" }));
        _handler.MapRoute("/api/sync/report-position", req =>
        {
            _reported.Add(System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query)["sequence"] ?? "");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });
        _handler.MapRoute("/api/sync/blobs/check", req =>
        {
            var hashes = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                .RootElement.GetProperty("hashes").EnumerateArray().Select(h => h.GetString()!).ToList();
            return Json(new { missing = hashes });
        });
        _handler.MapRoute("/api/sync/blobs", _ => Json(new { stored = 1, rejected = 0 }));
        MapIdentity();
        MapGone(after => after < Checkpoint ? GoneBody(Checkpoint, Head) : null);

        // The join: the host's checkpoint is this node's position for the host, and nothing else.
        await _positions.UpsertAsync(new SyncPosition { RemoteNodeId = _hostId, LastSequenceNum = 345, UpdatedAt = DateTime.UtcNow });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _node.DisposeAsync();
    }

    private SyncClient NewClient(ILogger<SyncClient> log) =>
        new(_node.NodeRepo, _node.EventLogRepo, _positions, new SyncPushPositionRepository(_node.Factory),
            _node.EventApplier, new SessionNodeAuthSigner(_node.Session), log, new PeerNewerProtocolState(),
            _node.QuarantineRepo, new BlobRepository(_node.Factory), blindState: new BlindState(_node.Factory),
            sentinelVerifier: new RemoteSentinelVerifier(_node.Session,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<RemoteSentinelVerifier>.Instance));

    private static HttpResponseMessage Json(object body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private void MapIdentity() =>
        _handler.MapRoute("/api/sync/identity", _ => Json(new
        {
            nodeId = _peerId, displayName = "Server", ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]),
            protocolVersion = SyncProtocolVersion.Current
        }));

    /// <summary>The peer's pull: the response <paramref name="goneFor"/> gives for afterSequence (null = an empty page).</summary>
    private void MapGone(Func<long, HttpResponseMessage?> goneFor) =>
        _handler.MapRoute("/api/sync/events", req =>
        {
            if (req.Method == HttpMethod.Post)
                return Json(new { applied = 1, skipped = 0, lastAppliedSequence = 1, dropped = 0 });
            var after = long.Parse(System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query)["afterSequence"] ?? "0");
            _pulledAfter.Add(after);
            return goneFor(after) ?? Json(Array.Empty<object>());
        });

    /// <summary>A 410 as SyncEndpoints writes it; a null member is left out, as a peer that does not send it would.</summary>
    private static HttpResponseMessage GoneBody(long cp, long? head, string? error = "SEQUENCE_TOO_OLD")
    {
        var body = new Dictionary<string, object?> { ["last_compaction_cp"] = cp, ["message"] = "too old" };
        if (head is { } h) body["current_head_seq"] = h;
        if (error is not null) body["error"] = error;
        return Json(body, System.Net.HttpStatusCode.Gone);
    }

    /// <summary>What a reverse proxy with <c>ProxyErrorOverride On</c> (Apache) sends instead of the node's 410 body.</summary>
    private static HttpResponseMessage ProxyErrorPage() =>
        new(System.Net.HttpStatusCode.Gone)
        {
            Content = new StringContent("<!DOCTYPE HTML PUBLIC \"-//IETF//DTD HTML 2.0//EN\"><html><head><title>410 Gone</title></head></html>",
                Encoding.UTF8, "text/html")
        };

    private async Task<long?> PositionAsync() => (await _positions.GetAsync(_peerId))?.LastSequenceNum;

    [Fact]
    public async Task AFullPeerThisJoinedNodeNeverPulledFrom_IsPulledFromItsCheckpoint_NotFromZeroForEver()
    {
        await _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        _pulledAfter.Should().Equal([0L, Checkpoint], "one pull from nothing, refused; one from the peer's own checkpoint");
        (await PositionAsync()).Should().Be(Checkpoint);
        _reported.Should().Equal([Checkpoint.ToString()], "the peer learns where this node stands, so its next compaction is not held up by a node that never synced");
        _handler.CallLog.Should().Contain("POST /api/sync/events", "the push the 410 used to cut off happens now");
    }

    [Fact]
    public async Task APhonesPlainSync_TakesTheCheckpointToo()
    {
        // The phone apps join from a snapshot the same way and call SyncWithAsync for each peer.
        await _client.SyncWithAsync(_http, "http://remote.local", _peerId);

        (await PositionAsync()).Should().Be(Checkpoint);
    }

    [Fact]
    public async Task ASecond410_AfterTheAdoption_IsARefusal_NotChased()
    {
        var cp = Checkpoint;
        MapGone(after => after < (cp += 10) ? GoneBody(cp, Head) : null);   // the checkpoint moves above whatever is adopted

        var sync = () => _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        await sync.Should().ThrowAsync<SnapshotRequiredException>();
        (await PositionAsync()).Should().Be(Checkpoint + 10, "adopted once");
        _pulledAfter.Should().HaveCount(2);
        (await sync.Should().ThrowAsync<SnapshotRequiredException>()).Which.LastCompactionCp.Should().BeGreaterThan(Checkpoint + 10);
        (await PositionAsync()).Should().Be(Checkpoint + 10, "a cursor that exists is never moved by a 410");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(100L)]
    public async Task APeerThisNodeHoldsAPositionFor_IsNeverAdvancedByA410(long stored)
    {
        await _positions.UpsertAsync(new SyncPosition { RemoteNodeId = _peerId, LastSequenceNum = stored, UpdatedAt = DateTime.UtcNow });

        var sync = () => _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        await sync.Should().ThrowAsync<SnapshotRequiredException>("this node really fell behind what the peer kept");
        (await PositionAsync()).Should().Be(stored);
        _pulledAfter.Should().HaveCount(1);
    }

    [Fact]
    public async Task ANodeWithNoOtherFullPeerToStandOn_StillGetsTheRefusal()
    {
        // A recovery restore keeps a position for the blind node it came from and none for any full peer: what the
        // compacted peer removed may never have reached it, so a refusal stays one.
        using (var conn = _node.Factory.CreateConnection())
            await Dapper.SqlMapper.ExecuteAsync(conn, "DELETE FROM tbl_sync_position");
        await _positions.UpsertAsync(new SyncPosition { RemoteNodeId = BlindNodeId.NewId(), LastSequenceNum = 50, UpdatedAt = DateTime.UtcNow });

        var sync = () => _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        await sync.Should().ThrowAsync<SnapshotRequiredException>();
        (await PositionAsync()).Should().BeNull();
        _pulledAfter.Should().HaveCount(1);
    }

    [Fact]
    public async Task A410WhoseBodyAProxyReplaced_IsNotAdopted()
    {
        MapGone(after => after < Checkpoint ? ProxyErrorPage() : null);

        var sync = () => _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        var thrown = (await sync.Should().ThrowAsync<SnapshotRequiredException>()).Which;
        thrown.LastCompactionCp.Should().Be(0, "nothing could be read from the page");
        (await PositionAsync()).Should().BeNull("a checkpoint nobody stated is not one to start from");
    }

    [Theory]
    [InlineData("SOMETHING_ELSE", Checkpoint, Head)]
    [InlineData(null, Checkpoint, Head)]
    [InlineData("SEQUENCE_TOO_OLD", 0L, Head)]
    [InlineData("SEQUENCE_TOO_OLD", Head + 1, Head)]
    [InlineData("SEQUENCE_TOO_OLD", Checkpoint, 0L)]
    public async Task A410ThatDoesNotStateACheckpointInsideItsLog_IsNotAdopted(string? code, long cp, long head)
    {
        MapGone(after => after < Math.Max(cp, 1) ? GoneBody(cp, head, code) : null);

        var sync = () => _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        await sync.Should().ThrowAsync<SnapshotRequiredException>();
        (await PositionAsync()).Should().BeNull();
    }

    [Fact]
    public async Task A410ThatReportsNoHead_IsNotAdopted()
    {
        MapGone(after => after < Checkpoint ? GoneBody(Checkpoint, head: null) : null);

        var sync = () => _client.SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        await sync.Should().ThrowAsync<SnapshotRequiredException>();
        (await PositionAsync()).Should().BeNull();
    }

    [Fact]
    public async Task TheAdoption_IsLoggedWithTheNumbers()
    {
        var log = new ListLogger<SyncClient>();

        await NewClient(log).SyncWithPeerAsync(_http, "http://remote.local", _peerId);

        log.Lines.Should().Contain(l => l.Contains(_peerId.ToString()) && l.Contains($"checkpoint {Checkpoint}") && l.Contains("never pulled"));
    }

    // ---------------------------------------------------------------------------- the scheduler's cycle

    [Fact]
    public async Task TheSchedulersCycle_RaisesNoWipeNotice_AndClearsOneThatWasUp()
    {
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = _peerId, DisplayName = "Server", Ed25519PublicKey = new byte[32], ApiAddress = "http://remote.local",
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var services = new ServiceCollection()
            .AddSingleton(new InvisibleModeService())
            .AddSingleton(_node.WhitelistRepo)
            .AddSingleton(_client)
            .BuildServiceProvider();
        var log = new CapturingLogger<SyncScheduler>();
        var state = new SnapshotRequiredState();
        // What the build without the fix left behind on the node: the notice from the cycles before.
        state.Set(new SnapshotRequiredException("http://remote.local", 0, 0, "too old"), _peerId);
        var scheduler = new SyncScheduler(services.GetRequiredService<IServiceScopeFactory>(), log, new SyncTrigger(),
            new FixedHttpClientFactory(_handler), snapshotRequiredState: state);

        await scheduler.SyncAllAsync(CancellationToken.None);

        (await PositionAsync()).Should().Be(Checkpoint);
        log.Entries.Should().NotContain(e => e.Level == LogLevel.Critical);
        state.LastException.Should().BeNull("the peer answered and this node syncs with it now");
    }

    private sealed class Fixture : SyncTestFixture { }

    private sealed class FixedHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception) + (exception is null ? "" : " [" + exception + "]")));
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<string> _lines = new();
        public List<string> Lines { get { lock (_lines) return _lines.ToList(); } }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add(formatter(state, exception));
        }
    }

    private sealed class MockHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new();
        public List<string> CallLog { get; } = [];

        public void MapRoute(string path, Func<HttpRequestMessage, HttpResponseMessage> handler) => _routes[path] = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("No URI");
            lock (CallLog) CallLog.Add($"{request.Method} {uri.AbsolutePath}");
            foreach (var (path, handler) in _routes)
                if (uri.AbsolutePath.EndsWith(path, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(handler(request));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
