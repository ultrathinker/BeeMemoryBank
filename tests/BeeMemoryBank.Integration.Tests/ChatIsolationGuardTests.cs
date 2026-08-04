using System.IO;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Guard test (plan §4): the chat feature must NEVER bleed into the sync, snapshot, or
/// Storage-migration surface. chat.db is node-local and must not enter the event stream, a
/// snapshot, or a join/restore — otherwise chat data would be synced to other devices / bundled
/// into snapshot tarballs (which include decrypted bodies and wrapped DEKs).
///
/// This is a SOURCE-SCAN test: it asserts the tokens <c>chat</c> / <c>ChatDb</c> / <c>chat.db</c>
/// do not appear in any <c>*.cs</c> under <c>libs/BeeMemoryBank.Sync/</c> nor in
/// <c>SnapshotService.cs</c> / <c>RestoreInitiatorService.cs</c>, and that no <c>chat_*.sql</c>
/// file lands under <c>libs/BeeMemoryBank.Storage/Migrations/</c>.
/// </summary>
public class ChatIsolationGuardTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate repo root from " + AppContext.BaseDirectory);
    }

    // Matches the plan's three tokens (case-insensitive). "chat" covers "ChatDb"/"chat.db" too,
    // but each is checked explicitly so a failure names the precise offending token.
    private static readonly string[] ForbiddenTokens = { "chat", "ChatDb", "chat.db" };

    private static IEnumerable<string> RelevantSourceFiles()
    {
        var syncDir = Path.Combine(RepoRoot, "libs", "BeeMemoryBank.Sync");
        if (Directory.Exists(syncDir))
        {
            foreach (var f in Directory.EnumerateFiles(syncDir, "*.cs", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(f);
                if (full.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    full.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;
                yield return f;
            }
        }

        foreach (var name in new[] { "SnapshotService.cs", "RestoreInitiatorService.cs" })
        {
            var path = Path.Combine(RepoRoot, "server", "BeeMemoryBank.Api", "Services", name);
            if (File.Exists(path))
                yield return path;
        }
    }

    [Fact]
    public void Chat_Tokens_Are_Absent_From_Sync_And_Snapshot_Source()
    {
        var offenders = new List<string>();

        foreach (var file in RelevantSourceFiles())
        {
            var content = File.ReadAllText(file);
            foreach (var token in ForbiddenTokens)
            {
                if (content.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"'{token}' found in {Path.GetRelativePath(RepoRoot, file)}");
                }
            }
        }

        if (offenders.Count > 0)
            Assert.Fail(
                "Chat feature leaked into sync/snapshot source — chat data must never enter the " +
                "event stream, a snapshot, or a join/restore (plan §1, §4):\n  - " +
                string.Join("\n  - ", offenders));
    }

    [Fact]
    public void No_Chat_Migration_Sql_Under_Storage_Migrations()
    {
        var migrationsDir = Path.Combine(RepoRoot, "libs", "BeeMemoryBank.Storage", "Migrations");
        var offenders = Directory.Exists(migrationsDir)
            ? Directory.EnumerateFiles(migrationsDir, "chat_*.sql", SearchOption.TopDirectoryOnly).ToList()
            : [];

        if (offenders.Count > 0)
            Assert.Fail(
                "chat_*.sql files must NOT live under libs/BeeMemoryBank.Storage/Migrations/ — that " +
                "folder is glob-embedded and MigrationRunner-managed. The chat schema lives in " +
                "Api/Services/ChatDbInitializer instead (plan §1, §4):\n  - " +
                string.Join("\n  - ", offenders.Select(f => Path.GetRelativePath(RepoRoot, f))));
    }
}
