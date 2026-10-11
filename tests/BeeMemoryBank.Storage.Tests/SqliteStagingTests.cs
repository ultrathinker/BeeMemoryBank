using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// The removal of a staging database (<see cref="SqliteStaging.TryCleanup"/>) after a pooled connection touched it: the pool keeps the handle
/// open, so the copy and its WAL must still go, and nothing of them may be left behind.
/// </summary>
public sealed class SqliteStagingTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), $"bmb_sqlite_staging_{Guid.NewGuid():N}");

    public SqliteStagingTests() => Directory.CreateDirectory(_data);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_data, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void A_pooled_connection_that_wrote_a_staging_copy_does_not_keep_the_copy_or_its_WAL_after_cleanup()
    {
        var copy = SnapshotStaging.NewFile(_data);
        // The way a factory on a file path opens it: pooled, in WAL mode, and returned to the pool still open.
        using (var conn = new SqliteConnection($"Data Source={copy}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE t (x); INSERT INTO t VALUES (1);";
            cmd.ExecuteNonQuery();
        }
        File.Exists(copy + "-wal").Should().BeTrue("the control: the WAL is there while the pooled connection is open");

        SqliteStaging.TryCleanup(copy).Should().BeTrue();

        foreach (var name in new[] { copy, copy + "-journal", copy + "-wal", copy + "-shm" }) File.Exists(name).Should().BeFalse(name);
    }
}
