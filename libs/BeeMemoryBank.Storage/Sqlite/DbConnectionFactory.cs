using Microsoft.Data.Sqlite;
using System.Data;

namespace BeeMemoryBank.Storage.Sqlite;

public class DbConnectionFactory : IDisposable
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

    /// <summary>
    /// Creates a factory with an in-memory SQLite database (for tests).
    /// Keeps the connection open so the DB is not destroyed between requests.
    /// </summary>
    public static DbConnectionFactory CreateInMemory(string name = "bmb_test")
    {
        var cs = $"Data Source={name};Mode=Memory;Cache=Shared";
        var factory = new DbConnectionFactory(cs, true);
        factory._keepAlive = new SqliteConnection(cs);
        factory._keepAlive.Open();
        return factory;
    }

    public IDbConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        connection.CreateFunction("unicode_contains", (string? text, string? search) =>
            text != null && search != null && text.Contains(search, StringComparison.OrdinalIgnoreCase));
        return connection;
    }

    public void Dispose()
    {
        _keepAlive?.Dispose();
        _keepAlive = null;
    }
}
