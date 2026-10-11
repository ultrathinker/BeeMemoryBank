using Dapper;

namespace BeeMemoryBank.Storage.Boot;

/// <summary>
/// The boot store file (docs/design/step-b-spec.md 2.3): created and migrated here, with its meta rows. The only file a sealed node
/// reads before unlock. <c>node_id</c> of the meta is written once, when the store is created, and never changed (spec 3.3): it binds
/// the database-key wrappers to this store.
/// </summary>
public sealed class BootStore(BootStoreConnectionFactory factory) : IBootStore
{
    public BootStore(string path) : this(new BootStoreConnectionFactory(path)) { }

    public BootStoreConnectionFactory Connections => factory;

    public string FilePath => factory.FilePath;

    public bool Exists => File.Exists(factory.FilePath) && new FileInfo(factory.FilePath).Length > 0;

    public Task EnsureCreatedAsync(CancellationToken ct = default) => new BootMigrationRunner(factory).RunAsync(ct);

    public async Task<string?> GetMetaAsync(string key, CancellationToken ct = default)
    {
        using var connection = factory.Open();
        return await connection.ExecuteScalarAsync<string?>("SELECT value FROM tbl_boot_meta WHERE key = @key", new { key });
    }

    public async Task SetMetaAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var connection = factory.Open();
        await SetMetaAsync(connection, null, key, value);
    }

    /// <summary>Writes a meta row inside the caller's transaction. A changed <c>node_id</c> is refused (never changes once written).</summary>
    internal static async Task SetMetaAsync(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction? transaction,
        string key, string value)
    {
        if (key == BootMetaKeys.NodeId)
        {
            var existing = await connection.ExecuteScalarAsync<string?>("SELECT value FROM tbl_boot_meta WHERE key = @key", new { key }, transaction);
            if (existing is not null && !string.Equals(existing, value, StringComparison.Ordinal))
                throw new InvalidOperationException("The node id of a boot store is written once and never changed.");
            if (existing is not null) return;
        }
        await connection.ExecuteAsync(
            "INSERT INTO tbl_boot_meta (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key, value }, transaction);
    }

    /// <summary>Every meta row (status readers).</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetAllMetaAsync(CancellationToken ct = default)
    {
        using var connection = factory.Open();
        var rows = await connection.QueryAsync<(string Key, string Value)>("SELECT key, value FROM tbl_boot_meta");
        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// <c>PRAGMA wal_checkpoint(TRUNCATE)</c>: the main file becomes complete and the <c>-wal</c> empty, so a stopped node's three files are
    /// each self-contained (spec 5.4 shutdown, 8.5, 8.7.1). Best effort: a busy result is returned, never thrown.
    /// </summary>
    public async Task<bool> CheckpointAsync(CancellationToken ct = default)
    {
        if (!Exists) return true;
        using var connection = factory.Open();
        var row = await connection.QuerySingleAsync<(long Busy, long Log, long Checkpointed)>("PRAGMA wal_checkpoint(TRUNCATE)");
        return row.Busy == 0;
    }
}
