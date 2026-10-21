using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// The embedded migrations as the ledger sees them: logical names and bytes, pinned in docs/migrations.golden.txt.
/// Since 2.6.2 there is the baseline (BMB-314), and since 2.7 the first migrations after it (041, the invitations to a blind copy, and 042, a server blind node as the one invited). The ledger row of a database carries its resource name, and the runner
/// refuses a ledger it does not recognise, so a rename, a lost resource or a changed byte (for example when code moves between
/// assemblies, BMB-99) must fail here, not on a live node's volume.
/// Regenerate after an INTENDED new migration with BMB_UPDATE_GOLDEN=1.
/// </summary>
public class MigrationManifestTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the tests run from inside the repository");
        return dir!.FullName;
    }

    [Fact]
    public void TheEmbeddedMigrationsAreTheBaselineAndTheOnesAfterIt()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var lines = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("BeeMemoryBank.Storage.Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var s = assembly.GetManifestResourceStream(n)!;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return n + " " + Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant();
            })
            .ToList();
        lines.Should().HaveCount(3, "the baseline, 041_blind_invite.sql and 042_blind_invite_server.sql");
        var actual = string.Join("\n", lines) + "\n";

        var path = Path.Combine(RepoRoot(), "docs", "migrations.golden.txt");
        if (Environment.GetEnvironmentVariable("BMB_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }
        actual.Should().Be(File.ReadAllText(path).Replace("\r\n", "\n"),
            "the migration resource names and bytes are part of the on-disk contract (the ledger row names the baseline; see MigrationRunner)");
    }
}
