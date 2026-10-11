using System.Security.Cryptography;
using BeeMemoryBank.Cli.Commands;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// The CLI writers of a snapshot remove their working copy and the SQLite sidecars of it: a sidecar that cannot be removed fails the command
/// (repair-required) and the snapshot is never reported as made. The sidecars are planted, or held, where the working copy is removed.
/// </summary>
public sealed class SnapshotCommandStagingTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_clistaging_" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync() => await InitCommand.HandleAsync(_dir, "StagingNode", "stagingPassword");

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_plain_snapshot_leaves_none_of_the_sidecars_its_working_copy_had()
    {
        var output = new StringWriter();

        var code = await SnapshotCommand.HandleCreateAsync(_dir, output, PlantClearSidecars);

        code.Should().Be(0, output.ToString());
        StagingEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task A_plain_snapshot_whose_sidecar_cannot_be_removed_fails_and_is_not_reported_as_made()
    {
        var output = new StringWriter();

        var code = await SnapshotCommand.HandleCreateAsync(_dir, output, copy => Directory.CreateDirectory(copy + "-journal"));

        code.Should().Be(1, output.ToString());
        output.ToString().Should().Contain("repair").And.NotContain("Snapshot created");
        foreach (var dir in StagingEntries().Where(Directory.Exists)) Directory.Delete(dir, recursive: true);
    }

    private static void PlantClearSidecars(string copy)
    {
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" }) File.WriteAllText(copy + suffix, "clear vault pages");
    }

    private List<string> StagingEntries() =>
        Directory.Exists(SnapshotStaging.DirIn(_dir))
            ? Directory.EnumerateFileSystemEntries(SnapshotStaging.DirIn(_dir)).ToList()
            : [];
}

/// <summary>The sealed writer of <c>bmb snapshot create</c> (<see cref="SnapshotCommand.CreateSealedSnapshotAsync"/>) with the same sidecar rules.</summary>
public sealed class SealedSnapshotCommandStagingTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "bmb_clisealedstaging_" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryVaultKeySource _keys = new();
    private readonly byte[] _masterDek = RandomNumberGenerator.GetBytes(32);
    private readonly DbConnectionFactory _vault;

    public SealedSnapshotCommandStagingTests()
    {
        Directory.CreateDirectory(_data);
        _vault = new DbConnectionFactory(_data, _keys);
        using var conn = (SqliteConnection)_vault.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE tbl_article (id INTEGER PRIMARY KEY, title TEXT); INSERT INTO tbl_article (title) VALUES ('note');";
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _vault.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_data, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task A_sealed_snapshot_leaves_none_of_the_sidecars_its_staging_copy_had()
    {
        var result = await SnapshotCommand.CreateSealedSnapshotAsync(_data, _vault, _keys, _masterDek, "node-1", "Test node",
            afterStagingWork: PlantClearSidecars);

        File.Exists(result.Path).Should().BeTrue();
        StagingEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task A_sealed_snapshot_whose_sidecar_cannot_be_removed_throws_and_is_not_reported_as_made()
    {
        var act = () => SnapshotCommand.CreateSealedSnapshotAsync(_data, _vault, _keys, _masterDek, "node-1", "Test node",
            afterStagingWork: copy => Directory.CreateDirectory(copy + "-journal"));

        await act.Should().ThrowAsync<IOException>();
        foreach (var dir in StagingEntries().Where(Directory.Exists)) Directory.Delete(dir, recursive: true);
    }

    private static void PlantClearSidecars(string copy)
    {
        foreach (var suffix in new[] { "-journal", "-wal", "-shm" }) File.WriteAllText(copy + suffix, "clear vault pages");
    }

    private List<string> StagingEntries() =>
        Directory.Exists(SnapshotStaging.DirIn(_data))
            ? Directory.EnumerateFileSystemEntries(SnapshotStaging.DirIn(_data)).ToList()
            : [];
}
