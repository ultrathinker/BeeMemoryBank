using System.Reflection;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// BMB-314, the rule of the public tree: "the version on GitHub must contain no migrations at all", so Migrations/ holds the baseline and
/// nothing else, the baseline is the only embedded migration, and no other SQL file of the Storage project is a migration of the vault.
/// </summary>
public class TreeTests
{
    private static string StorageDir() => Path.Combine(LegacyChain.RepoRoot(), "libs", "BeeMemoryBank.Storage");

    [Fact]
    public void The_migrations_folder_holds_exactly_the_baseline_file()
    {
        var dir = Path.Combine(StorageDir(), "Migrations");

        Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Should().Equal(new[] { "040_baseline.sql" },
            "a second file in this folder would put a migration back into the public tree");
    }

    [Fact]
    public void The_baseline_is_the_only_embedded_vault_migration()
    {
        var names = typeof(MigrationRunner).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("BeeMemoryBank.Storage.Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .ToList();

        names.Should().Equal("BeeMemoryBank.Storage.Migrations.040_baseline.sql");
    }

    [Fact]
    public void No_other_sql_file_of_the_storage_project_is_a_vault_migration()
    {
        var sql = Directory.EnumerateFiles(StorageDir(), "*.sql", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Select(f => Path.GetRelativePath(StorageDir(), f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        sql.Should().Equal(["BootMigrations/B001_boot_store.sql", "Migrations/040_baseline.sql"],
            "the boot store of a sealed node has its own ledger and its own single file; nothing else belongs here");
    }
}
