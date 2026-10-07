using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

[Collection(HeavyOperationCollection.Name)]
public class CompactionServiceTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"bmb_compact_{Guid.NewGuid():N}");
    private DbConnectionFactory _factory = null!;
    private EventLogRepository _eventLogRepo = null!;
    private SyncPushPositionRepository _syncPushPosRepo = null!;
    private WhitelistRepository _whitelistRepo = null!;
    private NodeIdentityRepository _nodeRepo = null!;
    private SnapshotService _snapshotService = null!;
    private CompactionService _compactionService = null!;
    private SnapshotJoinCache _cache = null!;
    private Guid _localNodeId;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_compact_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        _eventLogRepo = new EventLogRepository(_factory);
        _syncPushPosRepo = new SyncPushPositionRepository(_factory);
        _whitelistRepo = new WhitelistRepository(_factory);
        _nodeRepo = new NodeIdentityRepository(_factory);

        Directory.CreateDirectory(_tempDir);
        _snapshotService = new SnapshotService(_tempDir, _factory, _nodeRepo, new NullLamportClock(), keys: new SessionSnapshotKeyOperations(null));

        var (pubKey, privKey) = Ed25519Signer.GenerateKeyPair();
        _localNodeId = Guid.NewGuid();
        await _nodeRepo.CreateAsync(new NodeIdentity
        {
            NodeId = _localNodeId,
            DisplayName = "TestNode",
            Ed25519PublicKey = pubKey,
            Ed25519PrivateKey = privKey,
            CreatedAt = DateTime.UtcNow
        });

        var loggerFactory = LoggerFactory.Create(b => b.AddDebug());
        var logger = loggerFactory.CreateLogger<CompactionService>();
        _cache = new SnapshotJoinCache();
        _compactionService = new CompactionService(
            _eventLogRepo, _syncPushPosRepo, _snapshotService, new NullEventLogger(),
            _nodeRepo, _cache, _factory, logger);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PreviewAsync_EmptyLog_ReturnsCanCompactFalse()
    {
        var preview = await _compactionService.PreviewAsync();

        preview.CanCompact.Should().BeFalse();
        preview.HeadSeq.Should().Be(0);
        preview.MinSeq.Should().Be(0);
        preview.TotalEvents.Should().Be(0);
        preview.Reason.Should().Be("Event log is empty");
    }

    [Fact]
    public async Task PreviewAsync_LoneNode_ProposesHeadMinusMargin()
    {
        // Insert > TARGET_KEEP_COUNT (1500) events so compaction has something to delete.
        await InsertEventsAsync(2000);

        var preview = await _compactionService.PreviewAsync();

        preview.CanCompact.Should().BeTrue();
        preview.HeadSeq.Should().Be(2000);
        preview.ActivePeerCount.Should().Be(0);
        preview.ProposedCp.Should().Be(Math.Max(0, 2000 - 1500));
    }

    [Fact]
    public async Task PreviewAsync_WithPeers_ProposesMinPeerPositionMinusMargin()
    {
        await InsertEventsAsync(5000);

        var peerId1 = Guid.NewGuid();
        var peerId2 = Guid.NewGuid();
        await InsertWhitelistPeerAsync(peerId1, "Peer1");
        await InsertWhitelistPeerAsync(peerId2, "Peer2");
        // Both peers within last 1500 events of head=5000. Peer at 3500 is exactly at the
        // boundary (peerBehind = 1500); we use 3700 / 4000 to stay safely inside.
        await _syncPushPosRepo.UpsertAsync(new SyncPushPosition
        {
            RemoteNodeId = peerId1,
            LastPushedSeq = 3700,
            PushedAt = DateTime.UtcNow
        });
        await _syncPushPosRepo.UpsertAsync(new SyncPushPosition
        {
            RemoteNodeId = peerId2,
            LastPushedSeq = 4000,
            PushedAt = DateTime.UtcNow
        });

        var preview = await _compactionService.PreviewAsync();

        preview.CanCompact.Should().BeTrue();
        preview.ActivePeerCount.Should().Be(2);
        // Compaction proposed by count-based formula: delete (totalEvents - TARGET_KEEP_COUNT)
        // events. With 5000 events / 1500 keep-count, 3500 events are eligible to delete; the
        // 3500th-oldest event is at sequence 3500.
        preview.ProposedCp.Should().Be(3500);
        preview.PeerPositions.Should().HaveCount(2);
    }

    [Fact]
    public async Task PreviewAsync_WithStalePeer_AddsWarning()
    {
        await InsertEventsAsync(5000);

        var peerId = Guid.NewGuid();
        await InsertWhitelistPeerAsync(peerId, "StalePeer");
        await _syncPushPosRepo.UpsertAsync(new SyncPushPosition
        {
            RemoteNodeId = peerId,
            LastPushedSeq = 3000,
            PushedAt = DateTime.UtcNow.AddDays(-20)
        });

        var preview = await _compactionService.PreviewAsync();

        preview.Warnings.Should().Contain(w => w.Contains("days ago"));
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_DeletesOldEventsAndCreatesSnapshot()
    {
        await InsertEventsAsync(5000);

        var result = await _compactionService.ExecuteAsync(explicitCp: 2000, reason: "test");

        result.CpAfter.Should().Be(2000);
        result.EventsDeleted.Should().Be(2000);
        result.SnapshotFileName.Should().NotBeNullOrEmpty();

        var remaining = await _eventLogRepo.GetTotalCountAsync();
        remaining.Should().Be(3000);

        var minSeq = await _eventLogRepo.GetMinSequenceAsync();
        minSeq.Should().BeGreaterOrEqualTo(2001);

        using var conn = _factory.CreateConnection();
        var logEntries = await conn.QueryAsync(
            "SELECT * FROM tbl_compaction_log ORDER BY id DESC LIMIT 1");
        var entry = logEntries.Single();
        ((long)entry.events_removed).Should().Be(2000);
        ((string)entry.reason).Should().Be("test");
    }

    [Fact]
    public async Task ExecuteAsync_ProposedCpBelowCurrentMin_Throws()
    {
        await InsertEventsAsync(5000);
        await _compactionService.ExecuteAsync(explicitCp: 2000, reason: "first pass");

        var act = () => _compactionService.ExecuteAsync(explicitCp: 2000, reason: "should fail");

        // ArgumentException, so the endpoint answers 400: the caller named a checkpoint that is
        // out of range. Asserted by type, not just by message, because the type is what decides
        // the status code — and it must stay distinct from the ConflictException below, which is
        // the retryable 409.
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*current min*");
    }

    /// <summary>
    /// The server's 410 branch, over HTTP (it used to be "tested" by two repository reads that asserted NotBeNull): a peer
    /// asking a compacted host for events below the checkpoint gets 410 SEQUENCE_TOO_OLD with the checkpoint and head, and
    /// a peer at the checkpoint gets its events. This 410 is what left a <c>bmb join</c>-ed node empty (BMB-81).
    /// </summary>
    [Fact]
    public async Task Sync_Events_Returns410_WhenPositionTooOld()
    {
        const string password = "compaction410Password";
        using var host = new BmbWebApplicationFactory();
        await host.InitializeNodeAsync("Host", password);
        using var admin = host.CreateClient();
        (await admin.PostAsJsonAsync("/api/session/unlock", new { Password = password })).EnsureSuccessStatusCode();
        for (var i = 0; i < 12; i++)
            (await admin.PostAsJsonAsync("/api/articles", new { title = $"Note {i}", treePath = "/", content = "body" }))
                .EnsureSuccessStatusCode();

        // A whitelisted peer that authenticates the way a node does: no internal key, its own Ed25519 key. (Written to the
        // whitelist directly rather than through /api/join, whose rate limit is process-wide.)
        using var peer = new HttpClient(host.Server.CreateHandler()) { BaseAddress = new Uri("http://localhost") };
        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        var peerId = Guid.NewGuid();
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = peerId, DisplayName = "Peer", Ed25519PublicKey = publicKey, Status = "A",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        var token = await AuthenticateAsync(peer, peerId, privateKey);

        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CompactionService>()
                .ExecuteAsync(explicitCp: 8, reason: "410 test", acceptCuttingOffPeers: true);

        using var tooOld = await GetEventsAsync(peer, token, afterSequence: 0);
        tooOld.StatusCode.Should().Be(HttpStatusCode.Gone);
        var body = await tooOld.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("SEQUENCE_TOO_OLD");
        body.GetProperty("last_compaction_cp").GetInt64().Should().Be(8);
        body.GetProperty("current_head_seq").GetInt64().Should().BeGreaterThan(8);

        using var atCheckpoint = await GetEventsAsync(peer, token, afterSequence: 8);
        atCheckpoint.StatusCode.Should().Be(HttpStatusCode.OK, "a peer at the checkpoint continues from there");
    }

    private static async Task<HttpResponseMessage> GetEventsAsync(HttpClient http, string token, long afterSequence)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/sync/events?afterSequence={afterSequence}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request);
    }

    private static async Task<string> AuthenticateAsync(HttpClient http, Guid nodeId, byte[] privateKey)
    {
        var challenge = await (await http.PostAsync("/api/sync/challenge", null)).Content.ReadFromJsonAsync<JsonElement>();
        var challengeB64 = challenge.GetProperty("challenge").GetString()!;
        var payload = "BMB-CHALLENGE-V2\0"u8.ToArray()
            .Concat(challenge.GetProperty("serverNodeId").GetGuid().ToByteArray())
            .Concat(Convert.FromBase64String(challengeB64))
            .ToArray();
        var auth = await http.PostAsJsonAsync("/api/sync/authenticate", new
        {
            NodeId = nodeId,
            ChallengeB64 = challengeB64,
            SignatureB64 = Convert.ToBase64String(Ed25519Signer.Sign(privateKey, payload)),
            ProtocolVersion = BeeMemoryBank.Sync.SyncProtocolVersion.Current
        });
        auth.EnsureSuccessStatusCode();
        return (await auth.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task ExecuteAsync_WithExplicitCp_StillRefusesToStrandAPeer()
    {
        // The hole this closes: the peer-safety check lived only in PreviewAsync, and
        // ExecuteCoreAsync consulted it only through preview.ProposedCp. Passing an explicit
        // checkpoint therefore skipped it completely — and the explicit checkpoint is exactly what
        // an operator reaches for when the Compact button is greyed out, so the bypass was on the
        // likeliest path to the case the check exists for. Peers were stranded silently.
        await InsertEventsAsync(5000);

        var strandedPeer = Guid.NewGuid();
        await InsertWhitelistPeerAsync(strandedPeer, "PeerLeftBehind");
        await _syncPushPosRepo.UpsertAsync(new SyncPushPosition
        {
            RemoteNodeId = strandedPeer,
            LastPushedSeq = 100,          // 4900 events behind head — far past the 1500 keep-count
            PushedAt = DateTime.UtcNow
        });

        var act = () => _compactionService.ExecuteAsync(explicitCp: 2000, reason: "should refuse");

        // ConflictException, so the endpoint answers 409: the request is well-formed and the
        // caller can retry once the peer catches up or is revoked.
        var ex = await act.Should().ThrowAsync<ConflictException>();
        ex.WithMessage("*would be cut off*");
        ex.WithMessage($"*{strandedPeer}*", "the operator has to know WHICH machine is about to be stranded");
    }

    [Fact]
    public async Task ExecuteAsync_WithAcceptCuttingOffPeers_ProceedsAnyway()
    {
        // The deliberate exit from the deadlock. Without it one phone that has been off for a week
        // blocks every compaction forever, tbl_event grows without bound, and the operator's only
        // remedy is revoking a peer they still want.
        await InsertEventsAsync(5000);

        var strandedPeer = Guid.NewGuid();
        await InsertWhitelistPeerAsync(strandedPeer, "PeerLeftBehind");
        await _syncPushPosRepo.UpsertAsync(new SyncPushPosition
        {
            RemoteNodeId = strandedPeer,
            LastPushedSeq = 100,
            PushedAt = DateTime.UtcNow
        });

        var result = await _compactionService.ExecuteAsync(
            explicitCp: 2000, reason: "operator accepted", acceptCuttingOffPeers: true);

        result.EventsDeleted.Should().BeGreaterThan(0);

        // And the preview still says who was at risk, so the override is an informed one rather
        // than a flag that makes the problem invisible.
        var preview = await _compactionService.PreviewAsync();
        preview.AtRiskPeers.Should().NotBeNull();
        preview.AtRiskPeers!.Should().Contain(strandedPeer);
    }

    [Fact]
    public async Task ExecuteAsync_ParallelCalls_SecondThrows()
    {
        await InsertEventsAsync(5000);

        var tcs = new TaskCompletionSource();
        var task1 = _compactionService.ExecuteAsync(explicitCp: 1000, reason: "first");

        var task2 = Task.Run(() => _compactionService.ExecuteAsync(explicitCp: 2000, reason: "second"));

        Func<Task> act = async () => await task2;
        // ConflictException specifically: a collision with a running compaction is a 409 the
        // operator can retry, and this used to arrive as 400 because the endpoint flattened every
        // InvalidOperationException. ConflictException derives from it, so asserting the base
        // type here would pass even if the fix were reverted.
        var ex = await act.Should().ThrowAsync<ConflictException>();
        ex.WithMessage("*Another compaction is already in progress*");

        await task1;
    }

    [Fact]
    public async Task PreviewAsync_PeerNeverSynced_ShowsWarningAndUsesLoneFormula()
    {
        await InsertEventsAsync(5000);

        var syncedPeerId = Guid.NewGuid();
        var neverSyncedPeerId = Guid.NewGuid();
        await InsertWhitelistPeerAsync(syncedPeerId, "SyncedPeer");
        await InsertWhitelistPeerAsync(neverSyncedPeerId, "NeverSyncedPeer");

        await _syncPushPosRepo.UpsertAsync(new SyncPushPosition
        {
            RemoteNodeId = syncedPeerId,
            LastPushedSeq = 4500,
            PushedAt = DateTime.UtcNow
        });

        var preview = await _compactionService.PreviewAsync();

        preview.ActivePeerCount.Should().Be(2);
        preview.Warnings.Should().Contain(w => w.Contains(neverSyncedPeerId.ToString()) && w.Contains("never synced"));
        // Production refuses to compact when any peer is never-synced (would be cut off).
        // Test was originally written for a more permissive lone-formula fallback; current
        // safety-first behavior is correct.
        preview.CanCompact.Should().BeFalse();
        preview.PeerPositions.Should().Contain(p => p.NodeId == neverSyncedPeerId && p.LastSequenceNum == -1);
    }

    private async Task InsertEventsAsync(int count)
    {
        for (int i = 1; i <= count; i++)
        {
            await _eventLogRepo.AppendAsync(new SyncEvent
            {
                EventId = Guid.NewGuid(),
                NodeId = _localNodeId,
                LamportTs = i,
                EventType = "article_create",
                ArticleId = Guid.NewGuid(),
                Payload = "{}",
                Signature = new byte[64],
                CreatedAt = DateTime.UtcNow
            });
        }
    }

    private async Task InsertWhitelistPeerAsync(Guid nodeId, string displayName)
    {
        var (pubKey, _) = Ed25519Signer.GenerateKeyPair();
        await _whitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = nodeId,
            DisplayName = displayName,
            Ed25519PublicKey = pubKey,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
    }
}
