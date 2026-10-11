using System.Security.Cryptography;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Sealed;

/// <summary>Real sealed and plain databases for the page-codec and importer tests: a table, an FTS5 index, rows with random words.</summary>
internal static class SealedFixtures
{
    public static readonly string[] Words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel"];

    public sealed record Db(string Path, byte[] Key, int Rows);

    private const string Schema =
        "CREATE TABLE tbl_article (id INTEGER PRIMARY KEY, title TEXT NOT NULL, body BLOB);" +
        "CREATE INDEX ix_article_title ON tbl_article(title);" +
        "CREATE VIRTUAL TABLE fts_article USING fts5(title);";

    private static void Fill(System.Data.IDbConnection c, int rows, int blobBytes, string? canary)
    {
        c.Execute(Schema);
        using var tx = c.BeginTransaction();
        var random = new Random(7);
        for (var i = 0; i < rows; i++)
        {
            var title = $"{Words[i % Words.Length]} {Words[(i / 3) % Words.Length]} note {i}" + (canary is not null && i % 50 == 0 ? " " + canary : "");
            var body = new byte[blobBytes];
            random.NextBytes(body);
            c.Execute("INSERT INTO tbl_article (title, body) VALUES (@title, @body)", new { title, body }, tx);
            c.Execute("INSERT INTO fts_article (rowid, title) VALUES (last_insert_rowid(), @title)", new { title }, tx);
        }
        tx.Commit();
    }

    /// <summary>A sealed vault file, closed and checkpointed: no -wal, no -shm, no pooled connection left.</summary>
    public static async Task<Db> CreateSealedAsync(string dir, string name, int rows, int blobBytes = 64, string? canary = null)
    {
        var path = System.IO.Path.Combine(dir, name);
        var keys = new InMemoryVaultKeySource();
        var key = keys.GetPageKey(DbPurposes.Vault);
        using (var factory = new DbConnectionFactory(path, keys))
        {
            using (var c = factory.CreateConnection())
            {
                Fill(c, rows, blobBytes, canary);
                c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            }
            await factory.CloseForSealAsync(TimeSpan.FromSeconds(10));
        }
        SqliteConnection.ClearAllPools();
        foreach (var extra in new[] { "-wal", "-shm" })
            if (File.Exists(path + extra) && new FileInfo(path + extra).Length == 0) File.Delete(path + extra);
        return new Db(path, key, rows);
    }

    /// <summary>A plain database file (reserve 0 at SQLite's default page size unless asked), closed, journal mode as asked.</summary>
    public static Db CreatePlain(string dir, string name, int rows, int pageSize = 4096, bool walHeader = false, string? canary = null)
    {
        var path = System.IO.Path.Combine(dir, name);
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            c.Execute($"PRAGMA page_size = {pageSize}");
            if (walHeader) c.Execute("PRAGMA journal_mode=WAL");
            Fill(c, rows, 64, canary);
            if (walHeader) c.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        }
        SqliteConnection.ClearAllPools();
        return new Db(path, [], rows);
    }

    /// <summary>The rows of the vault as one text, in a fixed order, to compare two databases.</summary>
    public static string Dump(System.Data.IDbConnection c) =>
        string.Join("\n", c.Query("SELECT id, title, body FROM tbl_article ORDER BY id")
            .Select(r => $"{(long)r.id}|{(string)r.title}|{Convert.ToHexString(SHA256.HashData((byte[])r.body))}"));

    public static string FtsIds(System.Data.IDbConnection c, string word) =>
        string.Join(",", c.Query<long>("SELECT rowid FROM fts_article WHERE fts_article MATCH @word ORDER BY rowid", new { word }));

    /// <summary>Opens a sealed file read-only through a keyed factory over that very file.</summary>
    public static DbConnectionFactory OpenFactory(string path, byte[] key)
    {
        var keys = new InMemoryVaultKeySource(key, RandomNumberGenerator.GetBytes(32));
        return new DbConnectionFactory(path, keys);
    }
}
