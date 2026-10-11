using System.Text.RegularExpressions;

namespace BeeMemoryBank.Storage.Tests.Sealed;

/// <summary>
/// Red test 15.1.3 (d) (docs/design/step-b-spec.md): no source file outside <c>docs/design/key-string-sites.golden.txt</c> builds a
/// key-bearing string (<c>SqlCipherScheme.Password</c>, <c>SqlCipherScheme.VacuumIntoTarget</c>) or calls the engine's key functions.
/// </summary>
public class KeyStringSitesTests
{
    private static readonly Regex KeyUse = new(
        @"SqlCipherScheme\s*\.\s*(Password|VacuumIntoTarget)\b|using\s+static\s+[\w.]*SqlCipherScheme\b|\bsqlite3_(key|key_v2|rekey|rekey_v2)\b",
        RegexOptions.Compiled);

    private static readonly string[] SourceRoots = ["libs", "server", "desktop", "mobile", "tools"];

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) return dir.FullName;
        throw new InvalidOperationException("BeeMemoryBank.slnx not found above " + AppContext.BaseDirectory);
    }

    private static HashSet<string> AllowList(string root) =>
        File.ReadAllLines(Path.Combine(root, "docs", "design", "key-string-sites.golden.txt"))
            .Select(l => l.Split('#')[0].Trim())
            .Where(l => l.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> SourceFiles(string root) =>
        SourceRoots.Select(r => Path.Combine(root, r)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f =>
            {
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                return !rel.Contains("/obj/") && !rel.Contains("/bin/");
            });

    [Fact]
    public void No_source_file_outside_the_allow_list_handles_a_page_key_as_text_or_calls_the_engine_key_functions()
    {
        var root = RepositoryRoot();
        var allowed = AllowList(root);

        var offenders = SourceFiles(root)
            .Where(f => KeyUse.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(rel => !allowed.Contains(rel))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty("a file that touches a page key as text belongs in docs/design/key-string-sites.golden.txt only after review");
    }

    [Fact]
    public void The_scan_does_find_the_one_native_key_call_so_it_cannot_pass_by_looking_nowhere()
    {
        var root = RepositoryRoot();

        var users = SourceFiles(root)
            .Where(f => KeyUse.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();

        users.Should().Contain("libs/BeeMemoryBank.Storage/Sealed/SealedSqlite.cs");
    }

    [Fact]
    public void Every_listed_site_that_exists_is_a_real_source_file_and_the_list_has_no_duplicates()
    {
        var root = RepositoryRoot();
        var lines = File.ReadAllLines(Path.Combine(root, "docs", "design", "key-string-sites.golden.txt"))
            .Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0).ToList();

        lines.Should().OnlyHaveUniqueItems();
        lines.Should().OnlyContain(l => l.EndsWith(".cs", StringComparison.Ordinal) && !l.StartsWith('/') && !l.Contains(".."));
    }
}
