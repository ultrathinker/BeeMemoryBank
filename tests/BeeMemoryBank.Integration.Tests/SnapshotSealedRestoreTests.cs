using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Restores on an encrypted node (docs/design/step-b-spec.md 8.4, 10.7, red tests 15.1.7 restore half, 15.1.9 network restore, 15.1.16 restore half):
/// the container is opened in memory and imported into a staging copy under the vault's page key, the staged database is edited and swapped like
/// today, the boot store is told before and after (hooks), a plain database in an archive is refused before anything is extracted, and a snapshot
/// of an earlier vault key is opened through its key envelope. The boot-store side is a recording double: the mirror of track 2 is not on this branch.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class SnapshotSealedRestoreTests(SealedNodeTemplate template) : IClassFixture<SealedNodeTemplate>
{
    private const string Changed = "Changed after the snapshot";

    private sealed class RecordingHooks(Func<Task>? whileBefore = null) : ISealedRestoreHooks
    {
        public List<string> Calls { get; } = [];
        public byte[]? StagedDek { get; private set; }
        public long StagedSlots { get; private set; }

        public async Task BeforeSwapAsync(SqliteConnection staged, byte[] stagedMasterDek, CancellationToken ct)
        {
            Calls.Add("before");
            StagedDek = (byte[])stagedMasterDek.Clone();
            StagedSlots = staged.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_key_slot");
            if (whileBefore is not null) await whileBefore();
        }

        public Task AfterSwapAsync(CancellationToken ct)
        {
            Calls.Add("after");
            return Task.CompletedTask;
        }
    }

    private static string DisplayName(DbConnectionFactory factory)
    {
        using var c = factory.CreateConnection();
        return c.ExecuteScalar<string>("SELECT display_name FROM tbl_node_identity")!;
    }

    private static void SetDisplayName(DbConnectionFactory factory, string name)
    {
        using var c = factory.CreateConnection();
        c.Execute("UPDATE tbl_node_identity SET display_name = @name", new { name });
    }

    private static long Count(DbConnectionFactory factory, string sql)
    {
        using var c = factory.CreateConnection();
        return c.ExecuteScalar<long>(sql);
    }

    private static bool StagingIsEmpty(string dir)
    {
        var staging = SnapshotStaging.DirIn(dir);
        return !Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any();
    }

    private static byte[] FileHash(string path) => SHA256.HashData(File.ReadAllBytes(path));

    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>A plain node of its own (a vault that was reset and set up again): fresh identity, fresh master DEK, the same password.</summary>
    private static async Task<(string Root, string Dir, DbConnectionFactory Factory, SessionService Session, SnapshotService Snapshots)> NewPlainNodeAsync(string password)
    {
        DapperConfig.Configure();
        var root = Path.Combine(Path.GetTempPath(), "bmb-plain2-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "data");
        Directory.CreateDirectory(dir);
        var factory = new DbConnectionFactory(Path.Combine(dir, "beememorybank.db"));
        await new MigrationRunner(factory).RunMigrationsAsync();
        var nodeRepo = new NodeIdentityRepository(factory);
        var keySlots = new KeySlotRepository(factory);
        await new InitializationService(nodeRepo, keySlots, new UserRepository(factory), factory).InitializeAsync("admin", "Node2", password);
        var session = new SessionService(keySlots);
        (await session.UnlockAsync(password)).Should().BeTrue();
        var snapshots = new SnapshotService(dir, factory, nodeRepo, new NullLamportClock(), keys: new SessionSnapshotKeyOperations(session));
        return (root, dir, factory, session, snapshots);
    }

    private static void DropPlainNode(string root, DbConnectionFactory factory)
    {
        factory.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(root, true); } catch { }
    }

    /// <summary>The same archive with one entry's bytes changed (the entry list and the manifest are kept).</summary>
    private static void RewriteArchive(string source, string target, string entryName, Func<byte[], byte[]> change)
    {
        using var input = File.OpenRead(source);
        using var gzipIn = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzipIn);
        using var output = File.Create(target);
        using var gzipOut = new GZipStream(output, CompressionLevel.Optimal);
        using var writer = new TarWriter(gzipOut, TarEntryFormat.Pax);
        while (reader.GetNextEntry() is { } entry)
        {
            using var memory = new MemoryStream();
            entry.DataStream!.CopyTo(memory);
            var bytes = entry.Name == entryName ? change(memory.ToArray()) : memory.ToArray();
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entry.Name) { DataStream = new MemoryStream(bytes) });
        }
    }

    private static void RewriteArchiveWithAdditionalEntry(string source, string target, string entryName, byte[] bytes)
    {
        using var input = File.OpenRead(source);
        using var gzipIn = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(gzipIn);
        using var output = File.Create(target);
        using var gzipOut = new GZipStream(output, CompressionLevel.Optimal);
        using var writer = new TarWriter(gzipOut, TarEntryFormat.Pax);
        while (reader.GetNextEntry() is { } entry)
        {
            using var memory = new MemoryStream();
            entry.DataStream!.CopyTo(memory);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entry.Name) { DataStream = new MemoryStream(memory.ToArray()) });
        }
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entryName) { DataStream = new MemoryStream(bytes) });
    }

    // ------------------------------------------------------------ same vault

    [Fact]
    public async Task A_snapshot_is_restored_onto_the_sealed_node_in_place_with_the_boot_store_told_before_and_after_and_no_plain_file_meanwhile()
    {
        using var node = await SealedNode.CreateAsync(template);
        var info = await node.Snapshots.CreateAsync(filterSecrets: false, sign: false);
        var original = DisplayName(node.Factory);
        SetDisplayName(node.Factory, Changed);
        string? nameWhenBeforeRan = null;
        var hooks = new RecordingHooks(() =>
        {
            nameWhenBeforeRan = DisplayName(node.Factory);
            return Task.CompletedTask;
        });
        node.Snapshots.SealedHooks = hooks;
        var staging = SnapshotStaging.DirIn(node.Dir);
        Directory.CreateDirectory(staging);
        using var watch = new PlainFileWatch([staging], template.Canaries);

        var outcome = await node.Snapshots.RestoreAsync(info.FileName);
        watch.Stop();

        outcome.Standalone.Should().BeFalse();
        DisplayName(node.Factory).Should().Be(original, "the snapshot's database is the node's now");
        hooks.Calls.Should().Equal("before", "after");
        nameWhenBeforeRan.Should().Be(Changed, "the boot store is told before the file is replaced");
        hooks.StagedSlots.Should().BeGreaterThan(0);
        hooks.StagedDek.Should().Equal(node.CopyDek(), "the snapshot is of this vault: its slots hand out this vault's master key");
        watch.Seen.Keys.Should().NotBeEmpty("the watch saw the extraction folder");
        watch.Plain.Should().BeEmpty("the extracted container and the staging copy are never a plain database");
        watch.CanaryHits.Should().BeEmpty();
        StagingIsEmpty(node.Dir).Should().BeTrue();
        VaultStoreFiles.HasSqliteMagic(Path.Combine(node.Dir, "beememorybank.db")).Should().BeFalse("the live file is still sealed");
        Count(node.Factory, "SELECT COUNT(*) FROM tbl_article").Should().BeGreaterThan(0);
        MetadataCanaries.ScanDirectory(node.Dir, template.Canaries).Should().BeEmpty("nothing in the data directory holds a canary in clear");
    }

    [Fact]
    public async Task A_standalone_restore_on_the_sealed_node_gives_it_a_new_identity_sealed_under_the_snapshots_master_key()
    {
        using var node = await SealedNode.CreateAsync(template);
        var info = await node.Snapshots.CreateAsync(filterSecrets: false, sign: false);
        var hooks = new RecordingHooks();
        node.Snapshots.SealedHooks = hooks;
        string oldNodeId;
        using (var c = node.Factory.CreateConnection()) oldNodeId = c.ExecuteScalar<string>("SELECT node_id FROM tbl_node_identity")!;

        var outcome = await node.Snapshots.RestoreAsync(info.FileName, standaloneMode: true, masterPassword: SealedNodeTemplate.Password);

        outcome.Standalone.Should().BeTrue();
        outcome.NewNodeId.Should().NotBeNull();
        using (var c = node.Factory.CreateConnection())
        {
            c.ExecuteScalar<string>("SELECT node_id FROM tbl_node_identity").Should().Be(outcome.NewNodeId.ToString()).And.NotBe(oldNodeId);
            c.ExecuteScalar<long>("SELECT ed25519_private_key_v FROM tbl_node_identity").Should().Be(1, "the new private key is wrapped under the master DEK");
            c.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_event").Should().Be(0, "the originator's event log is wiped");
        }
        hooks.Calls.Should().Equal("before", "after");
        hooks.StagedDek.Should().Equal(node.CopyDek());
        VaultStoreFiles.HasSqliteMagic(Path.Combine(node.Dir, "beememorybank.db")).Should().BeFalse();
        StagingIsEmpty(node.Dir).Should().BeTrue();
    }

    [Fact]
    public async Task An_encrypted_node_without_boot_store_hooks_refuses_to_restore_and_the_live_vault_is_untouched()
    {
        using var node = await SealedNode.CreateAsync(template);
        var info = await node.Snapshots.CreateAsync(filterSecrets: false, sign: false);
        SetDisplayName(node.Factory, Changed);

        var act = async () => await node.Snapshots.RestoreAsync(info.FileName);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unlock material*");
        DisplayName(node.Factory).Should().Be(Changed);
        StagingIsEmpty(node.Dir).Should().BeTrue();
    }

    /// <summary>BMB-304: a node that cannot hold the snapshot's database in memory says which limit would do, before it replaces anything.</summary>
    [Fact]
    public async Task A_node_with_less_memory_than_the_snapshots_database_needs_refuses_the_restore_naming_the_limit_and_the_live_vault_is_untouched()
    {
        using var node = await SealedNode.CreateAsync(template);
        var info = await node.Snapshots.CreateAsync(filterSecrets: false, sign: false);
        SetDisplayName(node.Factory, Changed);
        var hooks = new RecordingHooks();
        node.Snapshots.SealedHooks = hooks;
        // The test's ceiling is half the database: the fixture really is larger than it (the refusal is the database's, not only the slack's).
        var dbSize = new FileInfo(Path.Combine(node.Dir, "beememorybank.db")).Length;
        node.Snapshots.FreeMemoryOverride = dbSize / 2;
        (dbSize / 2).Should().BeLessThan(dbSize);

        var act = async () => await node.Snapshots.RestoreAsync(info.FileName);

        var ex = (await act.Should().ThrowAsync<NotEnoughMemoryException>()).Which;
        ex.Message.Should().Contain("Opening the database of a snapshot needs about").And.Contain("MB free")
            .And.Contain($"to at least {ex.RecommendedLimitBytes / (1024 * 1024)} MB").And.Contain("Nothing was changed");
        hooks.Calls.Should().BeEmpty("the boot store is not told anything before the memory is known to be there");
        DisplayName(node.Factory).Should().Be(Changed, "the live vault is untouched");
        StagingIsEmpty(node.Dir).Should().BeTrue();

        // The ceiling lifted, the same snapshot restores.
        node.Snapshots.FreeMemoryOverride = null;
        await node.Snapshots.RestoreAsync(info.FileName);
        DisplayName(node.Factory).Should().NotBe(Changed);
    }

    [Fact]
    public async Task A_snapshot_with_a_plain_database_is_refused_by_an_encrypted_node_before_anything_is_extracted()
    {
        var (root, plainDir, plainFactory, _, plainSnapshots) = await SealedNode.CreatePlainAsync(template);
        try
        {
            var plainInfo = await plainSnapshots.CreateAsync(filterSecrets: false, sign: false, encryptDb: false);
            using var node = await SealedNode.CreateAsync(template);
            Directory.CreateDirectory(node.Snapshots.SnapshotsDir);
            File.Copy(plainSnapshots.GetSnapshotPath(plainInfo.FileName), Path.Combine(node.Snapshots.SnapshotsDir, plainInfo.FileName));
            node.Snapshots.SealedHooks = new RecordingHooks();
            var staging = SnapshotStaging.DirIn(node.Dir);
            Directory.CreateDirectory(staging);
            using var watch = new PlainFileWatch([staging], template.Canaries);

            var act = async () => await node.Snapshots.RestoreAsync(plainInfo.FileName);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not encrypted*");
            watch.Stop();
            watch.Plain.Should().BeEmpty();
            watch.CanaryHits.Should().BeEmpty();
            StagingIsEmpty(node.Dir).Should().BeTrue();
            plainDir.Should().NotBeNull();
        }
        finally
        {
            DropPlainNode(root, plainFactory);
        }
    }

    [Fact]
    public async Task A_sealed_restore_rejects_a_plain_companion_after_an_encrypted_canonical_database_before_extraction()
    {
        var (root, plainDir, plainFactory, _, _) = await SealedNode.CreatePlainAsync(template);
        try
        {
            using var node = await SealedNode.CreateAsync(template);
            var info = await node.Snapshots.CreateAsync(filterSecrets: false, sign: false);
            var archive = node.Snapshots.GetSnapshotPath(info.FileName);
            var tamperedName = "sealed-with-plain-companion.tar.gz";
            var tampered = Path.Combine(node.Snapshots.SnapshotsDir, tamperedName);
            RewriteArchiveWithAdditionalEntry(archive, tampered, "extra.sqlite", ReadSharedBytes(Path.Combine(plainDir, "beememorybank.db")));

            node.Snapshots.SealedHooks = new RecordingHooks();
            SetDisplayName(node.Factory, Changed);
            var staging = SnapshotStaging.DirIn(node.Dir);
            Directory.CreateDirectory(staging);
            using var watch = new PlainFileWatch([staging], template.Canaries);

            var act = async () => await node.Snapshots.RestoreAsync(tamperedName);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not encrypted*");
            watch.Stop();
            watch.Plain.Should().BeEmpty("the companion is rejected before any regular member is extracted");
            DisplayName(node.Factory).Should().Be(Changed);
            StagingIsEmpty(node.Dir).Should().BeTrue();
        }
        finally
        {
            DropPlainNode(root, plainFactory);
        }
    }

    [Fact]
    public async Task A_join_import_from_a_snapshot_of_the_sealed_node_attaches_the_staged_copy_without_a_key_clause_and_leaves_no_plain_file()
    {
        using var node = await SealedNode.CreateAsync(template);
        var info = await node.Snapshots.CreateAsync(filterSecrets: true, sign: true, cpSequenceNum: 1);
        var path = node.Snapshots.GetSnapshotPath(info.FileName);
        var identity = (await node.NodeRepo.GetAsync())!;
        var articles = Count(node.Factory, "SELECT COUNT(*) FROM tbl_article");
        var staging = SnapshotStaging.DirIn(node.Dir);
        Directory.CreateDirectory(staging);
        using var watch = new PlainFileWatch([staging], template.Canaries);

        var (cpSeq, _) = await node.Snapshots.RestoreForJoinAsync(path, await File.ReadAllBytesAsync(path + ".sig"), identity.Ed25519PublicKey);
        watch.Stop();

        cpSeq.Should().Be(1);
        watch.Plain.Should().BeEmpty();
        watch.CanaryHits.Should().BeEmpty();
        StagingIsEmpty(node.Dir).Should().BeTrue();
        Count(node.Factory, "SELECT COUNT(*) FROM tbl_article").Should().Be(articles, "importing a node's own rows changes nothing");
    }

    /// <summary>The database entry of a snapshot archive, written beside the staging folder the way a restore extracts it (the container of a sealed node).</summary>
    private static async Task<string> ExtractContainerAsync(string snapshotPath, string folder)
    {
        Directory.CreateDirectory(folder);
        var extracted = Path.Combine(folder, "beememorybank.db");
        await using var file = File.OpenRead(snapshotPath);
        await using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (!entry.Name.EndsWith("beememorybank.db", StringComparison.OrdinalIgnoreCase)) continue;
            await using var output = File.Create(extracted);
            await entry.DataStream!.CopyToAsync(output);
            return extracted;
        }
        throw new InvalidOperationException("The snapshot has no database entry.");
    }

    /// <summary>
    /// BMB-304 fix3 (G4): a join import of a sealed node's snapshot could not put its finished staging copy over the extracted container on Windows (the
    /// move answered "access denied"): a destination with the read-only attribute, or one that a scanner has open for a moment, is replaced all the same.
    /// </summary>
    [Theory]
    [InlineData("read-only")]
    [InlineData("open without sharing delete for a moment")]
    public async Task The_sealed_staging_copy_replaces_an_extracted_container_that_is_read_only_or_briefly_open(string state)
    {
        if (!OperatingSystem.IsWindows()) return;   // elsewhere a rename replaces both
        using var node = await SealedNode.CreateAsync(template);
        var info = await node.Snapshots.CreateAsync(filterSecrets: false, sign: false);
        var articles = Count(node.Factory, "SELECT COUNT(*) FROM tbl_article");
        var folder = Path.Combine(SnapshotStaging.DirIn(node.Dir), "g4-" + Guid.NewGuid().ToString("N"));
        var extracted = await ExtractContainerAsync(node.Snapshots.GetSnapshotPath(info.FileName), folder);
        FileStream? holder = null;
        Task? release = null;
        if (state == "read-only") File.SetAttributes(extracted, FileAttributes.ReadOnly);
        else
        {
            holder = new FileStream(extracted, FileMode.Open, FileAccess.Read, FileShare.Read);
            release = Task.Run(async () => { await Task.Delay(400); holder.Dispose(); });
        }

        await node.Snapshots.DecryptDbIfNeededAsync(extracted);
        if (release is not null) await release;

        using (var conn = node.Factory.OpenFile(extracted))
            conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_article").Should().Be(articles, "the file now under that name is the sealed copy of the snapshot's database");
        File.Exists(extracted + ".sealing").Should().BeFalse();
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task A_network_restore_replaces_the_replicated_tables_of_the_sealed_node_from_a_snapshot_through_a_staged_copy()
    {
        using var node = await SealedNode.CreateAsync(template);
        var nodeWithShield = new SnapshotService(node.Dir, node.Factory, node.NodeRepo, new NullLamportClock(),
            replayShieldRepo: new RestoreReplayShieldRepository(node.Factory), keys: new SessionSnapshotKeyOperations(node.Session));
        var info = await nodeWithShield.CreateAsync(filterSecrets: false, sign: false);
        var folders = Count(node.Factory, "SELECT COUNT(*) FROM tbl_folder");
        using (var c = node.Factory.CreateConnection()) c.Execute("UPDATE tbl_folder SET name = 'renamed after the snapshot'");
        Count(node.Factory, "SELECT COUNT(*) FROM tbl_folder WHERE name = 'renamed after the snapshot'").Should().Be(folders);
        var self = (await node.NodeRepo.GetAsync())!;
        var restoreEvent = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = self.NodeId, LamportTs = 100, EventType = "restore_network", ProtocolVersion = 1, CreatedAt = DateTime.UtcNow,
        };
        var staging = SnapshotStaging.DirIn(node.Dir);
        Directory.CreateDirectory(staging);
        using var watch = new PlainFileWatch([staging], template.Canaries);

        await nodeWithShield.ApplyNetworkRestoreAsync(nodeWithShield.GetSnapshotPath(info.FileName),
            new RestoreNetworkEventPayload("hash", "ts", 1, "exp", "https://example.invalid/x", false), restoreEvent);
        watch.Stop();

        Count(node.Factory, "SELECT COUNT(*) FROM tbl_folder WHERE name = 'renamed after the snapshot'").Should().Be(0, "the folder names are the snapshot's again");
        Count(node.Factory, "SELECT COUNT(*) FROM tbl_folder").Should().Be(folders);
        watch.Plain.Should().BeEmpty();
        watch.CanaryHits.Should().BeEmpty();
        StagingIsEmpty(node.Dir).Should().BeTrue();
        MetadataCanaries.ScanDirectory(node.Dir, template.Canaries).Should().BeEmpty();
    }

    [Fact]
    public async Task The_restore_hooks_over_the_mirror_write_the_wrapper_and_one_pending_door_per_slot_and_per_agent_and_rewrite_after()
    {
        using var node = await SealedNode.CreateAsync(template);
        var slotCount = (int)Count(node.Factory, "SELECT COUNT(*) FROM tbl_key_slot");
        var firstSlot = Count(node.Factory, "SELECT MIN(slot_id) FROM tbl_key_slot");
        var ownerId = Count(node.Factory, "SELECT id FROM tbl_user LIMIT 1");
        using (var c = node.Factory.CreateConnection())
        {
            c.Execute(@"INSERT INTO tbl_agent (name, key_prefix, key_hash, encrypted_dek, dek_iv, kdf_version, salt, status, created_at, owner_user_id)
                        VALUES ('agent-a', 'bee_x', 'hash-a', randomblob(48), randomblob(12), 1, randomblob(16), 'A', '2026-01-01', @owner),
                               ('agent-b', 'bee_y', 'hash-b', NULL, NULL, 0, NULL, 'A', '2026-01-01', @owner),
                               ('agent-c', 'bee_z', 'hash-c', randomblob(48), randomblob(12), 1, randomblob(16), 'D', '2026-01-01', @owner)", new { owner = ownerId });
        }
        var staging = Path.Combine(node.Dir, "staged-copy.tmp");
        using (var c = (SqliteConnection)node.Factory.CreateConnection()) c.Execute($"VACUUM INTO '{staging}'");
        var mirror = new RecordingMirror();
        var dek = node.CopyDek();

        using (var staged = node.Factory.OpenFile(staging))
        {
            var hooks = new MirrorRestoreHooks(mirror);
            await hooks.BeforeSwapAsync(staged, dek, CancellationToken.None);
            await hooks.AfterSwapAsync(CancellationToken.None);
        }

        mirror.Calls.Should().Equal(["wrapper", .. Enumerable.Repeat("door:slot", slotCount), "door:agent", "rewrite"]);
        mirror.Wrapper.Should().Equal(dek);
        mirror.Doors.Should().HaveCount(slotCount + 1);
        var slot = mirror.Doors.First(d => d.Kind == "slot" && d.SourceId == firstSlot);
        slot.SlotType.Should().Be("user");
        slot.Salt.Should().NotBeNull();
        slot.ArgonMemory.Should().BeGreaterThan(0);
        var agent = mirror.Doors.Single(d => d.Kind == "agent");
        agent.KeyHash.Should().Be("hash-a", "only an active agent with a wrapped key is a door");
        agent.KdfVersion.Should().Be(1);
    }

    private sealed class RecordingMirror : IUnlockMaterialMirror
    {
        public List<string> Calls { get; } = [];
        public List<PendingDoor> Doors { get; } = [];
        public byte[]? Wrapper { get; private set; }

        public Task RewriteAsync(CancellationToken ct = default)
        {
            Calls.Add("rewrite");
            return Task.CompletedTask;
        }

        public Task AddPendingDoorAsync(PendingDoor door, CancellationToken ct = default)
        {
            Calls.Add("door:" + door.Kind);
            Doors.Add(door);
            return Task.CompletedTask;
        }

        public Task RetireDoorAsync(string kind, long sourceId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task AddDbKeyWrapperAsync(byte[] masterDek, CancellationToken ct = default)
        {
            Calls.Add("wrapper");
            Wrapper = (byte[])masterDek.Clone();
            return Task.CompletedTask;
        }

        public Task WriteNewBootStoreAsync(string bootPath, IDbConnectionFactory vault, byte[] databaseKey, int dbkGeneration,
            IReadOnlyList<byte[]> masterDeks, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------ server auto-unlock is never restored (11.2 item 2)

    private const string ServerSlot = "server_auto_unlock";

    private static void InsertServerSlot(DbConnectionFactory factory)
    {
        using var c = factory.CreateConnection();
        c.Execute(@"INSERT INTO tbl_key_slot (slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism, created_at)
                    VALUES ('server_auto_unlock', randomblob(48), randomblob(12), NULL, NULL, NULL, NULL, '2026-01-01')");
    }

    [Fact]
    public async Task A_server_auto_unlock_slot_in_a_snapshot_does_not_arrive_in_either_restore_mode_on_an_encrypted_node()
    {
        using var node = await SealedNode.CreateAsync(template);
        var mirror = new RecordingMirror();
        node.Snapshots.SealedHooks = new MirrorRestoreHooks(mirror);
        var others = Count(node.Factory, "SELECT COUNT(*) FROM tbl_key_slot");
        InsertServerSlot(node.Factory);
        var info = await node.Snapshots.CreateAsync(filterSecrets: false, sign: false);
        Count(node.Factory, $"SELECT COUNT(*) FROM tbl_key_slot WHERE slot_type = '{ServerSlot}'").Should().Be(1, "the snapshot was made while the checkbox was on");

        await node.Snapshots.RestoreAsync(info.FileName, standaloneMode: true, masterPassword: SealedNodeTemplate.Password);

        Count(node.Factory, $"SELECT COUNT(*) FROM tbl_key_slot WHERE slot_type = '{ServerSlot}'").Should().Be(0, "restore of this node only");
        Count(node.Factory, "SELECT COUNT(*) FROM tbl_key_slot").Should().Be(others, "every other slot arrived");
        mirror.Doors.Should().NotContain(d => d.SlotType == ServerSlot, "no pending door for it");
        mirror.Doors.Count(d => d.Kind == "slot").Should().Be((int)others);

        InsertServerSlot(node.Factory);
        var second = await node.Snapshots.CreateAsync(filterSecrets: false, sign: false);
        await node.Snapshots.RestoreAsync(second.FileName);
        Count(node.Factory, $"SELECT COUNT(*) FROM tbl_key_slot WHERE slot_type = '{ServerSlot}'").Should().Be(0, "keep-identity restore");
        Count(node.Factory, "SELECT COUNT(*) FROM tbl_key_slot").Should().Be(others);
    }

    [Fact]
    public async Task A_network_restore_imports_no_key_slot_and_leaves_the_slot_this_server_enabled_alone()
    {
        using var node = await SealedNode.CreateAsync(template);
        var service = new SnapshotService(node.Dir, node.Factory, node.NodeRepo, new NullLamportClock(),
            replayShieldRepo: new RestoreReplayShieldRepository(node.Factory), keys: new SessionSnapshotKeyOperations(node.Session));
        var info = await service.CreateAsync(filterSecrets: false, sign: false);
        InsertServerSlot(node.Factory);
        var self = (await node.NodeRepo.GetAsync())!;

        await service.ApplyNetworkRestoreAsync(service.GetSnapshotPath(info.FileName),
            new RestoreNetworkEventPayload("hash", "ts", 1, "exp", "https://example.invalid/x", false),
            new SyncEvent { EventId = Guid.NewGuid(), NodeId = self.NodeId, LamportTs = 100, EventType = "restore_network", ProtocolVersion = 1, CreatedAt = DateTime.UtcNow });

        Count(node.Factory, $"SELECT COUNT(*) FROM tbl_key_slot WHERE slot_type = '{ServerSlot}'").Should().Be(1, "key slots are not replicated: nothing arrived, nothing left");
    }

    // ------------------------------------------------------------ the key envelope (10.7)

    // ------------------------------------------------------------ review round 1, H3: the live file is replaced behind the barrier

    private sealed record HeldRun(bool Quiesced, bool CompletedWhileHeld, bool LiveChangedWhileHeld, Exception? Failure);

    /// <summary>
    /// Runs <paramref name="operation"/> while this test holds one connection to the live database, and reports what the factory showed: whether the
    /// file-switch barrier was taken (<see cref="DbConnectionFactory.IsQuiescing"/>), whether the operation finished with the connection still held, and
    /// whether the live file's bytes changed meanwhile. The connection is returned afterwards, so the operation's drain can converge.
    /// </summary>
    private static async Task<HeldRun> RunWithAConnectionHeldAsync(DbConnectionFactory factory, string livePath, Func<Task> operation, bool returnTheConnection = true)
    {
        var held = factory.CreateConnection();
        held.ExecuteScalar<long>("SELECT 1");
        var before = SHA256.HashData(ReadSharedBytes(livePath));
        var running = Task.Run(operation);
        var quiesced = false;
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until && !running.IsCompleted)
        {
            if (factory.IsQuiescing) { quiesced = true; break; }
            await Task.Delay(20);
        }
        await Task.Delay(300);   // time for a swap to happen if the barrier did not stop it
        var completed = running.IsCompleted;
        var changed = !SHA256.HashData(ReadSharedBytes(livePath)).SequenceEqual(before);
        if (returnTheConnection) held.Dispose();
        Exception? failure = null;
        try { await running; }
        catch (Exception ex) { failure = ex; }
        if (!returnTheConnection) held.Dispose();
        return new HeldRun(quiesced, completed, changed, failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_restore_replaces_the_live_database_only_behind_the_quiesce_barrier_and_waits_for_a_connection_in_use(bool standalone)
    {
        var password = SealedNodeTemplate.Password;
        using var sealedOne = await SealedNode.CreateAsync(template);
        var factory = sealedOne.Factory;
        var snapshots = sealedOne.Snapshots;
        var dir = sealedOne.Dir;
        snapshots.SealedHooks = new RecordingHooks();
        {
            var info = await snapshots.CreateAsync(filterSecrets: false, sign: false);
            var original = DisplayName(factory);
            SetDisplayName(factory, Changed);

            var run = await RunWithAConnectionHeldAsync(factory, Path.Combine(dir, "beememorybank.db"),
                () => snapshots.RestoreAsync(info.FileName, standaloneMode: standalone, masterPassword: password));

            run.Quiesced.Should().BeTrue("the file is switched behind the factory's quiesce, never under a connection that is still in use");
            run.CompletedWhileHeld.Should().BeFalse("the restore waits for the connection in use");
            run.LiveChangedWhileHeld.Should().BeFalse("the live file is not replaced while a connection of the old generation is open");
            run.Failure.Should().BeNull();
            DisplayName(factory).Should().Be(original, "the restore went through once the connection was returned");
            factory.IsQuiescing.Should().BeFalse("the barrier is released when the swap is done");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_restore_that_cannot_drain_the_live_connections_changes_nothing_says_so_and_a_later_restore_goes_through(bool standalone)
    {
        var password = SealedNodeTemplate.Password;
        using var sealedOne = await SealedNode.CreateAsync(template);
        var (dir, factory, snapshots) = (sealedOne.Dir, sealedOne.Factory, sealedOne.Snapshots);
        snapshots.SealedHooks = new RecordingHooks();
        {
            var info = await snapshots.CreateAsync(filterSecrets: false, sign: false);
            var original = DisplayName(factory);
            SetDisplayName(factory, Changed);
            snapshots.LiveSwapDrainTimeout = TimeSpan.FromMilliseconds(500);

            var run = await RunWithAConnectionHeldAsync(factory, Path.Combine(dir, "beememorybank.db"),
                () => snapshots.RestoreAsync(info.FileName, standaloneMode: standalone, masterPassword: password), returnTheConnection: false);

            run.Failure.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Contain("Nothing was changed");
            run.LiveChangedWhileHeld.Should().BeFalse();
            DisplayName(factory).Should().Be(Changed, "the live database is exactly what it was");
            factory.IsQuiescing.Should().BeFalse("a refused restore releases the barrier");
            StagingIsEmpty(dir).Should().BeTrue();

            snapshots.LiveSwapDrainTimeout = TimeSpan.FromSeconds(30);
            await snapshots.RestoreAsync(info.FileName, standaloneMode: standalone, masterPassword: password);
            DisplayName(factory).Should().Be(original);
        }
    }

    [Fact]
    public async Task A_network_restore_that_fails_puts_the_backup_back_behind_the_same_barrier()
    {
        using var node = await SealedNode.CreateAsync(template);
        var service = new SnapshotService(node.Dir, node.Factory, node.NodeRepo, new NullLamportClock(),
            replayShieldRepo: new RestoreReplayShieldRepository(node.Factory), keys: new SessionSnapshotKeyOperations(node.Session));
        var self = (await node.NodeRepo.GetAsync())!;
        var restoreEvent = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = self.NodeId, LamportTs = 100, EventType = "restore_network", ProtocolVersion = 1, CreatedAt = DateTime.UtcNow,
        };
        var notAnArchive = Path.Combine(node.Dir, "not-an-archive.tar.gz");
        File.WriteAllText(notAnArchive, "this is not a snapshot");

        var run = await RunWithAConnectionHeldAsync(node.Factory, Path.Combine(node.Dir, "beememorybank.db"),
            () => service.ApplyNetworkRestoreAsync(notAnArchive, new RestoreNetworkEventPayload("hash", "ts", 1, "exp", "https://example.invalid/x", false), restoreEvent));

        run.Failure.Should().NotBeNull("the archive is not one; the restore fails and puts the pre-restore copy back");
        run.Quiesced.Should().BeTrue("the copy of the backup over the live file is a replacement of the live database like any other");
        run.CompletedWhileHeld.Should().BeFalse();
        node.Factory.IsQuiescing.Should().BeFalse();
        DisplayName(node.Factory).Should().NotBeNullOrEmpty("the database is intact and usable afterwards");
    }
}
