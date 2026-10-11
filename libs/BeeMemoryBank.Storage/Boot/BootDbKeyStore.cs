using Dapper;
using Microsoft.Data.Sqlite;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Boot;

/// <summary>The database-key wrappers of the boot store (docs/design/step-b-spec.md 3.3, 3.4): one row per (master DEK fingerprint, DBK generation).</summary>
public sealed class BootDbKeyStore : IBootDbKeyStore
{
    private const string Columns = "dek_fp AS DekFingerprint, dbk_generation AS DbkGeneration, wrapped AS Wrapped, iv AS Iv, created_at AS CreatedAt";

    private readonly BootStoreConnectionFactory _factory;

    public BootDbKeyStore(BootStoreConnectionFactory factory)
    {
        DapperConfig.Configure();
        _factory = factory;
    }

    public async Task<BootDbKeyRow?> FindAsync(string dekFingerprint, int dbkGeneration, CancellationToken ct = default)
    {
        using var connection = _factory.Open();
        return (await connection.QuerySingleOrDefaultAsync<WrapperRecord>(
            $"SELECT {Columns} FROM tbl_boot_db_key WHERE dek_fp = @dekFingerprint AND dbk_generation = @dbkGeneration",
            new { dekFingerprint, dbkGeneration }))?.ToRow();
    }

    public async Task<IReadOnlyList<BootDbKeyRow>> ListAsync(CancellationToken ct = default)
    {
        using var connection = _factory.Open();
        return await ListAsync(connection, null);
    }

    internal static async Task<IReadOnlyList<BootDbKeyRow>> ListAsync(SqliteConnection connection, SqliteTransaction? transaction) =>
        (await connection.QueryAsync<WrapperRecord>($"SELECT {Columns} FROM tbl_boot_db_key ORDER BY dbk_generation, dek_fp", transaction: transaction))
            .Select(r => r.ToRow()).ToList();

    /// <summary>The row as the engine returns it (integers are 64-bit, the time is text); a positional record would need exact column types.</summary>
    private sealed class WrapperRecord
    {
        public string DekFingerprint { get; set; } = "";
        public long DbkGeneration { get; set; }
        public byte[] Wrapped { get; set; } = [];
        public byte[] Iv { get; set; } = [];
        public string CreatedAt { get; set; } = "";

        public BootDbKeyRow ToRow() => new(DekFingerprint, (int)DbkGeneration, Wrapped, Iv,
            DateTime.Parse(CreatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime());
    }

    /// <summary>Adds a wrapper unless one exists for its fingerprint and generation (a row is never re-encrypted under the same key and iv). True when added.</summary>
    public async Task<bool> AddIfMissingAsync(BootDbKeyRow row, CancellationToken ct = default)
    {
        using var connection = _factory.Open();
        return await AddIfMissingAsync(connection, null, row);
    }

    internal static async Task<bool> AddIfMissingAsync(SqliteConnection connection, SqliteTransaction? transaction, BootDbKeyRow row) =>
        await connection.ExecuteAsync(
            "INSERT OR IGNORE INTO tbl_boot_db_key (dek_fp, dbk_generation, wrapped, iv, created_at) VALUES (@DekFingerprint, @DbkGeneration, @Wrapped, @Iv, @now)",
            new { row.DekFingerprint, row.DbkGeneration, row.Wrapped, row.Iv, now = row.CreatedAt.ToUniversalTime().ToString("o") }, transaction) > 0;
}
