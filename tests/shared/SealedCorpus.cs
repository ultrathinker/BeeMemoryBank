using System.Security.Cryptography;
using BeeMemoryBank.Storage.Sealed;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.TestSupport;

/// <summary>
/// Real SQLite files for the Step B tests (docs/design/step-b-spec.md 15.1.14, 15.1.15): plain and sealed databases, boot stores, and the
/// WAL and rollback-journal files that live connections leave beside them, made by real connections of SQLite3 Multiple Ciphers - never by
/// hand. Every database holds tables whose names match nothing the product has and no canary, so no schema or name heuristic can pass it.
/// </summary>
public static class SealedCorpus
{
    private static string Filler(int i) => "title number " + i + " " + new string('x', 120);

    private static string PlainCs(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path, byte[]? key)
    {
        if (key is not null) return SealedSqlite.OpenKeyed(path, key);
        var c = new SqliteConnection(PlainCs(path));
        c.Open();
        return c;
    }

    /// <summary>A database (sealed under <paramref name="key"/>, or plain when null) with a table and an index, closed and checkpointed.</summary>
    public static void MakeDb(string path, byte[]? key, int rows = 80, int pageSize = 4096)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var c = Open(path, key);
        if (key is null) Exec(c, $"PRAGMA page_size = {pageSize};");
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, "CREATE TABLE zq" + Guid.NewGuid().ToString("N")[..8] + "(id INTEGER PRIMARY KEY, body TEXT); ");
        Exec(c, "CREATE TABLE t(id INTEGER PRIMARY KEY, body TEXT); CREATE INDEX ix ON t(body);");
        using var tx = c.BeginTransaction();
        for (var i = 0; i < rows; i++) Exec(c, "INSERT INTO t(body) VALUES ('" + Filler(i) + "');");
        tx.Commit();
        Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
        Exec(c, "PRAGMA journal_mode=DELETE;");
    }

    /// <summary>Keeps a database open in WAL mode with frames not yet checkpointed and calls <paramref name="whileOpen"/> with the -wal path.</summary>
    public static void WithOpenWal(string path, byte[]? key, Action<string> whileOpen, bool skipPageOne = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var c = Open(path, key);
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, "PRAGMA wal_autocheckpoint=0;");
        Exec(c, "CREATE TABLE IF NOT EXISTS t(id INTEGER PRIMARY KEY, body TEXT);");
        if (skipPageOne)
        {
            for (var i = 0; i < 60; i++) Exec(c, "INSERT INTO t(body) VALUES ('" + Filler(i) + "');");
            Exec(c, "PRAGMA wal_checkpoint(TRUNCATE);");
            Exec(c, "UPDATE t SET body = upper(body) WHERE id % 3 = 0;"); // same length: no growth, so no page-1 frame
        }
        else
        {
            for (var i = 0; i < 40; i++) Exec(c, "INSERT INTO t(body) VALUES ('" + Filler(i) + "');");
        }
        whileOpen(path + "-wal");
    }

    /// <summary>Keeps a database open in DELETE mode inside a transaction that spills, so a rollback journal exists, and calls <paramref name="whileOpen"/> with its path.</summary>
    public static void WithOpenJournal(string path, byte[]? key, Action<string> whileOpen)
    {
        MakeDb(path, key, rows: 400);
        using var c = Open(path, key);
        Exec(c, "PRAGMA journal_mode=DELETE;");
        Exec(c, "PRAGMA cache_size=2;");
        Exec(c, "BEGIN;");
        Exec(c, "UPDATE t SET body = body || 'zz';");
        whileOpen(path + "-journal");
        Exec(c, "ROLLBACK;");
    }

    /// <summary>A plain SQLite file that is a boot store: <c>PRAGMA application_id</c> = "BMBS".</summary>
    public static void MakeBoot(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var c = new SqliteConnection(PlainCs(path));
        c.Open();
        Exec(c, "PRAGMA application_id = " + VaultStoreFiles.BootApplicationId + ";");
        Exec(c, "CREATE TABLE tbl_boot_meta(k TEXT PRIMARY KEY, v TEXT); INSERT INTO tbl_boot_meta VALUES ('node_id','x');");
    }

    /// <summary>A copy of a file that a live connection holds open (shared read).</summary>
    public static void CopyShared(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = new FileStream(to, FileMode.Create, FileAccess.Write);
        src.CopyTo(dst);
    }

    public static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[fs.Length];
        fs.ReadExactly(bytes);
        return bytes;
    }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>The real files made once: a sealed and a plain database, a sealed and a plain chat database, WALs and journals of each kind.</summary>
    public sealed class Pool
    {
        public required string Dir { get; init; }
        public required byte[] Key { get; init; }
        public required string SealedMain { get; init; }
        public required string SealedChat { get; init; }
        public required string PlainMain { get; init; }
        public required string PlainChat { get; init; }
        public required string SealedWal { get; init; }
        public required string PlainWal { get; init; }
        public required string PlainWalNoPageOne { get; init; }
        public required string SealedJournal { get; init; }
        public required string PlainJournal { get; init; }

        public static Pool Create(string root)
        {
            var dir = Path.Combine(root, "pool-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var key = NewKey();
            string P(string n) => Path.Combine(dir, n);
            var pool = new Pool
            {
                Dir = dir, Key = key,
                SealedMain = P("sealed-main.db"), SealedChat = P("sealed-chat.db"), PlainMain = P("plain-main.db"), PlainChat = P("plain-chat.db"),
                SealedWal = P("sealed.wal"), PlainWal = P("plain.wal"), PlainWalNoPageOne = P("plain-nop1.wal"),
                SealedJournal = P("sealed.journal"), PlainJournal = P("plain.journal"),
            };
            MakeDb(pool.SealedMain, key, 300);
            MakeDb(pool.SealedChat, key, 80);
            MakeDb(pool.PlainMain, null, 300);
            MakeDb(pool.PlainChat, null, 80);
            WithOpenWal(P("w1.db"), key, w => CopyShared(w, pool.SealedWal));
            WithOpenWal(P("w2.db"), null, w => CopyShared(w, pool.PlainWal));
            WithOpenWal(P("w3.db"), null, w => CopyShared(w, pool.PlainWalNoPageOne), skipPageOne: true);
            WithOpenJournal(P("j1.db"), key, j => CopyShared(j, pool.SealedJournal));
            WithOpenJournal(P("j2.db"), null, j => CopyShared(j, pool.PlainJournal));
            SqliteConnection.ClearAllPools();
            return pool;
        }
    }
}
