using BeeMemoryBank.Api.Services;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Review L-merge round 3 #3: a connection opened in the window between the pre-swap pool clear and the
/// file swap — a request allowed through maintenance, a background reader — went back to the pool still
/// open on the file just moved away. Reused after the swap it read the old database and failed its writes
/// as read-only (SQLITE_READONLY_DBMOVED): the reseed that failed only under load. The racing open is put
/// inside the swap action here, so the window is hit every time instead of by chance.
/// </summary>
public sealed class SnapshotFileSwapPoolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb-swap-pool-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task AConnectionOpenedDuringTheSwap_DoesNotOutliveIt()
    {
        // Windows cannot rename a database file a pooled connection holds open: the swap fails and the
        // wrapper retries after clearing the pool, so this window only exists where renaming an open
        // file succeeds (Linux, where blind nodes run the cutover).
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(_dir);
        var live = Path.Combine(_dir, "live.db");
        var next = Path.Combine(_dir, "next.db");
        Create(live, "old");
        Create(next, "new");
        var pooled = $"Data Source={live}";

        await SnapshotService.SwapDbFileWithRetryAsync(() =>
        {
            using (var reader = new SqliteConnection(pooled))
            {
                reader.Open();
                reader.ExecuteScalar<string>("SELECT v FROM t");
            }
            File.Move(live, Path.Combine(_dir, "moved-away.db"));
            File.Move(next, live);
        }, SqliteConnection.ClearAllPools);

        using var after = new SqliteConnection(pooled);
        after.Open();
        after.ExecuteScalar<string>("SELECT v FROM t").Should().Be("new", "a connection from before the swap must not be handed out after it");
    }

    private static void Create(string path, string value)
    {
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        conn.Execute("CREATE TABLE t (v TEXT); INSERT INTO t (v) VALUES (@value)", new { value });
    }
}
