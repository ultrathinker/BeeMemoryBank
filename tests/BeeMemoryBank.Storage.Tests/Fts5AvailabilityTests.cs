using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Throwaway-style probe (kept as a permanent guard) that confirms the bundled
/// SQLitePCLRaw.lib.e_sqlite3 native build used by DbConnectionFactory ships with
/// the FTS5 extension compiled in. WP-06 requires this be verified before any
/// FTS5 migration is written. If this test ever fails on a given platform, FTS5
/// is genuinely unavailable and the whole FTS5 search story must be reconsidered.
/// </summary>
public class Fts5AvailabilityTests
{
    [Fact]
    public async Task Fts5_IsAvailable_InBundledSqlite()
    {
        using var factory = DbConnectionFactory.CreateInMemory($"bmb_fts5_probe_{Guid.NewGuid():N}");
        using var conn = factory.CreateConnection();

        // Minimal FTS5 virtual table. If the extension is absent this throws
        // "no such module: fts5" (SQLITE_ERROR).
        await conn.ExecuteAsync("CREATE VIRTUAL TABLE fts5_probe USING fts5(x)");

        await conn.ExecuteAsync("INSERT INTO fts5_probe(x) VALUES ('hello world')");

        var hit = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT x FROM fts5_probe WHERE fts5_probe MATCH 'hello' LIMIT 1");
        hit.Should().Be("hello world");

        // Also surface the SQLite version + compile options in the test output for the report.
        var version = await conn.QuerySingleAsync<string>("SELECT sqlite_version()");
        version.Should().NotBeNullOrEmpty();
        var compileOptions = (await conn.QueryAsync<string>("PRAGMA compile_options")).ToList();
        compileOptions.Should().Contain(o => o.StartsWith("ENABLE_FTS5"),
            "FTS5 just worked above; the compile_options row confirms it is statically enabled, not loaded at runtime");
    }
}
