using System.Text.RegularExpressions;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// The blind projects LINK original source files (tools/blind-link). The list of what must never be linked is
/// tools/blind-link/exclude.txt; these tests hold the checked-in linked sets to it, so a file added by hand to a .props
/// file - or kept after the exclude list grew - fails here instead of shipping in the container.
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

    [Theory]
    [InlineData("server/BeeMemoryBank.BlindNode/linked-api-files.props")]
    [InlineData("libs/BeeMemoryBank.Blind/linked-lib-files.props")]
    public void NothingOnTheExcludeListIsLinked(string props)
    {
        var linked = Linked(props);
        linked.Should().NotBeEmpty("the scan must see what is linked");
        var excluded = Excluded();
        excluded.Should().NotBeEmpty();
        linked.Where(f => excluded.Any(p => Matches(f, p))).Should().BeEmpty(
            "tools/blind-link/exclude.txt lists code a blind node must never contain");
    }

    [Theory]
    [InlineData("server/BeeMemoryBank.BlindNode/linked-api-files.props")]
    [InlineData("libs/BeeMemoryBank.Blind/linked-lib-files.props")]
    public void EveryLinkedFileExists_AndIsNotALibraryWiringFile(string props)
    {
        var root = RepoRoot();
        var linked = Linked(props);
        linked.Where(f => !File.Exists(Path.Combine(root, f))).Should().BeEmpty();
        linked.Where(f => f.EndsWith("/DependencyInjection.cs", StringComparison.Ordinal)).Should().BeEmpty(
            "the libraries' DependencyInjection.cs files register the whole application; the blind composition is BlindComposition.cs");
        linked.Where(f => f.EndsWith("/PendingEmbeddingProcessor.cs", StringComparison.Ordinal)).Should().BeEmpty(
            "Sync.csproj itself removes it from its compile list");
    }

    [Fact]
    public void TheHostLinksOnlyApiFiles_AndTheBlindAssemblyOnlyLibraryFiles()
    {
        Linked("server/BeeMemoryBank.BlindNode/linked-api-files.props")
            .Should().OnlyContain(f => f.StartsWith("server/BeeMemoryBank.Api/", StringComparison.Ordinal));
        Linked("libs/BeeMemoryBank.Blind/linked-lib-files.props")
            .Should().OnlyContain(f => Regex.IsMatch(f, "^libs/BeeMemoryBank\\.(Core|Storage|Sync|Crypto|Search)/"));
    }
}
