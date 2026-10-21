using System.Reflection;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests.Squash;

/// <summary>
/// BMB-314, the rule of the public tree: "the version on GitHub must contain no migrations at all", so Migrations/ holds the baseline and
/// nothing else, the baseline is the only embedded migration, and no other SQL file of the Storage project is a migration of the vault.
/// Release 2.7 adds the first migrations after the baseline (041, the invitations to a blind copy, and 042, a server blind node as the invited), so the lists below name them; when the public
/// tree is squashed again (a new baseline that contains it, a recognised chain for the databases that ran 041) these lists go back to one file.
/// </summary>
public class TreeTests
{
    private static string StorageDir() => Path.Combine(LegacyChain.RepoRoot(), "libs", "BeeMemoryBank.Storage");

    [Fact]
    public void The_migrations_folder_holds_exactly_the_baseline_file_and_the_migrations_after_it()
    {
        var dir = Path.Combine(StorageDir(), "Migrations");

        Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).Should().Equal(
            new[] { "040_baseline.sql", "041_blind_invite.sql", "042_blind_invite_server.sql" },
            "a further file in this folder is a new migration: it is named here on purpose, with its number following the last one");
    }

    [Fact]
    public void The_baseline_and_the_migrations_after_it_are_the_embedded_vault_migrations()
    {
        var names = typeof(MigrationRunner).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("BeeMemoryBank.Storage.Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .ToList();

        names.OrderBy(n => n, StringComparer.Ordinal).Should().Equal(
            "BeeMemoryBank.Storage.Migrations.040_baseline.sql", "BeeMemoryBank.Storage.Migrations.041_blind_invite.sql",
            "BeeMemoryBank.Storage.Migrations.042_blind_invite_server.sql");
    }

    [Fact]
    public void No_other_sql_file_of_the_storage_project_is_a_vault_migration()
    {
        var sql = Directory.EnumerateFiles(StorageDir(), "*.sql", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Select(f => Path.GetRelativePath(StorageDir(), f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        sql.Should().Equal(["BootMigrations/B001_boot_store.sql", "Migrations/040_baseline.sql", "Migrations/041_blind_invite.sql", "Migrations/042_blind_invite_server.sql"],
            "the boot store of a sealed node has its own ledger and its own single file; nothing else belongs here");
    }
}
