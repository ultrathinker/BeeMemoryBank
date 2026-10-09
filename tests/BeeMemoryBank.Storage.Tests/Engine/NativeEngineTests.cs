using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests.Engine;

/// <summary>
/// Guards the native SQLite every node loads. Since 2.5.3 it is SQLite3 Multiple Ciphers (SQLite3MC.PCLRaw.bundle), used
/// without a key: every database stays a plain SQLite file. These tests fail if a package change brings back another engine
/// (the build still passes then, because the managed API is the same) or a native SQLite older than the CVE-2025-6965 fix
/// (the floor Storage.csproj has always named).
/// </summary>
public class NativeEngineTests
{
    /// <summary>First SQLite release with the CVE-2025-6965 fix (memory corruption before 3.50.2).</summary>
    private static readonly Version SqliteFloor = new(3, 50, 2);

    [Fact]
    public async Task The_loaded_native_library_is_SQLite3_Multiple_Ciphers()
    {
        using var factory = DbConnectionFactory.CreateInMemory("engine_guard");
        using var conn = factory.CreateConnection();

        var version = await conn.QuerySingleAsync<string>("SELECT sqlite3mc_version()");

        version.Should().StartWith("SQLite3 Multiple Ciphers ");
    }

    [Fact]
    public async Task The_native_sqlite_is_not_older_than_the_CVE_2025_6965_fix()
    {
        using var factory = DbConnectionFactory.CreateInMemory("engine_floor");
        using var conn = factory.CreateConnection();

        var version = Version.Parse(await conn.QuerySingleAsync<string>("SELECT sqlite_version()"));

        version.Should().BeGreaterThanOrEqualTo(SqliteFloor);
    }

    [Fact]
    public async Task Fts5_and_wal_are_available_in_the_native_build()
    {
        using var factory = DbConnectionFactory.CreateInMemory("engine_fts5");
        using var conn = factory.CreateConnection();

        (await conn.QuerySingleAsync<string>("PRAGMA journal_mode")).Should().Be("wal");
        var options = (await conn.QueryAsync<string>("PRAGMA compile_options")).ToList();
        options.Should().Contain(o => o.StartsWith("ENABLE_FTS5"));
    }

    [Fact]
    public async Task No_key_is_set_so_the_file_is_a_plain_sqlite_database()
    {
        using var factory = DbConnectionFactory.CreateInMemory("engine_plain");
        string path;
        using (var conn = factory.CreateConnection())
        {
            await conn.ExecuteAsync("CREATE TABLE probe (x TEXT); INSERT INTO probe VALUES ('plain-marker-4f1c')");
            await conn.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)");
            path = factory.DatabaseId["Data Source=".Length..];
        }

        var bytes = await ReadSharedAsync(path);
        System.Text.Encoding.ASCII.GetString(bytes, 0, 15).Should().Be("SQLite format 3");
        bytes[15].Should().Be(0);
        System.Text.Encoding.UTF8.GetString(bytes).Should().Contain("plain-marker-4f1c",
            "no cipher is configured, so the page content is readable in the file");
        bytes[20].Should().Be(0, "no per-page space is reserved for a cipher's nonce or MAC");
    }

    [Fact]
    public async Task Connections_keep_temp_files_on_disk_as_they_did_on_the_stock_engine()
    {
        // The stock native build is compiled with SQLITE_TEMP_STORE=1 (temp files on disk by default); SQLite3 Multiple Ciphers with 2
        // (in memory by default). An in-place VACUUM or a large sort would then hold the whole database in RAM (VACUUM of a vault that
        // shrinks from 351 to 175 MiB: 257 MiB peak working set, 57 MiB with FILE; measured). 2.5.3 changes the engine, not the
        // memory profile, so the factory asks for the old default.
        using var factory = DbConnectionFactory.CreateInMemory("engine_temp_store");
        using var conn = factory.CreateConnection();

        (await conn.QuerySingleAsync<int>("PRAGMA temp_store")).Should().Be(1, "1 = FILE");
    }

    [Fact]
    public async Task The_two_user_functions_work()
    {
        using var factory = DbConnectionFactory.CreateInMemory("engine_functions");
        using var conn = factory.CreateConnection();

        (await conn.QuerySingleAsync<string>("SELECT sha256(CAST('abc' AS BLOB))"))
            .Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        (await conn.QuerySingleAsync<int>("SELECT unicode_contains('Hello World', 'WORLD')")).Should().Be(1);
    }

    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        await fs.CopyToAsync(ms);
        return ms.ToArray();
    }
}
