using System.Reflection;
using System.Security.Cryptography;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// The blind assembly carries EVERY migration of the full application, byte for byte, under the logical names the
/// Storage project gives them. The migration runner deletes the ledger rows of any migration whose embedded resource
/// it cannot match by name ("ghost hunter"), so a binary that lost a migration - or named it differently - would make a
/// node re-run it. These tests pin the embedded set to the SQL files in libs/BeeMemoryBank.Storage/Migrations.
/// </summary>
public class BlindMigrationLedgerTests
{
    private const string Prefix = "BeeMemoryBank.Storage.Migrations.";

    private static readonly Assembly BlindLib = typeof(BeeMemoryBank.Blind.BlindComposition).Assembly;

    private static string MigrationsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the tests run from inside the repository");
        return Path.Combine(dir!.FullName, "libs", "BeeMemoryBank.Storage", "Migrations");
    }

    private static string[] SourceFiles() => Directory.GetFiles(MigrationsDir(), "*.sql");

    [Fact]
    public void EveryMigrationFileIsEmbedded_UnderTheOriginalLogicalName_WithTheSameBytes()
    {
        var files = SourceFiles();
        files.Should().NotBeEmpty("the scan must see the migrations it guards");

        var embedded = BlindLib.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)).ToList();
        embedded.Should().BeEquivalentTo(files.Select(f => Prefix + Path.GetFileName(f)),
            "the runner matches a ledger row to a resource by this exact name; a missing or renamed one is a ghost");

        foreach (var file in files)
        {
            using var stream = BlindLib.GetManifestResourceStream(Prefix + Path.GetFileName(file))!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            SHA256.HashData(ms.ToArray()).Should().Equal(SHA256.HashData(File.ReadAllBytes(file)),
                $"{Path.GetFileName(file)} must be the very SQL the full application runs");
        }
    }

    [Fact]
    public async Task ARunOnAnEmptyDatabaseWritesTheLedgerTheFullApplicationWrites_AndASecondRunChangesNothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb_ledger_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var factory = new DbConnectionFactory(Path.Combine(dir, "ledger.db"));
            var runner = new MigrationRunner(factory);
            await runner.RunMigrationsAsync();

            var expected = SourceFiles().Select(f => Prefix + Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            using (var conn = factory.CreateConnection())
            {
                var rows = (await conn.QueryAsync<string>("SELECT filename FROM tbl_migration ORDER BY version")).ToList();
                rows.Should().Equal(expected, "the ledger of a fresh blind node is the full application's");
            }

            await runner.RunMigrationsAsync();
            using (var conn = factory.CreateConnection())
            {
                (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration")).Should().Be(expected.Count,
                    "running the runner again deletes and re-applies nothing");
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* a pooled handle on Windows; the temp folder is reaped */ }
        }
    }
}
