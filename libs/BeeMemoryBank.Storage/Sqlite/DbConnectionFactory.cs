using BeeMemoryBank.Core.Interfaces;
using Microsoft.Data.Sqlite;
using System.Data;

namespace BeeMemoryBank.Storage.Sqlite;

public class DbConnectionFactory : IDbConnectionFactory, IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _keepAlive; // keeps in-memory DB alive

    public DbConnectionFactory(string path)
    {
        string dbPath;
        if (Path.GetExtension(path)?.Equals(".db", StringComparison.OrdinalIgnoreCase) == true)
        {
            dbPath = path;
        }
        else
        {
            dbPath = Path.Combine(path, "beememorybank.db");
        }

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _connectionString = $"Data Source={dbPath}";
    }

    private DbConnectionFactory(string connectionString, bool _)
    {
        _connectionString = connectionString;
    }

    private string? _tempFilePath;

    /// <summary>
    /// Creates a factory backed by a temporary file SQLite database (for tests).
    /// Note: was previously shared-cache in-memory (Mode=Memory;Cache=Shared) but that does
    /// NOT support VACUUM INTO (silently produces an empty target file), which broke any test
    /// going through SnapshotService.CreateAsync. /tmp is typically tmpfs on Linux so the
    /// performance hit is negligible. The file is auto-deleted on Dispose.
    /// </summary>
    public static DbConnectionFactory CreateInMemory(string name = "bmb_test")
    {
        var path = Path.Combine(Path.GetTempPath(), $"bmb_test_{name}_{Guid.NewGuid():N}.db");
        var cs = $"Data Source={path}";
        var factory = new DbConnectionFactory(cs, true) { _tempFilePath = path };
        factory._keepAlive = new SqliteConnection(cs);
        factory._keepAlive.Open();
        return factory;
    }

    /// <summary>The connection string is what actually distinguishes one database from
    /// another here, and it is not a secret (a local file path or a temp path).</summary>
    public string DatabaseId => _connectionString;

    public IDbConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        connection.CreateFunction("unicode_contains", (string? text, string? search) =>
            text != null && search != null && text.Contains(search, StringComparison.OrdinalIgnoreCase));
        // Content addressing for tbl_blob. SQLite ships no hash function, and migration 016 has to
        // key existing article bodies and versions by the hash of their ciphertext to fold the
        // duplicates together — that is not expressible in plain SQL, and this project runs
        // migrations only through the app, so registering the function here is the way to keep the
        // migration a .sql file like every other one. Deterministic and pure, as SQLite requires of
        // a function usable in an index or a constraint.
        //
        // Returns lowercase hex rather than a BLOB so hashes stay greppable in a sqlite3 shell and
        // compare as ordinary TEXT — the volume is 64 bytes per row against bodies measured in
        // kilobytes, so the encoding overhead is irrelevant here.
        connection.CreateFunction("sha256", (byte[]? data) =>
            data == null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant());
        return connection;
    }

    private bool _disposed;

    public void Dispose()
    {
        // Idempotent: the same instance is registered under both DbConnectionFactory and
        // IDbConnectionFactory, so the container can capture and dispose it twice.
        if (_disposed) return;
        _disposed = true;

        _keepAlive?.Dispose();
        _keepAlive = null;

        // Disposing a SqliteConnection only returns it to the pool; the native handle stays open on
        // the database file. Every connection this factory ever handed out is still pooled under
        // _connectionString, so without this the file remains locked after the factory is gone.
        //
        // On Linux that is harmless (unlink works on an open file) and the process usually exits
        // anyway. In-process it is not: the temp-file deletes below silently failed and leaked a DB
        // per test, and a CLI test that removed its data directory after the command returned got
        // IOException on beememorybank.db. Both looked like flakes and were a leaked handle.
        try { SqliteConnection.ClearPool(new SqliteConnection(_connectionString)); } catch { }

        if (_tempFilePath != null)
        {
            // SQLite WAL leftover side files: -wal, -shm, -journal
            foreach (var ext in new[] { "", "-wal", "-shm", "-journal" })
            {
                try { if (File.Exists(_tempFilePath + ext)) File.Delete(_tempFilePath + ext); } catch { }
            }
        }
    }
}
