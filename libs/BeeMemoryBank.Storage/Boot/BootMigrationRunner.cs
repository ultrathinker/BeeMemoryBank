using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Boot;

/// <summary>
/// Applies the boot store's own migrations (docs/design/step-b-spec.md 2.3, Appendix B.1) and keeps their own ledger
/// <c>tbl_boot_migration (version, filename, applied_at)</c>. The vault's <see cref="MigrationRunner"/> never sees this ledger or these
/// files (their resource names start with a letter, not a version number) and this runner never sees the vault's.
/// A ledger row whose resource name no longer matches the assembly's is dropped and the current file is applied again, as the vault
/// runner does; every statement of a boot migration is idempotent (<c>IF NOT EXISTS</c>).
/// </summary>
public sealed class BootMigrationRunner(BootStoreConnectionFactory factory)
{
    /// <summary>Resource-name prefix of the boot migrations in the Storage assembly.</summary>
    public const string ResourcePrefix = "BeeMemoryBank.Storage.BootMigrations.";

    /// <summary>The <c>PRAGMA application_id</c> of a boot store, "BMBS".</summary>
    public const int ApplicationId = 0x424D4253;

    /// <summary>The embedded boot migrations by version.</summary>
    public static IReadOnlyDictionary<int, string> EmbeddedMigrations()
    {
        var map = new SortedDictionary<int, string>();
        foreach (var name in typeof(BootMigrationRunner).Assembly.GetManifestResourceNames())
            if (name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                && TryParseVersion(name, out var version))
                map[version] = name;
        return map;
    }

    /// <summary>Creates the file when it is missing or empty (application id, WAL), refuses a file that is not a boot store, applies what is missing.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var path = factory.FilePath;
        var creating = !File.Exists(path) || new FileInfo(path).Length == 0;

        using var connection = factory.Open();
        if (creating)
        {
            await connection.ExecuteAsync($"PRAGMA application_id = {ApplicationId}");
            await connection.ExecuteAsync("PRAGMA journal_mode = WAL");
        }
        else
        {
            var id = await connection.ExecuteScalarAsync<long>("PRAGMA application_id");
            if (id != ApplicationId)
                throw new InvalidOperationException("The file is not a Bee Memory Bank boot store: its application id is not ours. Nothing was changed.");
        }

        await connection.ExecuteAsync(
            "CREATE TABLE IF NOT EXISTS tbl_boot_migration (version INTEGER PRIMARY KEY, filename TEXT NOT NULL, applied_at TEXT NOT NULL)");

        var embedded = EmbeddedMigrations();
        var rows = (await connection.QueryAsync<(long Version, string Filename)>("SELECT version, filename FROM tbl_boot_migration")).ToList();
        foreach (var row in rows)
            if (!embedded.TryGetValue((int)row.Version, out var name) || name != row.Filename)
                await connection.ExecuteAsync("DELETE FROM tbl_boot_migration WHERE version = @v", new { v = row.Version });
        var applied = (await connection.QueryAsync<long>("SELECT version FROM tbl_boot_migration")).ToHashSet();

        var assembly = typeof(BootMigrationRunner).Assembly;
        foreach (var (version, name) in embedded)
        {
            ct.ThrowIfCancellationRequested();
            if (applied.Contains(version)) continue;
            string sql;
            using (var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Embedded boot migration missing."))
            using (var reader = new StreamReader(stream))
                sql = await reader.ReadToEndAsync(ct);

            var statements = MigrationRunner.SplitStatements(sql)
                .Select(StripComments)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            using var transaction = connection.BeginTransaction();
            try
            {
                foreach (var statement in statements)
                    await connection.ExecuteAsync(statement, transaction: transaction);
                await connection.ExecuteAsync(
                    "INSERT INTO tbl_boot_migration (version, filename, applied_at) VALUES (@version, @name, @now)",
                    new { version, name, now = DateTime.UtcNow.ToString("o") }, transaction);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    private static bool TryParseVersion(string resourceName, out int version)
    {
        // "BeeMemoryBank.Storage.BootMigrations.B001_boot_store.sql" -> the digits after the "B" of the file name.
        var file = resourceName[ResourcePrefix.Length..];
        version = 0;
        if (file.Length < 2 || file[0] != 'B' || !char.IsDigit(file[1])) return false;
        var digits = new string(file.Skip(1).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out version);
    }

    private static string StripComments(string sql) =>
        string.Join('\n', sql.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal))).Trim();
}
