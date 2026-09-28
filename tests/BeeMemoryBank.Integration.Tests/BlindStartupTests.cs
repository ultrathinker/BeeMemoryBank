using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A blind node's first and later starts on a data volume in a given state (plan 3.4-3.5): what it
/// finishes, and what it refuses to run on.
/// </summary>
public class BlindStartupTests
{
    /// <summary>A first start that wrote the key and died before the identity row resumes from the key.</summary>
    [Fact]
    public async Task KeyFileWithoutIdentity_IsCompletedFromTheKey()
    {
        using var blind = new BlindNodeFactory();
        Directory.CreateDirectory(blind.DataPath);
        var key = new FileNodeKey(Path.Combine(blind.DataPath, FileNodeKey.FileName));
        var publicKey = key.Create();

        var start = () => blind.Services;

        start.Should().NotThrow();
        var identity = await blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync();
        identity.Should().NotBeNull();
        identity!.Ed25519PublicKey.Should().Equal(publicKey, "the row is completed from the key on disk, not a new one");
        BlindNodeId.IsBlind(identity.NodeId).Should().BeTrue();
    }

    /// <summary>
    /// A first start that died mid-write left a file that exists and holds nothing. Reading it as a
    /// key failed every later start (the v=2 identity row points at a key that is not there); the
    /// node now mints the identity it never finished writing. The write itself is atomic now, so
    /// this is the shape only older builds could leave behind — and volumes from them exist.
    /// </summary>
    [Fact]
    public async Task AKeyFileLeftEmptyByACrash_IsCompletedWithAFreshKey()
    {
        using var blind = new BlindNodeFactory();
        Directory.CreateDirectory(blind.DataPath);
        var keyPath = Path.Combine(blind.DataPath, FileNodeKey.FileName);
        await File.WriteAllBytesAsync(keyPath, []); // the torn write

        var start = () => blind.Services;

        start.Should().NotThrow("the node has no usable key on disk, so it is a first start");
        var key = new FileNodeKey(keyPath);
        key.ReadSeed().Should().HaveCount(32);
        var identity = await blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync();
        identity.Should().NotBeNull();
        key.Matches(identity!.Ed25519PublicKey).Should().BeTrue();
    }

    /// <summary>
    /// Review L-stage0 #1: a blind volume that holds anything able to put the master DEK into the
    /// process — here a password key slot, or the update unlock handoff — is refused at start.
    /// </summary>
    [Fact]
    public async Task AKeySlotOnTheVolume_IsRefusedAtStart()
    {
        using var blind = new BlindNodeFactory();
        using (var db = new DbConnectionFactory(blind.DataPath))
        {
            await new MigrationRunner(db).RunMigrationsAsync();
            await new KeySlotRepository(db).CreateAsync(new MasterKeyStore
            {
                EncryptedMasterDek = new byte[48], IV = new byte[12], Salt = new byte[16], CreatedAt = DateTime.UtcNow
            });
        }

        var start = () => blind.Services;

        start.Should().Throw<InvalidOperationException>().WithMessage("*key slots*");
    }

    [Fact]
    public void AnUpdateUnlockHandoff_IsRefusedAtStart()
    {
        using var blind = new BlindNodeFactory();
        Directory.CreateDirectory(blind.DataPath);
        File.WriteAllBytes(Path.Combine(blind.DataPath, "update-unlock.dat"), new byte[64]);

        var start = () => blind.Services;

        start.Should().Throw<InvalidOperationException>().WithMessage("*update-unlock.dat*");
    }

    /// <summary>
    /// Review L-stage0 #4: a restore_network recorded as Pending whose reseed flag was never written
    /// (the process died in between) is finished at the next start.
    /// </summary>
    [Fact]
    public async Task APendingRestoreWithoutItsFlag_IsFlaggedAtStart()
    {
        using var blind = new BlindNodeFactory();
        var key = new FileNodeKey(Path.Combine(blind.DataPath, FileNodeKey.FileName));
        using (var db = new DbConnectionFactory(blind.DataPath))
        {
            await new MigrationRunner(db).RunMigrationsAsync();
            await new NodeIdentityRepository(db).CreateAsync(new NodeIdentity
            {
                NodeId = BlindNodeId.NewId(), DisplayName = "Blind", Ed25519PublicKey = key.Create(),
                Ed25519PrivateKey = [], Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion, CreatedAt = DateTime.UtcNow
            });
            var now = DateTime.UtcNow.ToString("O");
            await new RestoreEventStateRepository(db).UpsertAsync(new RestoreEventStateRow(
                "lost-restore", RestoreEventState.Pending, null, false, null, null, now, now));
        }

        var flag = await blind.Services.GetRequiredService<BlindState>().GetReseedNeededAsync();

        flag.Should().Contain("lost-restore");
    }

    /// <summary>
    /// Review L-stage1 #4: a cutover the process died in, after the old database was moved aside and
    /// the new one moved in, is rolled back at the next start — the old database, whole, is live again.
    /// </summary>
    [Fact]
    public async Task ACutoverInterruptedMidSwitch_IsRolledBackAtStart()
    {
        using var blind = new BlindNodeFactory();
        var oldPeer = Guid.NewGuid();
        await StageInterruptedCutoverAsync(blind.DataPath, BlindSeedCutover.Switching, oldPeer, newPeer: Guid.NewGuid());

        var whitelist = blind.Services.GetRequiredService<IWhitelistRepository>();

        (await whitelist.GetByNodeIdAsync(oldPeer)).Should().NotBeNull("the old database is back in place");
        Directory.Exists(BlindSeedCutover.DirOf(blind.DataPath)).Should().BeFalse();
    }

    /// <summary>
    /// Review L-stage1 round 2 #3: the process died between moving the live media directory aside and
    /// moving the new one in. The start puts the old database AND the old media directory back.
    /// </summary>
    [Fact]
    public async Task ACutoverInterruptedBetweenTheMediaMoves_RestoresTheOldMedia()
    {
        using var blind = new BlindNodeFactory();
        var oldPeer = Guid.NewGuid();
        await StageInterruptedCutoverAsync(blind.DataPath, BlindSeedCutover.Switching, oldPeer, newPeer: Guid.NewGuid());
        var oldMedia = Path.Combine(BlindSeedCutover.DirOf(blind.DataPath), "old-media");
        Directory.CreateDirectory(oldMedia);
        await File.WriteAllTextAsync(Path.Combine(oldMedia, "kept.enc"), "old media");
        var liveMedia = Path.Combine(blind.DataPath, "media");
        if (Directory.Exists(liveMedia)) Directory.Delete(liveMedia, recursive: true);

        var whitelist = blind.Services.GetRequiredService<IWhitelistRepository>();

        File.Exists(Path.Combine(liveMedia, "kept.enc")).Should().BeTrue("the old media directory is back in place");
        (await whitelist.GetByNodeIdAsync(oldPeer)).Should().NotBeNull();
    }

    /// <summary>
    /// Review L-stage1 round 3 #2: a node that had no media directory died mid-switch, after the seed's
    /// media directory was put in place. The start restores the old database and removes the seed's
    /// media rather than leaving it live beside the old database.
    /// </summary>
    [Fact]
    public async Task ACutoverInterruptedOnANodeWithoutMedia_RemovesTheSeedsMedia()
    {
        using var blind = new BlindNodeFactory();
        var oldPeer = Guid.NewGuid();
        var liveMedia = Path.Combine(blind.DataPath, "media");
        if (Directory.Exists(liveMedia)) Directory.Delete(liveMedia, recursive: true);
        await StageInterruptedCutoverAsync(blind.DataPath, BlindSeedCutover.Switching, oldPeer, newPeer: Guid.NewGuid());
        Directory.CreateDirectory(liveMedia);
        await File.WriteAllTextAsync(Path.Combine(liveMedia, "seed.enc"), "the seed's media");

        var whitelist = blind.Services.GetRequiredService<IWhitelistRepository>();

        File.Exists(Path.Combine(liveMedia, "seed.enc")).Should().BeFalse("the seed's media went with the seed");
        (await whitelist.GetByNodeIdAsync(oldPeer)).Should().NotBeNull();
    }

    /// <summary>Review L-stage1 #4: one that died after it was complete is finished: new database live, old one kept.</summary>
    [Fact]
    public async Task ACutoverInterruptedAfterItWasDone_IsFinishedAtStart()
    {
        using var blind = new BlindNodeFactory();
        var newPeer = Guid.NewGuid();
        await StageInterruptedCutoverAsync(blind.DataPath, BlindSeedCutover.Done, oldPeer: Guid.NewGuid(), newPeer);

        var whitelist = blind.Services.GetRequiredService<IWhitelistRepository>();

        (await whitelist.GetByNodeIdAsync(newPeer)).Should().NotBeNull("the new database stays live");
        File.Exists(Path.Combine(blind.DataPath, "beememorybank.db.pre-seed")).Should().BeTrue();
        Directory.Exists(BlindSeedCutover.DirOf(blind.DataPath)).Should().BeFalse();
    }

    /// <summary>
    /// The files a crash leaves: the new database live (holding <paramref name="newPeer"/>), the old one
    /// in the cutover directory (holding <paramref name="oldPeer"/>), and the marker at <paramref name="phase"/>.
    /// </summary>
    private static async Task StageInterruptedCutoverAsync(string dataPath, string phase, Guid oldPeer, Guid newPeer)
    {
        Directory.CreateDirectory(dataPath);
        var key = new FileNodeKey(Path.Combine(dataPath, FileNodeKey.FileName));
        var identity = new NodeIdentity
        {
            NodeId = BlindNodeId.NewId(), DisplayName = "Blind", Ed25519PublicKey = key.Create(),
            Ed25519PrivateKey = [], Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion, CreatedAt = DateTime.UtcNow
        };
        var oldDir = Path.Combine(dataPath, "old-build");
        foreach (var (dir, peer) in new[] { (oldDir, oldPeer), (dataPath, newPeer) })
        {
            using var db = new DbConnectionFactory(dir);
            await new MigrationRunner(db).RunMigrationsAsync();
            await new NodeIdentityRepository(db).CreateAsync(identity);
            await new WhitelistRepository(db).CreateAsync(new WhitelistEntry
            {
                NodeId = peer, DisplayName = "peer", Ed25519PublicKey = new byte[32], Status = "A",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var cutover = new BlindSeedCutover(dataPath);
        cutover.Prepare();
        File.Move(Path.Combine(oldDir, "beememorybank.db"), Path.Combine(BlindSeedCutover.DirOf(dataPath), "old.db"));
        cutover.WriteMarker(Guid.NewGuid(), phase);
    }

    /// <summary>
    /// Review L-stage0 round 2 #2: a rotation a crash left in Committing (a build that closed it in a
    /// background task, lost after the commit was already in the log) is closed at the next start.
    /// </summary>
    [Fact]
    public async Task ARotationLeftInCommitting_IsClosedAtStart()
    {
        using var blind = new BlindNodeFactory();
        var key = new FileNodeKey(Path.Combine(blind.DataPath, FileNodeKey.FileName));
        using (var db = new DbConnectionFactory(blind.DataPath))
        {
            await new MigrationRunner(db).RunMigrationsAsync();
            await new NodeIdentityRepository(db).CreateAsync(new NodeIdentity
            {
                NodeId = BlindNodeId.NewId(), DisplayName = "Blind", Ed25519PublicKey = key.Create(),
                Ed25519PrivateKey = [], Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion, CreatedAt = DateTime.UtcNow
            });
            var now = DateTime.UtcNow.ToString("O");
            await new DekRotationStateRepository(db).UpsertAsync(new DekRotationStateRow(
                "stranded-commit", DekRotationState.Committing, "its-proposal", now, null, null,
                null, null, null, null, null, now, now));
        }

        using var scope = blind.Services.CreateScope();
        var state = (await scope.ServiceProvider.GetRequiredService<IDekRotationStateRepository>().GetAsync("stranded-commit"))?.State;

        state.Should().Be(DekRotationState.Applied);
    }

    /// <summary>
    /// Review L-stage0 round 2 #1: what a pre-fix build let into the log — an event authored by a
    /// blind node, a rotation carrying an envelope for this blind node — is taken out at start:
    /// quarantined, no longer served to peers, and refused if delivered again. Ordinary events stay.
    /// </summary>
    [Fact]
    public async Task StoredViolations_AreQuarantined_NotServed_AndRefusedOnReplay()
    {
        using var blind = new BlindNodeFactory();
        var key = new FileNodeKey(Path.Combine(blind.DataPath, FileNodeKey.FileName));
        var selfId = BlindNodeId.NewId();
        var hub = Guid.NewGuid();
        var peer = Guid.NewGuid();
        var blindAuthored = StoredEvent(BlindNodeId.NewId(), EventTypes.FolderCreate, "{}");
        var sealedForMe = StoredEvent(hub, EventTypes.DekRotationProposed, JsonSerializer.Serialize(
            new DekRotationProposedPayload(2, "2026-09-27T00:00:00Z", "2026-09-28T00:00:00Z", hub.ToString(),
                DekEnvelopes: new DekEnvelopesPayload("e", new Dictionary<string, DekEnvelopeBox>
                {
                    [selfId.ToString().ToUpperInvariant()] = new(EnvelopeSentinel, "nonce")
                }))));
        var ordinary = StoredEvent(hub, EventTypes.FolderCreate, "{}");
        using (var db = new DbConnectionFactory(blind.DataPath))
        {
            await new MigrationRunner(db).RunMigrationsAsync();
            await new NodeIdentityRepository(db).CreateAsync(new NodeIdentity
            {
                NodeId = selfId, DisplayName = "Blind", Ed25519PublicKey = key.Create(),
                Ed25519PrivateKey = [], Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion, CreatedAt = DateTime.UtcNow
            });
            await new WhitelistRepository(db).CreateAsync(new WhitelistEntry
            {
                NodeId = peer, DisplayName = "peer", Ed25519PublicKey = new byte[32], Status = "A",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            var log = new EventLogRepository(db);
            foreach (var evt in new[] { blindAuthored, sealedForMe, ordinary }) await log.AppendAsync(evt);
        }

        using var http = blind.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer",
            blind.Services.GetRequiredService<SyncTokenStore>().IssueToken(peer, SyncProtocolVersion.Current));
        var served = (await http.GetFromJsonAsync<List<JsonElement>>("/api/sync/events?afterSequence=0"))!
            .Select(e => e.GetProperty("eventId").GetGuid()).ToList();
        using var scope = blind.Services.CreateScope();
        var quarantined = (await scope.ServiceProvider.GetRequiredService<ISyncQuarantineRepository>().GetAllAsync())
            .Select(q => q.EventId).ToList();
        var replay = () => scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(sealedForMe);

        served.Should().Contain(ordinary.EventId).And.NotContain(new[] { blindAuthored.EventId, sealedForMe.EventId });
        quarantined.Should().Contain(new[] { blindAuthored.EventId, sealedForMe.EventId }).And.NotContain(ordinary.EventId);
        await replay.Should().ThrowAsync<UnauthorizedAccessException>();
        (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().ExistsAsync(sealedForMe.EventId)).Should().BeFalse();
    }

    /// <summary>
    /// Review L-stage1 #2: the removed envelope is gone from the bytes on disk — main file and WAL —
    /// not just from queries, and the node reports that its master DEK must be treated as exposed.
    /// </summary>
    [Fact]
    public async Task ARemovedEnvelope_LeavesNoBytesOnDisk_AndTheNodeReportsTheExposure()
    {
        using var blind = new BlindNodeFactory();
        var key = new FileNodeKey(Path.Combine(blind.DataPath, FileNodeKey.FileName));
        var selfId = BlindNodeId.NewId();
        var hub = Guid.NewGuid();
        var peer = Guid.NewGuid();
        var sealedForMe = StoredEvent(hub, EventTypes.DekRotationCommit, JsonSerializer.Serialize(
            new DekRotationCommitPayload("p", 2, "2026-09-27T00:00:00Z", hub.ToString(),
                DekEnvelopes: new DekEnvelopesPayload("e", new Dictionary<string, DekEnvelopeBox>
                {
                    // Overflow-sized, as a real payload with many envelopes can be: freed overflow pages
                    // keep their bytes unless scrubbed.
                    [selfId.ToString().ToUpperInvariant()] = new(string.Concat(Enumerable.Repeat(EnvelopeSentinel, 200)), "nonce")
                }))));
        using (var db = new DbConnectionFactory(blind.DataPath))
        {
            await new MigrationRunner(db).RunMigrationsAsync();
            await new NodeIdentityRepository(db).CreateAsync(new NodeIdentity
            {
                NodeId = selfId, DisplayName = "Blind", Ed25519PublicKey = key.Create(),
                Ed25519PrivateKey = [], Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion, CreatedAt = DateTime.UtcNow
            });
            await new WhitelistRepository(db).CreateAsync(new WhitelistEntry
            {
                NodeId = peer, DisplayName = "peer", Ed25519PublicKey = new byte[32], Status = "A",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await new EventLogRepository(db).AppendAsync(sealedForMe);
        }

        using var http = blind.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer",
            blind.Services.GetRequiredService<SyncTokenStore>().IssueToken(peer, SyncProtocolVersion.Current));
        var standing = await http.GetFromJsonAsync<JsonElement>("/api/sync/my-standing");
        var sentinel = System.Text.Encoding.UTF8.GetBytes(EnvelopeSentinel);
        var onDisk = new[] { "beememorybank.db", "beememorybank.db-wal" }
            .Select(f => Path.Combine(blind.DataPath, f)).Where(File.Exists)
            .Select(ReadShared).ToList();

        onDisk.Should().NotBeEmpty();
        onDisk.Select(bytes => bytes.AsSpan().IndexOf(sentinel)).Should().OnlyContain(at => at < 0,
            "the envelope must not survive in a free page or an old WAL frame");
        standing.GetProperty("dek_exposure").GetString().Should().ContainEquivalentOf(sealedForMe.EventId.ToString());
    }

    /// <summary>
    /// Review L-stage1 round 2 #1: the process died after the repair committed the delete and before it
    /// scrubbed the file. The row is gone, its bytes are not; the marker written with the delete makes
    /// the next start scrub anyway.
    /// </summary>
    [Fact]
    public async Task AScrubThatDidNotRun_IsRunAtTheNextStart()
    {
        using var blind = new BlindNodeFactory();
        var key = new FileNodeKey(Path.Combine(blind.DataPath, FileNodeKey.FileName));
        var leftover = StoredEvent(Guid.NewGuid(), EventTypes.FolderCreate,
            JsonSerializer.Serialize(new { residue = string.Concat(Enumerable.Repeat(EnvelopeSentinel, 200)) }));
        using (var db = new DbConnectionFactory(blind.DataPath))
        {
            await new MigrationRunner(db).RunMigrationsAsync();
            await new NodeIdentityRepository(db).CreateAsync(new NodeIdentity
            {
                NodeId = BlindNodeId.NewId(), DisplayName = "Blind", Ed25519PublicKey = key.Create(),
                Ed25519PrivateKey = [], Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion, CreatedAt = DateTime.UtcNow
            });
            await new EventLogRepository(db).AppendAsync(leftover);
            using var conn = db.CreateConnection();
            // What the repair's committed transaction leaves when nothing scrubbed after it: the row gone
            // (here without secure_delete, so its bytes stay), and the pending marker.
            await conn.ExecuteAsync("PRAGMA secure_delete = OFF");
            await conn.ExecuteAsync("DELETE FROM tbl_event");
            await conn.ExecuteAsync("INSERT INTO tbl_blind_state (key, value, updated_at) VALUES (@k, 'x', 'x')",
                new { k = StoredEventRepair.CleanupPendingKey });
        }
        var sentinel = System.Text.Encoding.UTF8.GetBytes(EnvelopeSentinel);
        ReadShared(Path.Combine(blind.DataPath, "beememorybank.db")).AsSpan().IndexOf(sentinel)
            .Should().BeGreaterThan(-1, "precondition: the removed row's bytes are still in the file");

        _ = blind.Services;
        var onDisk = new[] { "beememorybank.db", "beememorybank.db-wal" }
            .Select(f => Path.Combine(blind.DataPath, f)).Where(File.Exists).Select(ReadShared).ToList();
        using var check = blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();

        onDisk.Select(bytes => bytes.AsSpan().IndexOf(sentinel)).Should().OnlyContain(at => at < 0);
        (await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_blind_state WHERE key = @k",
            new { k = StoredEventRepair.CleanupPendingKey })).Should().Be(0, "cleared only after the scrub");
    }

    /// <summary>
    /// Review L-stage1 round 3 #1: a scrub whose checkpoint a reader holds back (SQLITE_BUSY, reported in
    /// the checkpoint's result rather than thrown) has not scrubbed the file: the marker stays.
    /// Round 4 #1: once the reader is gone, the running node's own sync cycle finishes the job — no
    /// restart needed.
    /// </summary>
    [Fact]
    public async Task AScrubHeldBackByAReader_KeepsItsMarker_AndFinishesLater()
    {
        using var blind = new BlindNodeFactory();
        var key = new FileNodeKey(Path.Combine(blind.DataPath, FileNodeKey.FileName));
        var dbPath = Path.Combine(blind.DataPath, "beememorybank.db");
        using (var db = new DbConnectionFactory(blind.DataPath))
        {
            await new MigrationRunner(db).RunMigrationsAsync();
            await new NodeIdentityRepository(db).CreateAsync(new NodeIdentity
            {
                NodeId = BlindNodeId.NewId(), DisplayName = "Blind", Ed25519PublicKey = key.Create(),
                Ed25519PrivateKey = [], Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion, CreatedAt = DateTime.UtcNow
            });
            await new EventLogRepository(db).AppendAsync(StoredEvent(Guid.NewGuid(), EventTypes.FolderCreate,
                JsonSerializer.Serialize(new { residue = string.Concat(Enumerable.Repeat(EnvelopeSentinel, 200)) })));
            using var conn = db.CreateConnection();
            await conn.ExecuteAsync("PRAGMA secure_delete = OFF");
            await conn.ExecuteAsync("DELETE FROM tbl_event");
            await conn.ExecuteAsync("INSERT INTO tbl_blind_state (key, value, updated_at) VALUES (@k, 'x', 'x')",
                new { k = StoredEventRepair.CleanupPendingKey });
        }
        long Pending(System.Data.IDbConnection c) => c.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM tbl_blind_state WHERE key = @k", new { k = StoredEventRepair.CleanupPendingKey });

        using (var reader = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            reader.Open();
            using var snapshot = reader.BeginTransaction(deferred: true);
            reader.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_blind_state", transaction: snapshot);

            _ = blind.Services;
            // Past the busy timeout: every run the start began has met the reader by now.
            await Task.Delay(TimeSpan.FromSeconds(7));
        }
        using var check = blind.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();

        Pending(check).Should().Be(1, "a scrub held back by a reader has not finished; its marker stays");

        // No explicit repair call: the node's sync scheduler, woken as a save would wake it, is what
        // retries. Its own background work can hold a checkpoint back for a moment too; the next cycle
        // simply comes again.
        var trigger = blind.Services.GetRequiredService<ISyncTrigger>();
        for (var wait = 0; wait < 60 && Pending(check) > 0; wait++)
        {
            trigger.Signal();
            await Task.Delay(500);
        }
        var sentinel = System.Text.Encoding.UTF8.GetBytes(EnvelopeSentinel);
        var onDisk = new[] { "beememorybank.db", "beememorybank.db-wal" }
            .Select(f => Path.Combine(blind.DataPath, f)).Where(File.Exists).Select(ReadShared).ToList();

        Pending(check).Should().Be(0, "a sync cycle of the running node finished the scrub");
        onDisk.Select(bytes => bytes.AsSpan().IndexOf(sentinel)).Should().OnlyContain(at => at < 0);
    }

    private const string EnvelopeSentinel = "BMB-ENVELOPE-SENTINEL-7e3b2a91-must-not-remain";

    private static byte[] ReadShared(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        file.CopyTo(copy);
        return copy.ToArray();
    }

    private static SyncEvent StoredEvent(Guid originator, string type, string payload) => new()
    {
        EventId = Guid.NewGuid(), NodeId = originator, LamportTs = 3, EventType = type, Payload = payload,
        Signature = new byte[64], ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow
    };

    /// <summary>A v=2 row with a seed in its private columns defeats keeping the key outside the database.</summary>
    [Fact]
    public async Task V2IdentityWithPrivateKeyInTheDatabase_IsRefused()
    {
        using var blind = new BlindNodeFactory();
        Directory.CreateDirectory(blind.DataPath);
        var key = new FileNodeKey(Path.Combine(blind.DataPath, FileNodeKey.FileName));
        var publicKey = key.Create();
        using (var db = new DbConnectionFactory(blind.DataPath))
        {
            await new MigrationRunner(db).RunMigrationsAsync();
            await new NodeIdentityRepository(db).CreateAsync(new NodeIdentity
            {
                NodeId = BlindNodeId.NewId(), DisplayName = "Blind", Ed25519PublicKey = publicKey,
                Ed25519PrivateKey = key.ReadSeed(), Ed25519PrivateKeyV = NodeIdentityCrypto.ExternalKeyVersion,
                CreatedAt = DateTime.UtcNow
            });
        }

        var start = () => blind.Services;

        start.Should().Throw<InvalidOperationException>().WithMessage("*private key material*");
    }
}
