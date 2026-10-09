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
/// The "cannot catch up" state belongs to the peer that compacted past this node: another peer answering fine in the same
/// or a later cycle says nothing about it, and only that peer's own success (or its removal) ends it.
/// </summary>
public class SchedulerSnapshotRequiredPerPeerTests : IAsyncLifetime
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

    private static HttpResponseMessage Json(object body, System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    /// <summary>One fake peer per host name; <paramref name="stuck"/> answers the pull with a 410 that is not a blind peer's.</summary>
    private sealed class PeerHandler(Dictionary<string, (Guid Id, bool Stuck)> peers) : HttpMessageHandler
    {
        public bool StuckPeersAnswerFine { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var (id, stuckByDefault) = peers[uri.Host];
            var stuck = stuckByDefault && !StuckPeersAnswerFine;
            var path = uri.AbsolutePath;
            HttpResponseMessage r =
                path.EndsWith("/api/sync/sentinel") ? new(System.Net.HttpStatusCode.NotFound)
                : path.EndsWith("/api/sync/identity") ? Json(new { nodeId = id, displayName = uri.Host, ed25519PublicKeyB64 = Convert.ToBase64String(new byte[32]), protocolVersion = SyncProtocolVersion.Current })
                : path.EndsWith("/api/sync/challenge") ? Json(new { challenge = Convert.ToBase64String(new byte[32]), serverNodeId = id })
                : path.EndsWith("/api/sync/authenticate") ? Json(new { token = "t" })
                : path.EndsWith("/api/sync/report-position") ? new(System.Net.HttpStatusCode.OK)
                : path.EndsWith("/api/sync/events") && request.Method == HttpMethod.Get
                    ? (stuck ? Json(new { error = "SEQUENCE_TOO_OLD", last_compaction_cp = 500, current_head_seq = 900, message = "wipe" }, System.Net.HttpStatusCode.Gone)
                             : Json(Array.Empty<object>()))
                : path.EndsWith("/api/sync/events") ? Json(new { applied = 0, skipped = 0, lastAppliedSequence = (long?)null, dropped = 0 })
                : new(System.Net.HttpStatusCode.NotFound);
            return Task.FromResult(r);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private async Task<(SyncScheduler Scheduler, SnapshotRequiredState State, PeerHandler Handler, Guid StuckId)> TwoPeersAsync(
        string stuckName, string fineName, SnapshotRequiredState? state = null)
    {
        var stuckId = Guid.NewGuid();
        var fineId = Guid.NewGuid();
        foreach (var (name, id) in new[] { (stuckName, stuckId), (fineName, fineId) })
            await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
            {
                NodeId = id, DisplayName = name, Ed25519PublicKey = new byte[32], ApiAddress = $"http://{name}.local",
                Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        var handler = new PeerHandler(new Dictionary<string, (Guid, bool)>
        {
            [$"{stuckName}.local"] = (stuckId, true),
            [$"{fineName}.local"] = (fineId, false)
        });
        var services = new ServiceCollection()
            .AddSingleton(new InvisibleModeService()).AddSingleton(_node.WhitelistRepo).AddSingleton(_client).BuildServiceProvider();
        state ??= new SnapshotRequiredState();
        var scheduler = new SyncScheduler(services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncScheduler>.Instance, new SyncTrigger(),
            new Factory(handler), snapshotRequiredState: state);
        return (scheduler, state, handler, stuckId);
    }

    [Theory]
    [InlineData("a-stuck", "b-fine")] // the stuck peer sorts first: a healthy peer visited after it must not clear the flag
    [InlineData("b-stuck", "a-fine")] // the stuck peer sorts last: the flag stays
    public async Task AFullPeerWeCannotCatchUpWith_StaysReported_EvenWhenAnotherPeerAnswers(string stuckName, string fineName)
    {
        var (scheduler, state, _, _) = await TwoPeersAsync(stuckName, fineName);

        await scheduler.SyncAllAsync(CancellationToken.None);
        state.IsRequired.Should().BeTrue("one of the two peers has compacted past this node; the other answering says nothing about that");

        await scheduler.SyncAllAsync(CancellationToken.None);
        state.IsRequired.Should().BeTrue("still so in the next cycle");
    }

    /// <summary>
    /// The row <c>bmb status</c> reads from another process: peer B answering must not remove what names the stuck peer A. Uses only the
    /// surface the state had before the per-peer change (the constructor, <c>Read</c>, <c>IsRequired</c>), so with that change reverted it fails
    /// by assertion, not by compilation.
    /// </summary>
    [Fact]
    public async Task TheRowOfAStuckPeer_IsNotRemovedByTheSuccessOfAnotherPeer()
    {
        var (scheduler, state, handler, _) = await TwoPeersAsync("a-stuck", "b-fine", new SnapshotRequiredState(_node.Factory));

        await scheduler.SyncAllAsync(CancellationToken.None);

        state.IsRequired.Should().BeTrue("peer A answered 410 and peer B answered fine in the same cycle");
        SnapshotRequiredState.Read(_node.Factory).Should().NotBeNull("the row that bmb status reads still names the stuck peer");

        await scheduler.SyncAllAsync(CancellationToken.None);
        SnapshotRequiredState.Read(_node.Factory).Should().NotBeNull("still so in the next cycle");

        handler.StuckPeersAnswerFine = true;
        await scheduler.SyncAllAsync(CancellationToken.None);
        state.IsRequired.Should().BeFalse();
        SnapshotRequiredState.Read(_node.Factory).Should().BeNull("the stuck peer itself answering ends it");
    }

    [Fact]
    public async Task TheStateEnds_WhenTheStuckPeerItselfAnswersAgain()
    {
        var (scheduler, state, handler, _) = await TwoPeersAsync("a-stuck", "b-fine");
        await scheduler.SyncAllAsync(CancellationToken.None);
        state.IsRequired.Should().BeTrue();

        handler.StuckPeersAnswerFine = true; // e.g. the peer was restored, or its position was reset by a rejoin
        await scheduler.SyncAllAsync(CancellationToken.None);

        state.IsRequired.Should().BeFalse();
    }

    [Fact]
    public async Task TheStateEnds_WhenTheStuckPeerIsRemovedFromTheWhitelist()
    {
        var (scheduler, state, _, stuckId) = await TwoPeersAsync("a-stuck", "b-fine");
        await scheduler.SyncAllAsync(CancellationToken.None);
        state.IsRequired.Should().BeTrue();

        using (var conn = _node.Factory.CreateConnection())
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE tbl_whitelist SET status = 'R' WHERE node_id = @id COLLATE NOCASE", new { id = stuckId.ToString() });
        await scheduler.SyncAllAsync(CancellationToken.None);

        state.IsRequired.Should().BeFalse("a node this one no longer syncs with cannot be what it is stuck on");
    }
}
