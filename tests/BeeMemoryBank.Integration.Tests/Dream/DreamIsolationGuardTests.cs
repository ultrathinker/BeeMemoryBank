namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Guard test (Dream plan 2.5, 3.2), the twin of <see cref="ChatIsolationGuardTests"/>: Dream's data is local to the node. It lives in chat.db and
/// must never enter the sync event stream, a snapshot, a join or a restore, and its schema is never a vault migration. A SOURCE scan: the token
/// <c>dream</c> (covering <c>dream_</c> and <c>Dream</c>) appears in no <c>*.cs</c> of libs/BeeMemoryBank.Sync and libs/BeeMemoryBank.Vault/Sync, nor in
/// any file of SnapshotService or RestoreInitiatorService; no <c>dream_*.sql</c> lands under libs/BeeMemoryBank.Storage/Migrations. Also: the Dream
/// sources stay free of Cyrillic (a public repository; a Cyrillic literal is written in \u escapes).
/// </summary>
public class DreamIsolationGuardTests
{
    private static readonly string RepoRoot = DiSnapshot.RepoRoot();

    private static bool IsBuildOutput(string path)
    {
        var full = Path.GetFullPath(path);
        var sep = Path.DirectorySeparatorChar;
        return full.Contains($"{sep}bin{sep}") || full.Contains($"{sep}obj{sep}");
    }

    private static IEnumerable<string> GuardedSourceFiles()
    {
        foreach (var syncDir in new[]
                 {
                     Path.Combine(RepoRoot, "libs", "BeeMemoryBank.Sync"),
                     Path.Combine(RepoRoot, "libs", "BeeMemoryBank.Vault", "Sync"),
                 })
        {
            Assert.True(Directory.Exists(syncDir), syncDir + " must exist: the guard would otherwise scan nothing there");
            foreach (var f in Directory.EnumerateFiles(syncDir, "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
                yield return f;
        }

        // Every partial file of the two services, not only the main one: SnapshotService is split over a dozen files.
        var services = Path.Combine(RepoRoot, "server", "BeeMemoryBank.Api", "Services");
        var named = Directory.EnumerateFiles(services, "SnapshotService*.cs")
            .Concat(Directory.EnumerateFiles(services, "RestoreInitiatorService*.cs"))
            .ToList();
        named.Should().Contain(f => Path.GetFileName(f) == "SnapshotService.cs").And.Contain(f => Path.GetFileName(f) == "RestoreInitiatorService.cs");
        foreach (var f in named)
            yield return f;
    }

    [Fact]
    public void Dream_Tokens_Are_Absent_From_Sync_Snapshot_And_Restore_Source()
    {
        var offenders = new List<string>();
        foreach (var file in GuardedSourceFiles())
        {
            var content = File.ReadAllText(file);
            foreach (var token in new[] { "dream_", "Dream" })
                if (content.Contains(token, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"'{token}' found in {Path.GetRelativePath(RepoRoot, file)}");
        }

        offenders.Should().BeEmpty("Dream's runs, advice and journal are local to the node: they must never enter the event stream, a snapshot, "
                                   + "a join or a restore (Dream plan 2.5)");
    }

    [Fact]
    public void No_Dream_Migration_Sql_Under_Storage_Migrations()
    {
        var migrationsDir = Path.Combine(RepoRoot, "libs", "BeeMemoryBank.Storage", "Migrations");
        Directory.Exists(migrationsDir).Should().BeTrue();
        Directory.EnumerateFiles(migrationsDir, "*.sql", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f).Contains("dream", StringComparison.OrdinalIgnoreCase)
                        || File.ReadAllText(f).Contains("dream_", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(RepoRoot, f))
            .Should().BeEmpty("the Dream tables live in chat.db (DreamSchema via ChatDbInitializer), never in the vault's migrations");
    }

    [Fact]
    public void Dream_Sources_Hold_No_Cyrillic_Character()
    {
        var roots = new[]
        {
            Path.Combine(RepoRoot, "server", "BeeMemoryBank.Api", "Dream"),
            Path.Combine(RepoRoot, "tests", "BeeMemoryBank.Integration.Tests", "Dream"),
        };
        var files = roots.Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories))
            .Concat(Directory.EnumerateFiles(Path.Combine(RepoRoot, "server", "BeeMemoryBank.Api", "Endpoints"), "DreamEndpoints*.cs"))
            .Where(f => !IsBuildOutput(f))
            .ToList();
        files.Should().Contain(f => f.EndsWith("DreamThresholds.cs", StringComparison.Ordinal));

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Any(c => c is >= '\u0400' and <= '\u04FF'))
                    offenders.Add($"{Path.GetRelativePath(RepoRoot, file)}:{i + 1}");
        }
        offenders.Should().BeEmpty("the repository is public and its text English; a Cyrillic literal goes in \\u escapes");
    }
}
