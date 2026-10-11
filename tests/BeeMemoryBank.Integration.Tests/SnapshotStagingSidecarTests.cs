using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// What a snapshot leaves in the staging folder when SQLite has made sidecars beside its working copy (the rollback journal, the WAL and its shared
/// memory, which hold pages of the clear database): none may survive a successful snapshot, and one that cannot be removed fails the snapshot
/// (repair-required), never a snapshot reported as made. The sidecars are planted, or held open, where the working copy is removed.
/// </summary>
public class SnapshotStagingSidecarTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"bmb_sidecar_{Guid.NewGuid():N}");
    private DbConnectionFactory _factory = null!;
    private SnapshotService _service = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_sidecar_{Guid.NewGuid():N}");
        await new MigrationRunner(_factory).RunMigrationsAsync();

        var nodeRepo = new NodeIdentityRepository(_factory);
        _service = new SnapshotService(_tempDir, _factory, nodeRepo, new NullLamportClock(), keys: new SessionSnapshotKeyOperations(null));
        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        await nodeRepo.CreateAsync(new NodeIdentity
        {
            NodeId = Guid.NewGuid(),
            DisplayName = "SidecarNode",
            Ed25519PublicKey = publicKey,
            Ed25519PrivateKey = privateKey,
            CreatedAt = DateTime.UtcNow
        });
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_snapshot_leaves_none_of_the_sidecars_its_working_copy_had()
    {
        _service.AfterStagingWork = PlantClearSidecars;

        var info = await _service.CreateAsync(encryptDb: false);

        info.Should().NotBeNull();
        StagingEntries().Should().BeEmpty("the working copy and each sidecar of it are removed");
    }

    [Fact]
    public async Task A_pooled_connection_left_on_the_working_copy_does_not_keep_it_or_its_WAL_after_the_snapshot()
    {
        _service.AfterStagingWork = copy =>
        {
            // The way a factory on a file path opens it: pooled, in WAL mode, and returned to the pool still open.
            using var conn = new SqliteConnection($"Data Source={copy}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE probe (x); INSERT INTO probe VALUES (1);";
            cmd.ExecuteNonQuery();
        };

        var info = await _service.CreateAsync(encryptDb: false);

        info.Should().NotBeNull();
        StagingEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task A_sidecar_that_cannot_be_removed_fails_the_snapshot_and_is_never_reported_as_made()
    {
        // A folder under the name of a sidecar stands for a sidecar that cannot be deleted, on every platform.
        _service.AfterStagingWork = copy => Directory.CreateDirectory(copy + "-journal");

        var act = () => _service.CreateAsync(encryptDb: false);

        await act.Should().ThrowAsync<IOException>("the working copy is still on the disk: the snapshot requires repair");
        foreach (var dir in StagingEntries().Where(Directory.Exists)) Directory.Delete(dir, recursive: true);
    }

    private static void PlantClearSidecars(string copy)
    {
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" }) File.WriteAllText(copy + suffix, "clear vault pages");
    }

    private List<string> StagingEntries() =>
        Directory.Exists(SnapshotStaging.DirIn(_tempDir))
            ? Directory.EnumerateFileSystemEntries(SnapshotStaging.DirIn(_tempDir)).ToList()
            : [];
}
