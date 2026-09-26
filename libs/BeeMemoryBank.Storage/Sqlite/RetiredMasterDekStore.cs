using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

/// <inheritdoc cref="IRetiredMasterDekStore"/>
public class RetiredMasterDekStore(DbConnectionFactory factory) : IRetiredMasterDekStore
{
    public IReadOnlyList<(string Name, byte[]? Wrapped, byte[]? Iv)> ListWrapped()
    {
        using var conn = factory.CreateConnection();
        return conn.Query<(string, byte[]?, byte[]?)>(
                $"SELECT key_name, wrapped_key, iv FROM {NodeDataKeyEnvelope.TableName} WHERE key_name LIKE @prefix",
                new { prefix = IRetiredMasterDekStore.KeyNamePrefix + "%" })
            .ToList();
    }
}
