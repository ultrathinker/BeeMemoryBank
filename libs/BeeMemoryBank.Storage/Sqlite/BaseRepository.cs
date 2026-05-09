using System.Data;

namespace BeeMemoryBank.Storage.Sqlite;

public abstract class BaseRepository(DbConnectionFactory factory)
{
    protected readonly DbConnectionFactory Factory = factory;

    protected static string UtcNow() => DateTime.UtcNow.ToString("o");

    protected IDbConnection OpenConnection() => Factory.CreateConnection();
}
