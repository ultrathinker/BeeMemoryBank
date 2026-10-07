using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Integration.Tests;

public class SnapshotServiceTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"bmb_snaptest_{Guid.NewGuid():N}");
    private DbConnectionFactory _factory = null!;
    private SnapshotService _service = null!;
    private NodeIdentityRepository _nodeRepo = null!;
    private NullLamportClock _clock = null!;
    private byte[] _testPublicKey = null!;
    private byte[] _testPrivateKey = null!;
    private Guid _testNodeId;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_snaptest_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        _nodeRepo = new NodeIdentityRepository(_factory);
        _clock = new NullLamportClock();
        _service = new SnapshotService(_tempDir, _factory, _nodeRepo, _clock, keys: new SessionSnapshotKeyOperations(null));

        (_testPublicKey, _testPrivateKey) = Ed25519Signer.GenerateKeyPair();
        _testNodeId = Guid.NewGuid();
        await _nodeRepo.CreateAsync(new NodeIdentity
        {
            NodeId = _testNodeId,
            DisplayName = "TestNode",
            Ed25519PublicKey = _testPublicKey,
            Ed25519PrivateKey = _testPrivateKey,
            CreatedAt = DateTime.UtcNow
        });
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
    public async Task CreateAsync_WithFilterSecrets_ExcludesNodeIdentity()
    {
        var info = await _service.CreateAsync(filterSecrets: true);
        info.Should().NotBeNull();

        var snapshotPath = _service.GetSnapshotPath(info.FileName);
        var extractDir = await ExtractSnapshotToTempDirAsync(snapshotPath);
        try
        {
            var dbPath = Path.Combine(extractDir, "beememorybank.db");
            File.Exists(dbPath).Should().BeTrue();

            using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            conn.Open();

            var secretTables = new[]
            {
                "tbl_node_identity", "tbl_session", "tbl_agent", "tbl_agent_access",
                "tbl_sync_position", "tbl_sync_push_position", "tbl_compaction_log", "tbl_event",
                "tbl_key_slot", "tbl_user", "tbl_folder_acl_entry", "tbl_audit_log",
                "tbl_hard_delete_audit"
            };

            foreach (var table in secretTables)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{table}'";
                var result = cmd.ExecuteScalar();
                result.Should().BeNull($"table {table} should have been dropped");
            }

            using var wlCmd = conn.CreateCommand();
            wlCmd.CommandText = "SELECT COUNT(*) FROM tbl_whitelist WHERE status != 'A'";
            var nonActive = Convert.ToInt64(wlCmd.ExecuteScalar());
            nonActive.Should().Be(0, "only active whitelist entries should remain");
        }
        finally
        {
            Directory.Delete(extractDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_WithFilterSecrets_LeavesNothingInTablesNobodyClassified()
    {
        // The filter used to be a deny-list: name the secrets, ship everything else. Tables added
        // by later migrations were therefore shipped WITH THEIR CONTENTS to every joining node
        // because no one went back to add them — including tokens this node issued to remote
        // accounts and the wrapped key for its search index. Now anything outside
        // SnapshotTables.Replicated is emptied, so this asserts on the tables that were leaking
        // rather than on the mechanism.
        // Seed the tables first, or "it came out empty" proves nothing: they start empty.
        SeedLocalOnlyRows();

        var info = await _service.CreateAsync(filterSecrets: true);
        var extractDir = await ExtractSnapshotToTempDirAsync(_service.GetSnapshotPath(info.FileName));
        try
        {
            using var conn = new SqliteConnection(
                $"Data Source={Path.Combine(extractDir, "beememorybank.db")};Pooling=False");
            conn.Open();

            string[] mustBeEmpty = ["tbl_remote_api_token", "tbl_favorite"];

            foreach (var table in mustBeEmpty)
            {
                using var existsCmd = conn.CreateCommand();
                existsCmd.CommandText =
                    $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'";
                if (Convert.ToInt64(existsCmd.ExecuteScalar()) == 0) continue; // not in this schema yet

                using var countCmd = conn.CreateCommand();
                countCmd.CommandText = $"SELECT COUNT(*) FROM [{table}]";
                Convert.ToInt64(countCmd.ExecuteScalar()).Should().Be(0,
                    $"{table} is node-local and must not travel to a peer");
            }

            // The schema itself has to survive: the receiving node never re-runs migrations after
            // an import, so a dropped table would be missing there forever.
            using var schemaCmd = conn.CreateCommand();
            schemaCmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='tbl_remote_api_token'";
            Convert.ToInt64(schemaCmd.ExecuteScalar()).Should().Be(1,
                "emptied, not dropped — see SnapshotTables.StrippedByDropping for what is dropped and why");

            // And the content a peer actually joined for is still there.
            using var contentCmd = conn.CreateCommand();
            contentCmd.CommandText = "SELECT COUNT(*) FROM tbl_article";
            Convert.ToInt64(contentCmd.ExecuteScalar()).Should().BeGreaterThan(0);
        }
        finally
        {
            Directory.Delete(extractDir, recursive: true);
        }
    }

    /// <summary>
    /// Puts a row in each table this test claims gets stripped, plus the article that must survive.
    /// Written as raw SQL because the fixture has repositories, not services — and because the
    /// point is the table contents, not how they got there.
    /// </summary>
    private void SeedLocalOnlyRows()
    {
        var now = DateTime.UtcNow.ToString("O");
        using var conn = _factory.CreateConnection();

        void Exec(string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        Exec($"INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) " +
             $"VALUES ('{Guid.NewGuid()}', 'Replicated content', '/', 'A', '{now}', '{now}')");
        Exec($"INSERT INTO tbl_user (username, password_hash, display_name, role, created_at) " +
             $"VALUES ('seed', 'hash', 'Seed', 'user', '{now}')");
        Exec($"INSERT INTO tbl_remote_api_token (id, user_id, token_hash, label, created_at, expires_at) " +
             $"VALUES ('{Guid.NewGuid()}', (SELECT id FROM tbl_user WHERE username = 'seed'), " +
             $"'deadbeef', 'seed token', '{now}', '{now}')");
        Exec($"INSERT INTO tbl_favorite (user_id, article_id, created_at) " +
             $"VALUES ((SELECT id FROM tbl_user WHERE username = 'seed'), " +
             $"(SELECT id FROM tbl_article LIMIT 1), '{now}')");
    }

    [Fact]
    public async Task CreateAsync_WithSign_ProducesValidSignatureFile()
    {
        var info = await _service.CreateAsync(sign: true);
        info.Signed.Should().BeTrue();

        var snapshotPath = _service.GetSnapshotPath(info.FileName);
        var sigPath = $"{snapshotPath}.sig";
        File.Exists(sigPath).Should().BeTrue();

        var sigBytes = await File.ReadAllBytesAsync(sigPath);
        sigBytes.Length.Should().Be(64);

        var manifestBytes = await ExtractManifestBytesAsync(snapshotPath);
        var payload = await ComputeSignaturePayloadAsync(manifestBytes, snapshotPath);

        Ed25519Signer.Verify(_testPublicKey, payload, sigBytes).Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_WithSign_SignatureFailsWithWrongKey()
    {
        var info = await _service.CreateAsync(sign: true);
        var snapshotPath = _service.GetSnapshotPath(info.FileName);
        var sigPath = $"{snapshotPath}.sig";

        var sigBytes = await File.ReadAllBytesAsync(sigPath);
        var manifestBytes = await ExtractManifestBytesAsync(snapshotPath);
        var payload = await ComputeSignaturePayloadAsync(manifestBytes, snapshotPath);

        var (wrongPubKey, _) = Ed25519Signer.GenerateKeyPair();
        Ed25519Signer.Verify(wrongPubKey, payload, sigBytes).Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_WithCpSequenceNum_ManifestContainsMetadata()
    {
        _clock.Tick();
        _clock.Tick();

        var info = await _service.CreateAsync(cpSequenceNum: 1234);
        info.CpSequenceNum.Should().Be(1234);
        info.ProducerNodeId.Should().Be(_testNodeId);

        var snapshotPath = _service.GetSnapshotPath(info.FileName);
        var manifestJson = await ExtractManifestTextAsync(snapshotPath);
        var manifest = JsonDocument.Parse(manifestJson);

        manifest.RootElement.GetProperty("version").GetInt32().Should().Be(3);
        manifest.RootElement.GetProperty("cpSequenceNum").GetInt64().Should().Be(1234);
        manifest.RootElement.TryGetProperty("lamportTsAtCp", out var lts).Should().BeTrue();
        lts.GetInt64().Should().BeGreaterOrEqualTo(2);
        manifest.RootElement.TryGetProperty("producerNodeId", out var pnid).Should().BeTrue();
        pnid.GetString().Should().Be(_testNodeId.ToString());
        manifest.RootElement.TryGetProperty("migrationVersion", out var mv).Should().BeTrue();
        mv.GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CreateAsync_DefaultBehavior_WorksAsBeforeForLocalBackup()
    {
        // Local-backup scenario: explicit sign:false. Default sign was changed to true at
        // some point along the way — current default is signed-by-default for distribution.
        var info = await _service.CreateAsync(sign: false);
        info.Should().NotBeNull();
        info.FileName.Should().StartWith("bmb-snapshot-");
        info.FileName.Should().EndWith(".tar.gz");
        info.SizeBytes.Should().BeGreaterThan(0);
        info.CpSequenceNum.Should().BeNull();
        info.ProducerNodeId.Should().BeNull();
        info.Signed.Should().BeFalse();

        var snapshotPath = _service.GetSnapshotPath(info.FileName);
        File.Exists(snapshotPath).Should().BeTrue();
        File.Exists($"{snapshotPath}.sig").Should().BeFalse();

        var manifestJson = await ExtractManifestTextAsync(snapshotPath);
        var manifest = JsonDocument.Parse(manifestJson);
        manifest.RootElement.GetProperty("version").GetInt32().Should().Be(1);
        manifest.RootElement.TryGetProperty("cpSequenceNum", out _).Should().BeFalse();
        manifest.RootElement.TryGetProperty("lamportTsAtCp", out _).Should().BeFalse();
        manifest.RootElement.TryGetProperty("producerNodeId", out _).Should().BeFalse();
        manifest.RootElement.TryGetProperty("migrationVersion", out _).Should().BeFalse();
    }

    // ─── Review release-a #1: the working copy of the database is staged in the data folder ───

    [Fact]
    public async Task CreateAsync_StagesInsideTheDataFolder_NotInTheOsTempFolder_AndRemovesTheCopy()
    {
        string? staged = null;
        var additions = new SnapshotAdditions([], path =>
        {
            staged = path;
            File.Exists(path).Should().BeTrue();
            if (!OperatingSystem.IsWindows())
                File.GetUnixFileMode(path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }, new Dictionary<string, byte[]>());

        await _service.CreateAsync(filterSecrets: true, sign: false, additions: additions);

        staged.Should().NotBeNull();
        Path.GetDirectoryName(staged).Should().Be(SnapshotStaging.DirIn(_tempDir),
            "the copy of the whole database is made next to the live one, not in the OS temp folder");
        File.Exists(staged).Should().BeFalse("the copy is removed once the archive is written");
        if (!OperatingSystem.IsWindows())
            (File.GetUnixFileMode(SnapshotStaging.DirIn(_tempDir)) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead))
                .Should().Be(UnixFileMode.None);
    }

    [WindowsAclFact]
    public async Task CreateAsync_OnWindows_TheWorkingCopyIsOwnerOnly_EvenInAFolderEveryAccountCanRead()
    {
        if (!OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_tempDir);
        WindowsAcl.MakeReadableByAllUsers(_tempDir);
        bool? usersCouldRead = null;
        var additions = new SnapshotAdditions([], path => usersCouldRead = WindowsAcl.UsersCanRead(path), new Dictionary<string, byte[]>());

        await _service.CreateAsync(filterSecrets: true, sign: false, additions: additions);

        usersCouldRead.Should().BeFalse();
    }

    [Fact]
    public async Task CreateAsync_WhenItFails_LeavesNoWorkingCopy()
    {
        var additions = new SnapshotAdditions([], _ => throw new IOException("simulated failure"), new Dictionary<string, byte[]>());

        var act = () => _service.CreateAsync(filterSecrets: true, sign: false, additions: additions);

        await act.Should().ThrowAsync<IOException>();
        Directory.GetFiles(SnapshotStaging.DirIn(_tempDir)).Should().BeEmpty();
    }

    [Fact]
    public void StartupSweep_RemovesAbandonedWorkingCopies_AndNothingElse()
    {
        var dir = SnapshotStaging.DirIn(_tempDir);
        var abandoned = SnapshotStaging.NewFile(_tempDir);
        File.WriteAllText(abandoned, "a whole database, as a kill left it");
        File.WriteAllText(abandoned + "-journal", "its journal");
        var other = Path.Combine(dir, "not-a-snapshot.txt");
        File.WriteAllText(other, "someone else's");

        SnapshotStaging.Sweep(_tempDir).Should().Be(2);

        File.Exists(abandoned).Should().BeFalse();
        File.Exists(abandoned + "-journal").Should().BeFalse();
        File.Exists(other).Should().BeTrue("only the snapshot's own files are swept");
    }

    /// <summary>
    /// Review release-a #2: a restore commits the database, then swaps media.staging into media. A kill between the two
    /// left the restored legacy media in media.staging, where nothing reads it, and the next start's orphan sweep then
    /// removed the replaced state's files. The startup resume moves in what the committed tbl_media names, never
    /// overwriting, and leaves the rest where it is.
    /// </summary>
    [Fact]
    public async Task ResumeMediaStaging_AfterARestoreCutBeforeTheMediaSwap_MovesInWhatTheDatabaseNames()
    {
        var restored = Guid.NewGuid();
        var alreadyThere = Guid.NewGuid();
        var notInTheDatabase = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        await InsertLegacyMediaRowAsync(restored);
        await InsertLegacyMediaRowAsync(alreadyThere);
        var media = Path.Combine(_tempDir, "media");
        var staging = Path.Combine(_tempDir, "media.staging");
        Directory.CreateDirectory(media);
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(media, $"{alreadyThere}.enc"), "live");
        await File.WriteAllTextAsync(Path.Combine(media, $"{orphan}.enc"), "replaced state");
        await File.WriteAllTextAsync(Path.Combine(staging, $"{restored}.enc"), "restored");
        await File.WriteAllTextAsync(Path.Combine(staging, $"{alreadyThere}.enc"), "staged copy");
        await File.WriteAllTextAsync(Path.Combine(staging, $"{notInTheDatabase}.enc"), "not named");

        // The startup order: resume first, then the orphan sweep.
        _service.ResumeMediaStaging().Should().Be(1);
        _service.CleanupOrphanMediaFiles();

        (await File.ReadAllTextAsync(Path.Combine(media, $"{restored}.enc"))).Should().Be("restored");
        (await File.ReadAllTextAsync(Path.Combine(media, $"{alreadyThere}.enc"))).Should().Be("live", "nothing in media/ is overwritten");
        File.Exists(Path.Combine(media, $"{orphan}.enc")).Should().BeFalse("a file no row names belongs to the replaced state");
        Directory.GetFiles(staging).Select(Path.GetFileName).Should().BeEquivalentTo(
            [$"{alreadyThere}.enc", $"{notInTheDatabase}.enc"], "what is not moved stays where it is");
    }

    [Fact]
    public async Task ResumeMediaStaging_RemovesAnEmptyStagingFolder_AndIsANoOpWithoutOne()
    {
        _service.ResumeMediaStaging().Should().Be(0);

        var restored = Guid.NewGuid();
        await InsertLegacyMediaRowAsync(restored);
        var staging = Path.Combine(_tempDir, "media.staging");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, $"{restored}.enc"), "restored");

        _service.ResumeMediaStaging().Should().Be(1);

        Directory.Exists(staging).Should().BeFalse();
        File.Exists(Path.Combine(_tempDir, "media", $"{restored}.enc")).Should().BeTrue();
    }

    /// <summary>A media row of the pre-blob-store shape: no ciphertext hash, the bytes only in media/{id}.enc.</summary>
    private async Task InsertLegacyMediaRowAsync(Guid id)
    {
        using var conn = _factory.CreateConnection();
        await Dapper.SqlMapper.ExecuteAsync(conn,
            @"INSERT INTO tbl_media (id, file_name, content_type, file_size, encrypted_dek, dek_iv, iv, created_at, lamport_ts, source_node_id)
              VALUES (@Id, 'legacy.bin', 'application/octet-stream', 3, @Blob, @Blob, @Blob, @Now, 1, @Source)",
            new { Id = id.ToString().ToUpperInvariant(), Blob = new byte[12], Now = DateTime.UtcNow.ToString("O"), Source = _testNodeId.ToString() });
    }

    private static async Task<string> ExtractSnapshotToTempDirAsync(string tarGzPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bmb_snap_verify_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        await using var fs = File.OpenRead(tarGzPath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile) continue;
            var destPath = Path.GetFullPath(Path.Combine(tempDir, entry.Name));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }

        return tempDir;
    }

    private static async Task<byte[]> ExtractManifestBytesAsync(string tarGzPath)
    {
        await using var fs = File.OpenRead(tarGzPath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (entry.Name == "manifest.json")
            {
                using var stream = entry.DataStream!;
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                return ms.ToArray();
            }
        }

        throw new InvalidOperationException("manifest.json not found in snapshot");
    }

    private static async Task<byte[]> ComputeSignaturePayloadAsync(byte[] manifestBytes, string tarGzPath)
    {
        // Mirror SnapshotService.ComputeSignaturePayloadAsync: tag || manifest || file bytes.
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData("BMB-MANIFEST-FILE-V1\0"u8.ToArray());
        hasher.AppendData(manifestBytes);
        await using var fs = File.OpenRead(tarGzPath);
        var buffer = new byte[81920];
        int read;
        while ((read = await fs.ReadAsync(buffer)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
        }
        return hasher.GetHashAndReset();
    }

    private static async Task<string> ExtractManifestTextAsync(string tarGzPath)
    {
        var bytes = await ExtractManifestBytesAsync(tarGzPath);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
