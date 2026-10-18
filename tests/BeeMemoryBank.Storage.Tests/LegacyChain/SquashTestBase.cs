using System.Data;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>A scratch folder per test class instance (the tests own it and remove it) and the small moves the squash tests share.</summary>
public abstract class SquashTestBase : IDisposable
{
    protected const string BaselineName = "BeeMemoryBank.Storage.Migrations.040_baseline.sql";

    protected string Dir { get; } = Path.Combine(Path.GetTempPath(), "bmb_squash_" + Guid.NewGuid().ToString("N"));

    protected SquashTestBase() => Directory.CreateDirectory(Dir);

    protected string NewDbPath(string name = "beememorybank.db") => Path.Combine(Dir, name);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { /* a handle that Windows still holds; the temp folder is reaped */ }
    }

    /// <summary>A database made by whatever runner the product has now, then closed and flushed: the schema of the final release.</summary>
    protected static async Task<string> CurrentSchemaDbAsync(string path)
    {
        using (var factory = new DbConnectionFactory(path))
            await new MigrationRunner(factory).RunMigrationsAsync();
        SqliteConnection.ClearAllPools();
        return path;
    }

    /// <summary>The ledger a node of release <paramref name="tag"/> has (taken from the version table, no file needed).</summary>
    protected static void SetLedger(IDbConnection conn, IEnumerable<(int Version, string Name)> rows)
    {
        // The downgrade guard (MigrationRunner.DowngradeGuardSql) stops every DELETE from the ledger; a test that forges one drops it first,
        // as the old programs' ghost hunter never could.
        conn.Execute("DROP TRIGGER IF EXISTS trg_migration_ledger_guard");
        conn.Execute("DELETE FROM tbl_migration");
        var now = DateTime.UtcNow.ToString("o");
        foreach (var (version, name) in rows)
            conn.Execute("INSERT INTO tbl_migration (version, filename, applied_at, updated_at) VALUES (@version, @name, @now, @now)",
                new { version, name, now });
    }

    protected static void SetLedger(string path, IEnumerable<(int Version, string Name)> rows)
    {
        using (var factory = new DbConnectionFactory(path))
        using (var conn = factory.CreateConnection())
            SetLedger(conn, rows);
        SqliteConnection.ClearAllPools();
    }

    protected static void Run(string path, string sql)
    {
        using (var factory = new DbConnectionFactory(path))
        using (var conn = factory.CreateConnection())
            conn.Execute(sql);
        SqliteConnection.ClearAllPools();
    }

    protected static T Query<T>(string path, string sql)
    {
        T result;
        using (var factory = new DbConnectionFactory(path))
        using (var conn = factory.CreateConnection())
            result = conn.ExecuteScalar<T>(sql)!;
        SqliteConnection.ClearAllPools();
        return result;
    }

    protected static string ShapeOf(string path)
    {
        string text;
        using (var factory = new DbConnectionFactory(path))
        using (var conn = factory.CreateConnection())
            text = SchemaDump.Shape(conn, withLedger: false);
        SqliteConnection.ClearAllPools();
        return text;
    }

    protected static (string Shape, string Rows, List<(int Version, string Filename)> Ledger) ReadAll(string path)
    {
        (string, string, List<(int, string)>) result;
        using (var factory = new DbConnectionFactory(path))
        using (var conn = factory.CreateConnection())
            result = (SchemaDump.Shape(conn, withLedger: false), SchemaDump.RowsText(conn), SchemaDump.Ledger(conn));
        SqliteConnection.ClearAllPools();
        return result;
    }

    protected static void SeedRows(string path)
    {
        using (var factory = new DbConnectionFactory(path))
        using (var conn = factory.CreateConnection())
            SyntheticRows.Seed(conn);
        SqliteConnection.ClearAllPools();
    }

    protected static async Task RunNewRunnerAsync(string path, ILogger<MigrationRunner>? logger = null)
    {
        using (var factory = new DbConnectionFactory(path))
            await Runner(factory, logger).RunMigrationsAsync();
        SqliteConnection.ClearAllPools();
    }

    // The constructor with a logger arrives with the adoption (commit 2); until then the plain one is the only one there is.
    protected static MigrationRunner Runner(DbConnectionFactory factory, ILogger<MigrationRunner>? logger = null) =>
        RunnerFactory.Create(factory, logger);

    /// <summary>The runner must refuse; the database file must be the same bytes afterwards. Returns what it said.</summary>
    protected static async Task<InvalidOperationException> RefusedUntouchedAsync(string path)
    {
        SqliteConnection.ClearAllPools();
        var before = SchemaDump.FileDigest(path);
        var sizeBefore = new FileInfo(path).Length;
        InvalidOperationException ex;
        using (var factory = new DbConnectionFactory(path))
            ex = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Runner(factory).RunMigrationsAsync());
        SqliteConnection.ClearAllPools();
        new FileInfo(path).Length.Should().Be(sizeBefore, "a refused database is not written to");
        SchemaDump.FileDigest(path).Should().Be(before, "a refused database is left byte for byte as it was");
        return ex;
    }
}

/// <summary>Creates the runner the way the product does today; the one place the logger parameter is passed.</summary>
internal static partial class RunnerFactory
{
    public static MigrationRunner Create(DbConnectionFactory factory, ILogger<MigrationRunner>? logger) => new(factory, logger);
}

/// <summary>What was logged, by level, so a test can count the Information lines.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
