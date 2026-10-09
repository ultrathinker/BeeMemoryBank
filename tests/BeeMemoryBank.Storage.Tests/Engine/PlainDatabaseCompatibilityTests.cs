using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Engine;

/// <summary>
/// 2.5.3 swaps the native SQLite for SQLite3 Multiple Ciphers but sets no key, so a database must stay byte-compatible with
/// 2.5.1 in both directions: a 2.5.3 node opens a vault a 2.5.1 node wrote, and a 2.5.1 node (stock SQLite) opens a vault a
/// 2.5.3 node wrote - during a rolling upgrade, after a restore from an older backup, after a downgrade.
///
/// Fixtures/plain-2.5.1.sqlite was written by the 2.5.1 tree on the stock engine (migrations 1-36, WAL mode, FTS5 tables with
/// their triggers, three articles, two folders, two tags; see Fixtures/README.md). Never regenerate it with a newer engine.
/// The stock engine itself (the e_sqlite3 native file 2.5.1 shipped) is loaded next to the current one by <see cref="StockSqlite"/>.
/// </summary>
public class PlainDatabaseCompatibilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_engine_compat_" + Guid.NewGuid().ToString("N"));

    public PlainDatabaseCompatibilityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string FixtureCopy(string name)
    {
        var path = Path.Combine(_dir, name);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "plain-2.5.1.sqlite"), path);
        return path;
    }

    [Fact]
    public async Task A_vault_written_by_251_opens_and_works_on_the_current_engine()
    {
        var path = FixtureCopy("beememorybank.db");
        var factory = new DbConnectionFactory(path);
        var migrationsBefore = await CountAsync(factory, "SELECT COUNT(*) FROM tbl_migration");

        await new MigrationRunner(factory).RunMigrationsAsync();

        migrationsBefore.Should().BeGreaterThan(30);
        (await CountAsync(factory, "SELECT COUNT(*) FROM tbl_migration")).Should().Be(migrationsBefore,
            "the fixture holds the full 2.5.1 schema: nothing is pending, no 'ghost' migration is re-applied");
        using var conn = factory.CreateConnection();
        (await conn.QuerySingleAsync<string>("PRAGMA integrity_check")).Should().Be("ok");
        (await conn.QueryAsync<string>("PRAGMA foreign_key_check")).Should().BeEmpty();
        (await conn.QuerySingleAsync<int>("SELECT COUNT(*) FROM tbl_article")).Should().Be(3);

        // FTS5 (external content, kept in step by triggers): the 2.5.1 index answers, the trigger path still writes.
        (await conn.QueryAsync<string>("SELECT a.id FROM fts_article f JOIN tbl_article a ON a.rowid = f.rowid WHERE fts_article MATCH 'roadmap'"))
            .Should().Equal("a-1");
        (await conn.QueryAsync<string>("SELECT a.id FROM fts_article f JOIN tbl_article a ON a.rowid = f.rowid WHERE fts_article MATCH 'cafe'"))
            .Should().Equal(new[] { "a-2" }, "the default FTS5 tokenizer folds the accent, as it did on the stock engine");
        (await conn.QueryAsync<string>("SELECT name FROM fts_tag WHERE fts_tag MATCH 'menu*'")).Should().Equal("menu-tag");
        await conn.ExecuteAsync(@"INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at, lamport_ts, source_node_id, folder_id)
            VALUES ('a-4', 'Hiring plan', '/Projects/Hiring plan', 'A', '2026-10-09T11:00:00.0000000Z', '2026-10-09T11:00:00.0000000Z', 4, 'node-b', 'f-1')");
        (await conn.QueryAsync<string>("SELECT a.id FROM fts_article f JOIN tbl_article a ON a.rowid = f.rowid WHERE fts_article MATCH 'hiring'"))
            .Should().Equal("a-4");
        await conn.ExecuteAsync("INSERT INTO fts_article(fts_article) VALUES ('integrity-check')");

        // The two user functions, a snapshot copy and the backup API on a vault the old engine wrote.
        (await conn.QuerySingleAsync<int>("SELECT COUNT(*) FROM tbl_article WHERE unicode_contains(title, 'ROADMAP')")).Should().Be(1);
        var copy = Path.Combine(_dir, "copy.db");
        await conn.ExecuteAsync($"VACUUM INTO '{copy.Replace("'", "''")}'");
        using (var target = new SqliteConnection($"Data Source={Path.Combine(_dir, "backup.db")};Pooling=False"))
        {
            target.Open();
            ((SqliteConnection)conn).BackupDatabase(target);
            (await target.QuerySingleAsync<int>("SELECT COUNT(*) FROM tbl_article")).Should().Be(4);
        }
        (await ReadHeaderAsync(copy)).Take(16).Should().Equal("SQLite format 3\0"u8.ToArray());
    }

    [Fact]
    public async Task A_vault_written_by_the_current_engine_opens_and_works_on_stock_sqlite_251()
    {
        var path = Path.Combine(_dir, "beememorybank.db");
        await WriteVaultAsync(path);

        using (var stock = StockSqlite.Open(path))
        {
            stock.Scalar("PRAGMA integrity_check").Should().Be("ok");
            stock.Scalar("PRAGMA journal_mode").Should().Be("wal");
            stock.Scalar("SELECT COUNT(*) FROM tbl_article").Should().Be("3");
            stock.Query("SELECT a.id FROM fts_article f JOIN tbl_article a ON a.rowid = f.rowid WHERE fts_article MATCH 'roadmap'")
                .Select(r => r[0]).Should().Equal("a-1");
            // A 2.5.1 node writes into the same file: the FTS triggers fire, the freelist and the WAL work.
            stock.Execute(@"INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at, lamport_ts, source_node_id, folder_id)
                VALUES ('a-stock', 'Written by stock', '/Projects/Written by stock', 'A', '2026-10-09T12:00:00.0000000Z', '2026-10-09T12:00:00.0000000Z', 9, 'node-old', 'f-1')");
            stock.Execute("INSERT INTO fts_article(fts_article) VALUES ('integrity-check')");
            stock.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        }

        var factory = new DbConnectionFactory(path);
        using var conn = factory.CreateConnection();
        (await conn.QuerySingleAsync<string>("PRAGMA integrity_check")).Should().Be("ok");
        (await conn.QueryAsync<string>("SELECT a.id FROM fts_article f JOIN tbl_article a ON a.rowid = f.rowid WHERE fts_article MATCH 'stock'"))
            .Should().Equal(new[] { "a-stock" }, "a row the stock engine wrote and indexed is found by the current engine");
        await conn.ExecuteAsync("INSERT INTO fts_article(fts_article) VALUES ('integrity-check')");
    }

    [Fact]
    public async Task A_vault_written_by_251_is_read_by_stock_sqlite_after_the_current_engine_changed_it()
    {
        // The downgrade path: a 2.5.1 vault goes through 2.5.3 and back to 2.5.1.
        var path = FixtureCopy("beememorybank.db");
        var factory = new DbConnectionFactory(path);
        using (var conn = factory.CreateConnection())
        {
            await conn.ExecuteAsync(@"INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at, lamport_ts, source_node_id, folder_id)
                VALUES ('a-new', 'Changed by 2.5.3', '/Projects/Changed by 2.5.3', 'A', '2026-10-09T11:00:00.0000000Z', '2026-10-09T11:00:00.0000000Z', 5, 'node-new', 'f-1')");
            await conn.ExecuteAsync("DELETE FROM tbl_article WHERE id = 'a-3'");
            await conn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)");
        }
        factory.Dispose();

        using var stock = StockSqlite.Open(path);
        stock.Scalar("PRAGMA integrity_check").Should().Be("ok");
        stock.Query("SELECT id FROM tbl_article ORDER BY id").Select(r => r[0]).Should().Equal("a-1", "a-2", "a-new");
        stock.Query("SELECT a.id FROM fts_article f JOIN tbl_article a ON a.rowid = f.rowid WHERE fts_article MATCH 'changed'")
            .Select(r => r[0]).Should().Equal("a-new");
        stock.Execute("INSERT INTO fts_article(fts_article) VALUES ('integrity-check')");
    }

    [Fact]
    public async Task The_file_header_the_current_engine_writes_matches_the_one_251_wrote()
    {
        var old = await ReadHeaderAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "plain-2.5.1.sqlite"));
        var path = Path.Combine(_dir, "beememorybank.db");
        await WriteVaultAsync(path);
        var current = await ReadHeaderAsync(path);

        // 0-15 magic; 16-17 page size; 18 write format (2 = WAL); 19 read format; 20 reserved bytes per page (a cipher would use
        // 16-80); 21-23 payload fractions; 44-47 schema format; 56-59 text encoding (1 = UTF-8); 68-71 application id.
        current.Take(24).Should().Equal(old.Take(24));
        current.Skip(44).Take(4).Should().Equal(old.Skip(44).Take(4));
        current.Skip(56).Take(4).Should().Equal(old.Skip(56).Take(4));
        current.Skip(68).Take(4).Should().Equal(old.Skip(68).Take(4));
        current[18].Should().Be(2);
        current[20].Should().Be(0);
    }

    [Fact]
    public async Task A_snapshot_made_with_VACUUM_INTO_stays_plain_and_opens_on_stock_sqlite()
    {
        var path = FixtureCopy("beememorybank.db");
        var factory = new DbConnectionFactory(path);
        var snapshot = Path.Combine(_dir, "snapshot.db");
        using (var conn = factory.CreateConnection())
            await conn.ExecuteAsync($"VACUUM INTO '{snapshot.Replace("'", "''")}'");
        factory.Dispose();

        (await ReadHeaderAsync(snapshot)).Take(16).Should().Equal("SQLite format 3\0"u8.ToArray());
        using var stock = StockSqlite.Open(snapshot);
        stock.Scalar("PRAGMA integrity_check").Should().Be("ok");
        stock.Scalar("SELECT COUNT(*) FROM tbl_article").Should().Be("3");
    }

    [Fact]
    public void The_stock_engine_the_compatibility_tests_use_is_the_one_251_shipped()
    {
        // e_sqlite3 2.1.x (SQLite 3.5x): not the SQLite3 Multiple Ciphers library the product loads now.
        StockSqlite.LibraryVersion.Should().StartWith("3.");
        using var stock = StockSqlite.Open(FixtureCopy("beememorybank.db"));
        var act = () => stock.Scalar("SELECT sqlite3mc_version()");
        act.Should().Throw<InvalidOperationException>().WithMessage("*no such function*");
    }

    /// <summary>A vault made from scratch by the current engine: the whole schema, rows in every FTS5 table, WAL mode, checkpointed.</summary>
    private static async Task WriteVaultAsync(string path)
    {
        var factory = new DbConnectionFactory(path);
        await new MigrationRunner(factory).RunMigrationsAsync();
        using (var conn = factory.CreateConnection())
        {
            const string now = "2026-10-09T10:00:00.0000000Z";
            await conn.ExecuteAsync("INSERT INTO tbl_folder (id, path, name, parent_path, created_at, updated_at) VALUES ('f-1', '/Projects', 'Projects', NULL, @now, @now)", new { now });
            foreach (var (id, title, lamport) in new[] { ("a-1", "Quarterly roadmap", 1), ("a-2", "Cafe menu ideas", 2), ("a-3", "Archived draft", 3) })
                await conn.ExecuteAsync(@"INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at, lamport_ts, source_node_id, folder_id)
                    VALUES (@id, @title, @path, 'A', @now, @now, @lamport, 'node-a', 'f-1')", new { id, title, path = "/Projects/" + title, now, lamport });
            await conn.ExecuteAsync("INSERT INTO tbl_concept_tag (id, name) VALUES (1, 'roadmap-tag')");
            await conn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)");
        }
        factory.Dispose();
    }

    private static async Task<int> CountAsync(DbConnectionFactory factory, string sql)
    {
        using var conn = factory.CreateConnection();
        return await conn.QuerySingleAsync<int>(sql);
    }

    private static async Task<byte[]> ReadHeaderAsync(string path)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[100];
        await fs.ReadExactlyAsync(header);
        return header;
    }
}
