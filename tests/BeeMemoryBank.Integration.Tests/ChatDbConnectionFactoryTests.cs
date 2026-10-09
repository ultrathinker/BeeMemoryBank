using BeeMemoryBank.Api.Services;
using Dapper;

namespace BeeMemoryBank.Integration.Tests;

public class ChatDbConnectionFactoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_chatdb_factory_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Chat_connections_keep_temp_files_on_disk_as_the_main_database_does()
    {
        // See Storage.Tests Engine/NativeEngineTests: SQLite3 Multiple Ciphers defaults to in-memory temp storage, the stock engine to files.
        using var factory = new ChatDbConnectionFactory(_dir);
        using var conn = factory.CreateConnection();

        conn.QuerySingle<int>("PRAGMA temp_store").Should().Be(1, "1 = FILE");
        conn.QuerySingle<string>("PRAGMA journal_mode").Should().Be("wal");
    }
}
