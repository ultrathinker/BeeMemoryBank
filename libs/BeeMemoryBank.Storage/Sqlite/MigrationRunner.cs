using Dapper;
using System.Reflection;

namespace BeeMemoryBank.Storage.Sqlite;

public class MigrationRunner
{
    private readonly DbConnectionFactory _factory;

    public MigrationRunner(DbConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task RunMigrationsAsync()
    {
        using var connection = _factory.CreateConnection();

        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS tbl_migration (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                version    INTEGER NOT NULL UNIQUE,
                filename   TEXT NOT NULL,
                applied_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            )");

        var applied = (await connection.QueryAsync<int>("SELECT version FROM tbl_migration"))
            .ToHashSet();

        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(r => r.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r)
            .ToList();

        foreach (var resourceName in resourceNames)
        {
            var parts = resourceName.Split('.');
            var migrationPart = parts.FirstOrDefault(p => p.Length > 0 && char.IsDigit(p[0]));
            if (migrationPart == null) continue;

            var versionStr = new string(migrationPart.TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(versionStr, out var version)) continue;

            if (applied.Contains(version)) continue;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) continue;
            using var reader = new StreamReader(stream);
            var sql = await reader.ReadToEndAsync();

            using var transaction = connection.BeginTransaction();
            try
            {
                var statements = sql
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(StripComments)
                    .Where(s => !string.IsNullOrWhiteSpace(s));

                foreach (var statement in statements)
                    await connection.ExecuteAsync(statement, transaction: transaction);

                var now = DateTime.UtcNow.ToString("o");
                await connection.ExecuteAsync(
                    "INSERT INTO tbl_migration (version, filename, applied_at, updated_at) VALUES (@version, @filename, @now, @now)",
                    new { version, filename = resourceName, now },
                    transaction: transaction);

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    private static string StripComments(string sql)
    {
        var lines = sql.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("--"));
        return string.Join('\n', lines).Trim();
    }
}
