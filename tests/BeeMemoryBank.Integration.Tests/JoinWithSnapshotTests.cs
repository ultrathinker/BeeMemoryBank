using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Integration.Tests;

public class JoinWithSnapshotTests : IAsyncLifetime
{
    private readonly string _producerDir = Path.Combine(Path.GetTempPath(), $"bmb_join_prod_{Guid.NewGuid():N}");
    private readonly string _joinerDir = Path.Combine(Path.GetTempPath(), $"bmb_join_joiner_{Guid.NewGuid():N}");

    private DbConnectionFactory _producerFactory = null!;
    private DbConnectionFactory _joinerFactory = null!;
    private SnapshotService _producerSnapshot = null!;
    private SnapshotService _joinerSnapshot = null!;
    private NodeIdentityRepository _producerNodeRepo = null!;
    private NodeIdentityRepository _joinerNodeRepo = null!;
    private NullLamportClock _producerClock = null!;
    private NullLamportClock _joinerClock = null!;

    private byte[] _producerPubKey = null!;
    private byte[] _producerPrivKey = null!;
    private Guid _producerNodeId;

    private byte[] _joinerPubKey = null!;
    private byte[] _joinerPrivKey = null!;
    private Guid _joinerNodeId;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        Directory.CreateDirectory(_producerDir);
        Directory.CreateDirectory(_joinerDir);

        (_producerPubKey, _producerPrivKey) = Ed25519Signer.GenerateKeyPair();
        _producerNodeId = Guid.NewGuid();
        _producerFactory = DbConnectionFactory.CreateInMemory($"bmb_join_prod_{Guid.NewGuid():N}");
        await new MigrationRunner(_producerFactory).RunMigrationsAsync();
        _producerNodeRepo = new NodeIdentityRepository(_producerFactory);
        _producerClock = new NullLamportClock();
        _producerSnapshot = new SnapshotService(_producerDir, _producerFactory, _producerNodeRepo, _producerClock);

        await _producerNodeRepo.CreateAsync(new NodeIdentity
        {
            NodeId = _producerNodeId,
            DisplayName = "Producer",
            Ed25519PublicKey = _producerPubKey,
            Ed25519PrivateKey = _producerPrivKey,
            CreatedAt = DateTime.UtcNow
        });

        (_joinerPubKey, _joinerPrivKey) = Ed25519Signer.GenerateKeyPair();
        _joinerNodeId = Guid.NewGuid();
        _joinerFactory = DbConnectionFactory.CreateInMemory($"bmb_join_joiner_{Guid.NewGuid():N}");
        await new MigrationRunner(_joinerFactory).RunMigrationsAsync();
        _joinerNodeRepo = new NodeIdentityRepository(_joinerFactory);
        _joinerClock = new NullLamportClock();
        _joinerSnapshot = new SnapshotService(_joinerDir, _joinerFactory, _joinerNodeRepo, _joinerClock);

        await _joinerNodeRepo.CreateAsync(new NodeIdentity
        {
            NodeId = _joinerNodeId,
            DisplayName = "Joiner",
            Ed25519PublicKey = _joinerPubKey,
            Ed25519PrivateKey = _joinerPrivKey,
            CreatedAt = DateTime.UtcNow
        });
    }

    public Task DisposeAsync()
    {
        _producerFactory.Dispose();
        _joinerFactory.Dispose();
        try { Directory.Delete(_producerDir, true); } catch { }
        try { Directory.Delete(_joinerDir, true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RestoreForJoinAsync_ImportsDataTables_AndPreservesNodeIdentity()
    {
        await InsertProducerFolderAndArticleAsync();
        await InsertProducerProjectionMatrixAsync();

        var snapshotInfo = await _producerSnapshot.CreateAsync(
            filterSecrets: true, sign: true, cpSequenceNum: 42);
        var snapshotPath = _producerSnapshot.GetSnapshotPath(snapshotInfo.FileName);
        var sigPath = $"{snapshotPath}.sig";
        var signature = await File.ReadAllBytesAsync(sigPath);

        var (cpSeq, lamportTs) = await _joinerSnapshot.RestoreForJoinAsync(
            snapshotPath, signature, _producerPubKey);

        cpSeq.Should().Be(42);
        lamportTs.Should().BeGreaterOrEqualTo(0);

        using var conn = _joinerFactory.CreateConnection();

        var folderCount = await conn.QuerySingleAsync<int>("SELECT COUNT(*) FROM tbl_folder");
        folderCount.Should().Be(1);

        var articleCount = await conn.QuerySingleAsync<int>("SELECT COUNT(*) FROM tbl_article");
        articleCount.Should().Be(1);

        var bodyCount = await conn.QuerySingleAsync<int>("SELECT COUNT(*) FROM tbl_article_body");
        bodyCount.Should().Be(1);

        var matrixCount = await conn.QuerySingleAsync<int>("SELECT COUNT(*) FROM tbl_projection_matrix");
        matrixCount.Should().Be(1);

        var eventCount = await conn.QuerySingleAsync<int>("SELECT COUNT(*) FROM tbl_event");
        eventCount.Should().Be(0);

        var identityId = await conn.QuerySingleAsync<Guid>("SELECT node_id FROM tbl_node_identity");
        identityId.Should().Be(_joinerNodeId, "node_identity must NOT be overwritten by snapshot import");
    }

    [Fact]
    public async Task RestoreForJoinAsync_BadSignature_Throws()
    {
        await InsertProducerFolderAndArticleAsync();

        var snapshotInfo = await _producerSnapshot.CreateAsync(
            filterSecrets: true, sign: true, cpSequenceNum: 10);
        var snapshotPath = _producerSnapshot.GetSnapshotPath(snapshotInfo.FileName);

        var badSig = new byte[64];
        RandomNumberGenerator.Fill(badSig);

        var act = () => _joinerSnapshot.RestoreForJoinAsync(snapshotPath, badSig, _producerPubKey);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Snapshot signature verification failed");
    }

    [Fact]
    public async Task RestoreForJoinAsync_WrongPublicKey_Throws()
    {
        await InsertProducerFolderAndArticleAsync();

        var snapshotInfo = await _producerSnapshot.CreateAsync(
            filterSecrets: true, sign: true, cpSequenceNum: 10);
        var snapshotPath = _producerSnapshot.GetSnapshotPath(snapshotInfo.FileName);
        var sigPath = $"{snapshotPath}.sig";
        var signature = await File.ReadAllBytesAsync(sigPath);

        var (wrongPubKey, _) = Ed25519Signer.GenerateKeyPair();

        var act = () => _joinerSnapshot.RestoreForJoinAsync(snapshotPath, signature, wrongPubKey);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Snapshot signature verification failed");
    }

    [Fact]
    public async Task RestoreForJoinAsync_Version2_Throws()
    {
        await InsertProducerFolderAndArticleAsync();

        var snapshotInfo = await _producerSnapshot.CreateAsync(filterSecrets: false, sign: true);
        var snapshotPath = _producerSnapshot.GetSnapshotPath(snapshotInfo.FileName);
        var sigPath = $"{snapshotPath}.sig";
        var signature = await File.ReadAllBytesAsync(sigPath);

        var act = () => _joinerSnapshot.RestoreForJoinAsync(snapshotPath, signature, _producerPubKey);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*too old*");
    }

    [Fact]
    public async Task RestoreForJoinAsync_FKViolation_Throws()
    {
        await InsertProducerFolderAndArticleAsync();

        using (var conn = _producerFactory.CreateConnection())
        {
            using var fkOff = conn.CreateCommand();
            fkOff.CommandText = "PRAGMA foreign_keys = OFF";
            fkOff.ExecuteNonQuery();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO tbl_article_body (article_id, ciphertext, iv, encrypted_dek, dek_iv) VALUES (@Id, @Body, @Iv, @Dek, @DekIv)";
            var id = cmd.CreateParameter(); id.ParameterName = "Id"; id.Value = Guid.NewGuid(); cmd.Parameters.Add(id);
            var body = cmd.CreateParameter(); body.ParameterName = "Body"; body.Value = new byte[] { 0xFF }; cmd.Parameters.Add(body);
            var iv = cmd.CreateParameter(); iv.ParameterName = "Iv"; iv.Value = new byte[12]; cmd.Parameters.Add(iv);
            var dek = cmd.CreateParameter(); dek.ParameterName = "Dek"; dek.Value = new byte[48]; cmd.Parameters.Add(dek);
            var dekIv = cmd.CreateParameter(); dekIv.ParameterName = "DekIv"; dekIv.Value = new byte[12]; cmd.Parameters.Add(dekIv);
            cmd.ExecuteNonQuery();

            using var fkOn = conn.CreateCommand();
            fkOn.CommandText = "PRAGMA foreign_keys = ON";
            fkOn.ExecuteNonQuery();
        }

        var snapshotInfo = await _producerSnapshot.CreateAsync(
            filterSecrets: true, sign: true, cpSequenceNum: 10);
        var snapshotPath = _producerSnapshot.GetSnapshotPath(snapshotInfo.FileName);
        var sigPath = $"{snapshotPath}.sig";
        var signature = await File.ReadAllBytesAsync(sigPath);

        var act = () => _joinerSnapshot.RestoreForJoinAsync(snapshotPath, signature, _producerPubKey);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Foreign key violations*");
    }

    [Fact]
    public async Task RestoreForJoinAsync_OrphanMediaCleaned()
    {
        var fakeMediaDir = Path.Combine(_joinerDir, "media");
        Directory.CreateDirectory(fakeMediaDir);
        var fakeGuid = Guid.NewGuid();
        await File.WriteAllBytesAsync(Path.Combine(fakeMediaDir, $"{fakeGuid}.enc"), [1, 2, 3]);

        await InsertProducerFolderAndArticleAsync();

        var snapshotInfo = await _producerSnapshot.CreateAsync(
            filterSecrets: true, sign: true, cpSequenceNum: 10);
        var snapshotPath = _producerSnapshot.GetSnapshotPath(snapshotInfo.FileName);
        var sigPath = $"{snapshotPath}.sig";
        var signature = await File.ReadAllBytesAsync(sigPath);

        await _joinerSnapshot.RestoreForJoinAsync(snapshotPath, signature, _producerPubKey);

        File.Exists(Path.Combine(fakeMediaDir, $"{fakeGuid}.enc")).Should().BeFalse(
            "orphan media file not registered in tbl_media should be deleted after import");
    }

    [Fact]
    public async Task RestoreForJoinAsync_MigrationVersionMismatch_Throws()
    {
        await InsertProducerFolderAndArticleAsync();

        using (var conn = _producerFactory.CreateConnection())
        {
            await conn.ExecuteAsync("DELETE FROM tbl_migration WHERE version > 0");
            await conn.ExecuteAsync("INSERT INTO tbl_migration (version, filename, applied_at, updated_at) VALUES (999, @File, @At, @At)",
                new { File = "999_test.sql", At = DateTime.UtcNow });
        }

        var snapshotInfo = await _producerSnapshot.CreateAsync(
            filterSecrets: true, sign: true, cpSequenceNum: 10);
        var snapshotPath = _producerSnapshot.GetSnapshotPath(snapshotInfo.FileName);
        var sigPath = $"{snapshotPath}.sig";
        var signature = await File.ReadAllBytesAsync(sigPath);

        var act = () => _joinerSnapshot.RestoreForJoinAsync(snapshotPath, signature, _producerPubKey);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*schema version*Upgrade this node first*");
    }

    private async Task InsertProducerFolderAndArticleAsync()
    {
        using var conn = _producerFactory.CreateConnection();
        var folderId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_folder (id, path, name, created_at, updated_at) VALUES (@Id, @Path, @Name, @CreatedAt, @UpdatedAt)",
            new { Id = folderId, Path = "/Test", Name = "Test", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

        var articleId = Guid.NewGuid();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_article (id, title, tree_path, status, embedding_pending, created_at, updated_at) VALUES (@Id, @Title, @Path, @Status, @EmbeddingPending, @CreatedAt, @UpdatedAt)",
            new { Id = articleId, Title = "Test Article", Path = "/Test", Status = "A", EmbeddingPending = false, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

        await conn.ExecuteAsync(
            "INSERT INTO tbl_article_body (article_id, ciphertext, iv, encrypted_dek, dek_iv) VALUES (@Id, @Body, @Iv, @Dek, @DekIv)",
            new { Id = articleId, Body = new byte[] { 1, 2, 3 }, Iv = new byte[12], Dek = new byte[48], DekIv = new byte[12] });
    }

    private async Task InsertProducerProjectionMatrixAsync()
    {
        using var conn = _producerFactory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO tbl_projection_matrix (encrypted_matrix, iv, created_at) VALUES (@Matrix, @Iv, @CreatedAt)",
            new { Matrix = new byte[64], Iv = new byte[12], CreatedAt = DateTime.UtcNow });
    }

    private static async Task ExtractTarGzToDirAsync(string tarGzPath, string destDir)
    {
        await using var fs = File.OpenRead(tarGzPath);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);

        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile) continue;
            var destPath = Path.GetFullPath(Path.Combine(destDir, entry.Name));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }
}
