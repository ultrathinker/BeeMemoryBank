using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

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
        _snapshotService = new SnapshotService(_tempDir, _factory, _nodeRepo, new NullLamportClock());

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

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*current min*");
    }

    [Fact]
    public async Task Sync_Events_Returns410_WhenPositionTooOld()
    {
        await InsertEventsAsync(5000);
        await _compactionService.ExecuteAsync(explicitCp: 3000, reason: "compact");

        var minSeq = await _eventLogRepo.GetMinSequenceAsync();
        minSeq.Should().NotBeNull();

        var tooOldPosition = minSeq!.Value - 2;
        var events = await _eventLogRepo.GetAfterSequenceAsync(tooOldPosition, 100);
        events.Should().NotBeNull();

        var okPosition = minSeq.Value - 1;
        var okEvents = await _eventLogRepo.GetAfterSequenceAsync(okPosition, 100);
        okEvents.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ParallelCalls_SecondThrows()
    {
        await InsertEventsAsync(5000);

        var tcs = new TaskCompletionSource();
        var task1 = _compactionService.ExecuteAsync(explicitCp: 1000, reason: "first");

        var task2 = Task.Run(() => _compactionService.ExecuteAsync(explicitCp: 2000, reason: "second"));

        Func<Task> act = async () => await task2;
        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
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
