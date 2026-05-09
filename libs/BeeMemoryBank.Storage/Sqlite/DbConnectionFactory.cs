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

    public IDbConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        connection.CreateFunction("unicode_contains", (string? text, string? search) =>
            text != null && search != null && text.Contains(search, StringComparison.OrdinalIgnoreCase));
        return connection;
    }

    public void Dispose()
    {
        _keepAlive?.Dispose();
        _keepAlive = null;
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
