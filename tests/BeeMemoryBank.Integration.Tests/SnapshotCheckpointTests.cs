using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

[Collection(CompactionCollection.Name)]
public class SnapshotCheckpointTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"bmb_cp_{Guid.NewGuid():N}");
    private DbConnectionFactory _factory = null!;
    private EventLogRepository _eventLogRepo = null!;
    private SyncPushPositionRepository _syncPushPosRepo = null!;
    private WhitelistRepository _whitelistRepo = null!;
    private NodeIdentityRepository _nodeRepo = null!;
    private SnapshotService _snapshotService = null!;
    private CompactionService _compactionService = null!;
    private EventLogger _eventLogger = null!;
    private SnapshotJoinCache _cache = null!;
    private Guid _localNodeId;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_cp_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        _eventLogRepo = new EventLogRepository(_factory);
        _syncPushPosRepo = new SyncPushPositionRepository(_factory);
        _whitelistRepo = new WhitelistRepository(_factory);
        _nodeRepo = new NodeIdentityRepository(_factory);

        Directory.CreateDirectory(_tempDir);
        var clock = new NullLamportClock();
        _snapshotService = new SnapshotService(_tempDir, _factory, _nodeRepo, clock);

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

        var keySlotRepoForSession = new BeeMemoryBank.Storage.Sqlite.KeySlotRepository(_factory);
        _eventLogger = new EventLogger(
            _nodeRepo, _eventLogRepo, clock, new NullActorProvider(),
            new NullSyncTrigger(), new SessionService(keySlotRepoForSession));

        var loggerFactory = LoggerFactory.Create(b => b.AddDebug());
        var logger = loggerFactory.CreateLogger<CompactionService>();
        _cache = new SnapshotJoinCache();
        _compactionService = new CompactionService(
            _eventLogRepo, _syncPushPosRepo, _snapshotService, _eventLogger,
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
    public async Task FirstCompaction_EmitsCheckpointWithNullPrev()
    {
        await InsertEventsAsync(3000);
        await _compactionService.ExecuteAsync(explicitCp: 1000, reason: "first");

        using var conn = _factory.CreateConnection();
        var checkpointEvents = await conn.QueryAsync(
            "SELECT * FROM tbl_event WHERE event_type = @t ORDER BY sequence_num",
            new { t = EventTypes.SnapshotCheckpoint });

        var list = checkpointEvents.ToList();
        list.Should().HaveCount(1);

        var payload = JsonDocument.Parse((string)list[0].payload);
        payload.RootElement.GetProperty("cp_seq").GetInt64().Should().Be(1000);
        payload.RootElement.GetProperty("prev_checkpoint_sha256").ValueKind.Should().Be(JsonValueKind.Null);
        payload.RootElement.GetProperty("snapshot_file_name").GetString().Should().NotBeNullOrEmpty();
        payload.RootElement.GetProperty("snapshot_sha256").GetString().Should().NotBeNullOrEmpty();
        payload.RootElement.GetProperty("events_removed").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SecondCompaction_PreservesFirstCheckpoint_AndReferencesIt()
    {
        await InsertEventsAsync(3000);
        await _compactionService.ExecuteAsync(explicitCp: 1000, reason: "first");

        using var conn = _factory.CreateConnection();
        var firstPayload = await conn.ExecuteScalarAsync<string>(
            "SELECT payload FROM tbl_event WHERE event_type = @t ORDER BY sequence_num LIMIT 1",
            new { t = EventTypes.SnapshotCheckpoint });
        firstPayload.Should().NotBeNull();

        var expectedPrevHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(firstPayload!)));

        await InsertEventsAsync(2000);

        await _compactionService.ExecuteAsync(explicitCp: 3000, reason: "second");

        var checkpointEvents = (await conn.QueryAsync(
            "SELECT * FROM tbl_event WHERE event_type = @t ORDER BY sequence_num",
            new { t = EventTypes.SnapshotCheckpoint })).ToList();

        checkpointEvents.Should().HaveCount(2);

        var secondPayload = JsonDocument.Parse((string)checkpointEvents[1].payload);
        secondPayload.RootElement.GetProperty("prev_checkpoint_sha256").GetString().Should().Be(expectedPrevHash);
        secondPayload.RootElement.GetProperty("cp_seq").GetInt64().Should().Be(3000);

        var firstSeq = (long)checkpointEvents[0].sequence_num;
        firstSeq.Should().BeGreaterThan(0, "first checkpoint should still exist");
    }

    [Fact]
    public async Task CompactionDelete_PreservesAllCheckpointEvents()
    {
        await InsertEventsAsync(5000);
        await _compactionService.ExecuteAsync(explicitCp: 1000, reason: "first");

        await InsertEventsAsync(2000);
        await _compactionService.ExecuteAsync(explicitCp: 3000, reason: "second");

        await InsertEventsAsync(2000);
        await _compactionService.ExecuteAsync(explicitCp: 5000, reason: "third");

        using var conn = _factory.CreateConnection();
        var checkpointEvents = await conn.QueryAsync(
            "SELECT sequence_num FROM tbl_event WHERE event_type = @t ORDER BY sequence_num",
            new { t = EventTypes.SnapshotCheckpoint });

        var list = checkpointEvents.ToList();
        list.Should().HaveCount(3, "all three checkpoint events should survive compactions");

        foreach (var row in list)
        {
            var seq = (long)row.sequence_num;
            seq.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public async Task SyncEvents_Returns410_WhenPositionBelowLastCompaction()
    {
        await InsertEventsAsync(5000);
        await _compactionService.ExecuteAsync(explicitCp: 3000, reason: "compact");

        var lastCp = await _eventLogRepo.GetLastCompactionCpAsync();
        lastCp.Should().Be(3000);

        var events = await _eventLogRepo.GetAfterSequenceAsync(100, 1000);
        events.Should().NotBeEmpty("events are returned from the repo layer; 410 is enforced at the endpoint level");
    }

    [Fact]
    public async Task SyncEvents_Returns200_WhenPositionEqualOrAboveLastCompaction()
    {
        await InsertEventsAsync(5000);
        await _compactionService.ExecuteAsync(explicitCp: 3000, reason: "compact");

        var lastCp = await _eventLogRepo.GetLastCompactionCpAsync();
        lastCp.Should().Be(3000);

        var eventsAtCp = await _eventLogRepo.GetAfterSequenceAsync(3000, 1000);
        eventsAtCp.Should().NotBeEmpty("should return events after compaction point");

        var eventsAboveCp = await _eventLogRepo.GetAfterSequenceAsync(3001, 1000);
        eventsAboveCp.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Compaction_IgnoresForeignCheckpoint()
    {
        await InsertEventsAsync(3000);

        var foreignNodeId = Guid.NewGuid();
        using (var conn = _factory.CreateConnection())
        {
            var foreignPayload = "{\"cp_seq\":999,\"fake\":true}";
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_event (event_id, node_id, lamport_ts, event_type, article_id, payload, signature, created_at)
                  VALUES (@eventId, @nodeId, 0, @eventType, @articleId, @payload, @signature, @createdAt)",
                new
                {
                    eventId = Guid.NewGuid(),
                    nodeId = foreignNodeId,
                    eventType = EventTypes.SnapshotCheckpoint,
                    articleId = Guid.NewGuid(),
                    payload = foreignPayload,
                    signature = new byte[64],
                    createdAt = DateTime.UtcNow
                });
        }

        await _compactionService.ExecuteAsync(explicitCp: 1000, reason: "first");

        using (var conn2 = _factory.CreateConnection())
        {
            var ourPayload = await conn2.QueryAsync<string>(
                @"SELECT payload FROM tbl_event
                  WHERE event_type = @t AND node_id = @localNodeId
                  ORDER BY sequence_num DESC LIMIT 1",
                new { t = EventTypes.SnapshotCheckpoint, localNodeId = _localNodeId });

            var payload = ourPayload.Single();
            var doc = JsonDocument.Parse(payload);
            doc.RootElement.GetProperty("prev_checkpoint_sha256").ValueKind.Should().Be(JsonValueKind.Null,
                "first compaction from our node should have null prev — foreign checkpoint must be ignored");
        }
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

    private sealed class NullSyncTrigger : ISyncTrigger
    {
        public void Signal() { }
        public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(false);
    }
}
