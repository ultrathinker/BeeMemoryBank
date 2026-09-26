using System.Text.RegularExpressions;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Pins comment creation to <c>CommentService.CreateAsync</c>, the one path that encrypts the text
/// with the article's key before it is stored and logged.
///
/// <para>
/// <c>ICommentRepository.CreateAsync</c> takes plaintext and stores it unencrypted, and
/// <c>LogCommentCreateAsync</c> ships an unencrypted comment's text in the sync event as is. The
/// Android article page used exactly that pair: every comment written on a phone reached the hub
/// and every other node in clear, and — since it also displayed the raw row — every comment
/// written elsewhere showed up on the phone as an empty line. Found on the three-node test stand.
/// </para>
///
/// <para>
/// Source scanning for the same reason as <see cref="SessionUnlockCallSiteGuardTests"/>:
/// BeeMemoryBank.Mobile targets net10.0-android and cannot be loaded by this test host. The
/// repository call is matched by the receiver's name (<c>commentRepo…</c>), which is the convention
/// everywhere; the event-logger call has no such weakness.
/// </para>
/// </summary>
public class CommentCreateCallSiteGuardTests
{
    private static readonly string[] SearchRoots = ["libs", "server", "desktop", "mobile"];

    private static readonly Regex PlaintextCreate = new(
        @"\.\s*LogCommentCreateAsync\s*\(|[cC]ommentRepo\w*\s*\.\s*CreateAsync\s*\(",
        RegexOptions.Compiled);

    private const string CommentService = "libs/BeeMemoryBank.Core/Services/CommentService.cs";

    [Fact]
    public void Comments_are_created_only_through_CommentService()
    {
        var repoRoot = FindRepoRoot();
        var found = new List<string>();

        foreach (var root in SearchRoots)
        {
            var path = Path.Combine(repoRoot, root);
            if (!Directory.Exists(path)) continue;

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                var sep = Path.DirectorySeparatorChar;
                if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}")) continue;

                var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                if (string.Equals(relative, CommentService, StringComparison.OrdinalIgnoreCase)) continue;

                var text = File.ReadAllText(file);
                foreach (Match m in PlaintextCreate.Matches(text))
                    found.Add($"  - {relative} line {LineNumberAt(text, m.Index)}: {m.Value.Trim()}");
            }
        }

        Assert.True(found.Count == 0,
            "These call sites create a comment outside CommentService:\n" + string.Join("\n", found) +
            "\n\nA comment created this way is stored and synced UNENCRYPTED. Call " +
            "CommentService.CreateAsync (it encrypts with the article's key and logs the event), and " +
            "read comments back through CommentService.DecryptTextAsync.");
    }

    private static int LineNumberAt(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
            if (text[i] == '\n') line++;
        return line;
    }

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

        throw new InvalidOperationException($"Could not locate the repo root from {AppContext.BaseDirectory}");
    }
}
