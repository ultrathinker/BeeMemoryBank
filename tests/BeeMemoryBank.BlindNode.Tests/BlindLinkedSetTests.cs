using System.Text.RegularExpressions;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// The blind host LINKS Api source files (tools/blind-link; the library half is gone since the vault split - the host references
/// the shared libraries directly). The list of what must never be linked is tools/blind-link/exclude.txt; these tests hold the
/// checked-in linked set to it, so a file added by hand to the .props file - or kept after the exclude list grew - fails here
/// instead of shipping in the container.
/// </summary>
public class BlindLinkedSetTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the tests run from inside the repository");
        return dir!.FullName;
    }

    /// <summary>The repo-relative, forward-slash paths a .props file links.</summary>
    private static List<string> Linked(string propsRelativeToRepo)
    {
        var root = RepoRoot();
        var props = Path.Combine(root, propsRelativeToRepo.Replace('/', Path.DirectorySeparatorChar));
        var text = File.ReadAllText(props);
        return Regex.Matches(text, "Compile Include=\"([^\"]+)\"").Select(m =>
        {
            var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(props)!, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar)));
            return Path.GetRelativePath(root, full).Replace('\\', '/');
        }).ToList();
    }

    private static List<string> Excluded() =>
        File.ReadAllLines(Path.Combine(RepoRoot(), "tools", "blind-link", "exclude.txt"))
            .Select(l => l.Split('#')[0].Trim().Replace('\\', '/'))
            .Where(l => l.Length > 0)
            .Select(l => l.StartsWith("api:", StringComparison.Ordinal) ? "server/BeeMemoryBank.Api/" + l[4..] : l)
            .ToList();

    private static bool Matches(string path, string pattern) =>
        pattern.EndsWith('*')
            ? path.StartsWith(pattern[..^1], StringComparison.Ordinal)
            : path == pattern || path.StartsWith(pattern.TrimEnd('/') + "/", StringComparison.Ordinal);

    private const string ApiProps = "server/BeeMemoryBank.BlindNode/linked-api-files.props";

    [Fact]
    public void NothingOnTheExcludeListIsLinked()
    {
        var linked = Linked(ApiProps);
        linked.Should().NotBeEmpty("the scan must see what is linked");
        var excluded = Excluded();
        excluded.Should().NotBeEmpty();
        linked.Where(f => excluded.Any(p => Matches(f, p))).Should().BeEmpty(
            "tools/blind-link/exclude.txt lists code a blind node must never contain");
    }

    [Fact]
    public void EveryLinkedFileExists_AndIsNotAWiringFile()
    {
        var root = RepoRoot();
        var linked = Linked(ApiProps);
        linked.Where(f => !File.Exists(Path.Combine(root, f))).Should().BeEmpty();
        linked.Where(f => f.EndsWith("/ApiServices.cs", StringComparison.Ordinal) || f.EndsWith("/ApiStartupTasks.cs", StringComparison.Ordinal))
            .Should().BeEmpty("the Api's composition root registers the whole application; the blind host has its own (BlindNodeServices)");
    }

    [Fact]
    public void TheHostLinksOnlyApiFiles()
    {
        Linked(ApiProps).Should().OnlyContain(f => f.StartsWith("server/BeeMemoryBank.Api/", StringComparison.Ordinal));
    }

    [Fact]
    public void NoVaultFileIsLinkedIntoAnotherProject()
    {
        // The vault split (BMB-99): the library code of the full node lives in libs/BeeMemoryBank.Vault. No other project may compile one of its files.
        var root = RepoRoot();
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".props", StringComparison.Ordinal))
                                 && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                                 && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
        {
            scanned++;
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "Compile Include=\"([^\"]+)\""))
                if (m.Groups[1].Value.Replace('\\', '/').Contains("BeeMemoryBank.Vault/", StringComparison.Ordinal))
                    offenders.Add(Path.GetRelativePath(root, file) + " -> " + m.Groups[1].Value);
        }
        scanned.Should().BeGreaterThan(10, "the scan must see the projects");
        offenders.Should().BeEmpty("a source file of BeeMemoryBank.Vault must not be linked into another project");
    }
}
