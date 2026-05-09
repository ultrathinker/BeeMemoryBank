using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
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
        _service = new SnapshotService(_tempDir, _factory, _nodeRepo, _clock);

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

            using var conn = new SqliteConnection($"Data Source={dbPath}");
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
