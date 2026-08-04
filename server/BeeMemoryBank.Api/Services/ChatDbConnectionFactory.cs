using System.Data;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Connection factory for the chat database (<c>{dataPath}/chat.db</c>).
///
/// This is a DISTINCT type from <c>BeeMemoryBank.Storage.DbConnectionFactory</c> (which is
/// DI-injected everywhere pointing at <c>beememorybank.db</c>). The two must NOT collide in
/// the DI container: this type intentionally does NOT implement
/// <c>Core.Interfaces.IDbConnectionFactory</c> and is registered by its concrete type only.
///
/// The chat DB is node-local only: never touched by <c>MigrationRunner</c>,
/// <c>EventLogger</c>/<c>EventApplier</c>, <c>SnapshotService</c>/<c>RestoreInitiatorService</c>,
/// or <c>SyncClient</c>. See <c>docs/ai-chat-implementation-plan.md</c> §1 ("Chat DB").
/// </summary>
public sealed class ChatDbConnectionFactory : IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection _keepAlive;

    public ChatDbConnectionFactory(string dataPath)
    {
        Directory.CreateDirectory(dataPath);
        var dbPath = Path.Combine(dataPath, "chat.db");
        _connectionString = $"Data Source={dbPath}";
        // A held-open connection keeps WAL mode's -wal/-shm sidecars valid and prevents SQLite
        // from downgrading journal_mode when the last connection closes (matches DbConnectionFactory
        // keep-alive semantics; cheap, stateless from the app's perspective).
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public IDbConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return connection;
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
    }
}
