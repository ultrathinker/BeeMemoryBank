using System.Text.RegularExpressions;
using BeeMemoryBank.Boundary;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The full app's secret stores (the DPAPI store, the Keychain store, <c>IUserSecretStore</c>) live in <c>BeeMemoryBank.Infrastructure</c>,
/// which also holds the local CA, mDNS and the vault-bound auto-unlock. None of that may reach a blind app. Proved three ways:
/// on the project graph (no blind project reaches Infrastructure through ProjectReference, however indirectly, and none compiles one of its
/// files), on the compiled output of every blind project that has been built (no Infrastructure assembly, no reference to it, none of its
/// secret-store types), and on the packaged folders the release process names in BMB_SCAN_DIRS (the blind .app, the Windows blind publish
/// folder, the Android linker output). The vault itself is proved absent by the scans that already exist (VaultBoundaryListTests and the
/// per-app AppBoundaryTests); these add Infrastructure to the same discipline.
/// </summary>
public class InfrastructureBoundaryTests
{
    private const string Infrastructure = "BeeMemoryBank.Infrastructure";

    private static readonly string[] SecretStoreTypes =
    [
        "BeeMemoryBank.Infrastructure.Secrets.IUserSecretStore",
        "BeeMemoryBank.Infrastructure.Secrets.MacOsKeychainUserSecretStore",
        "BeeMemoryBank.Infrastructure.Secrets.WindowsDpapiUserSecretStore",
        "BeeMemoryBank.Infrastructure.Secrets.InMemoryUserSecretStore",
        "BeeMemoryBank.Infrastructure.Secrets.UnsupportedUserSecretStore",
        "BeeMemoryBank.Infrastructure.Secrets.UserSecretStores",
        "BeeMemoryBank.Infrastructure.Secrets.UserSecretStoreException",
        "BeeMemoryBank.Infrastructure.OsAutoUnlock.OsAutoUnlockService",
        "BeeMemoryBank.Infrastructure.OsAutoUnlock.UpdateUnlockHandoff",
        "BeeMemoryBank.Infrastructure.Tls.LocalCaService",
    ];

    private static AssemblyBoundaryScanner.Forbidden ForbiddenInfrastructure() =>
        new(SecretStoreTypes.ToHashSet(StringComparer.Ordinal), new HashSet<string>(), new HashSet<string>(StringComparer.Ordinal) { Infrastructure });

    private static string Root() => VaultBoundary.RepoRoot();

    private static bool IsBuildOutput(string path) =>
        path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
        path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
        path.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar);

    /// <summary>Every project file of the product (tests excluded) by project name, with its direct project references by name.</summary>
    private static Dictionary<string, (string Path, List<string> References)> ProjectGraph()
    {
        var projects = new Dictionary<string, (string Path, List<string> References)>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Root(), "*.csproj", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
        {
            var relative = Path.GetRelativePath(Root(), file).Replace('\\', '/');
            if (relative.StartsWith("tests/", StringComparison.Ordinal)) continue;
            var text = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", "", RegexOptions.Singleline);
            var references = Regex.Matches(text, "<ProjectReference\\s+Include=\"([^\"]+)\"")
                .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/'))).ToList();
            projects[Path.GetFileNameWithoutExtension(file)] = (relative, references);
        }
        return projects;
    }

    private static HashSet<string> Closure(Dictionary<string, (string Path, List<string> References)> graph, string project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(project);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!graph.TryGetValue(current, out var node)) continue;
            foreach (var reference in node.References)
                if (seen.Add(reference)) stack.Push(reference);
        }
        return seen;
    }

    private static List<string> BlindProjects(Dictionary<string, (string Path, List<string> References)> graph) =>
        graph.Keys.Where(name => name.StartsWith("BeeMemoryBank.Blind", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToList();

    [Fact]
    public void NoBlindProject_ReachesInfrastructure_ThroughProjectReferences()
    {
        var graph = ProjectGraph();
        var blind = BlindProjects(graph);

        blind.Should().Contain([
            "BeeMemoryBank.BlindDesktop", "BeeMemoryBank.BlindDesktop.MacOS", "BeeMemoryBank.BlindDesktop.Windows", "BeeMemoryBank.Blind.AppCore",
            "BeeMemoryBank.Blind.PhoneClient", "BeeMemoryBank.BlindMobile", "BeeMemoryBank.BlindNode", "BeeMemoryBank.BlindCli", "BeeMemoryBank.BlindConsole"],
            "the scan must see every blind project");
        foreach (var project in blind)
        {
            Closure(graph, project).Should().NotContain(Infrastructure, $"{project} ({graph[project].Path}) is a blind project: no path of ProjectReference may lead to the full app's Infrastructure");
            graph[project].References.Should().NotContain(Infrastructure);
        }
    }

    [Fact]
    public void Control_TheFullNodeAndTheApplePlatformLayer_AreSeenByTheSameScan()
    {
        var graph = ProjectGraph();

        // If the walk were blind to references, the clean results above would mean nothing: the full node does reach Infrastructure, and
        // Infrastructure now reaches the Apple layer (which the blind macOS app shares).
        Closure(graph, "BeeMemoryBank.Api").Should().Contain(Infrastructure);
        Closure(graph, "BeeMemoryBank.Node").Should().Contain(Infrastructure);
        Closure(graph, "BeeMemoryBank.Web").Should().Contain(Infrastructure);
        graph[Infrastructure].References.Should().Contain("BeeMemoryBank.Platforms.Apple");
        Closure(graph, "BeeMemoryBank.BlindDesktop.MacOS").Should().Contain("BeeMemoryBank.Platforms.Apple").And.NotContain(Infrastructure);
        Closure(graph, "BeeMemoryBank.Platforms.Apple").Should().BeEmpty("the Apple layer references nothing of the product");
    }

    [Fact]
    public void NoBlindProject_CompilesAFileOfInfrastructure()
    {
        var graph = ProjectGraph();
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var name in BlindProjects(graph))
        {
            var dir = Path.GetDirectoryName(Path.Combine(Root(), graph[name].Path))!;
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                         .Where(f => (f.EndsWith(".csproj", StringComparison.Ordinal) || f.EndsWith(".props", StringComparison.Ordinal) || f.EndsWith(".targets", StringComparison.Ordinal)) && !IsBuildOutput(f)))
            {
                scanned++;
                foreach (Match m in Regex.Matches(File.ReadAllText(file), "(?:Compile|Content|EmbeddedResource|None)\\s+(?:Include|Update)=\"([^\"]+)\""))
                    if (m.Groups[1].Value.Replace('\\', '/').Contains("BeeMemoryBank.Infrastructure/", StringComparison.Ordinal))
                        offenders.Add(Path.GetRelativePath(Root(), file) + " -> " + m.Groups[1].Value);
            }
        }
        scanned.Should().BeGreaterThan(8, "the scan must see the blind projects' files");
        offenders.Should().BeEmpty("a source file of BeeMemoryBank.Infrastructure must not be linked into a blind project");
    }

    [Fact]
    public void TheBuiltOutputOfEveryBlindProject_HasNoInfrastructureAndNoSecretStoreType()
    {
        var graph = ProjectGraph();
        var folders = new List<string>();
        foreach (var name in BlindProjects(graph))
        {
            var dir = Path.GetDirectoryName(Path.Combine(Root(), graph[name].Path))!;
            foreach (var configuration in new[] { "Debug", "Release" })
            {
                var bin = Path.Combine(dir, "bin", configuration);
                if (!Directory.Exists(bin)) continue;
                // net10.0 and the RID / platform folders below it (win-x64, osx-arm64, net10.0-android ...), where a publish put its files.
                folders.AddRange(Directory.EnumerateDirectories(bin, "*", SearchOption.AllDirectories)
                    .Prepend(bin).Where(d => Directory.GetFiles(d, "BeeMemoryBank.*.dll").Length > 0));
            }
        }
        folders.Should().NotBeEmpty("build the blind projects first (the macOS adapters are a dependency of these tests)");

        foreach (var folder in folders.Distinct())
        {
            var files = AssemblyBoundaryScanner.ApplicationAssemblies(folder);
            files.Select(Path.GetFileNameWithoutExtension).Should().NotContain(Infrastructure, folder);
            AssemblyBoundaryScanner.Scan(files, ForbiddenInfrastructure()).Should().BeEmpty(folder);
        }
    }

    [Fact]
    public void Control_TheScanFlagsInfrastructure_WhenItIsGivenInfrastructure()
    {
        // This test project references Infrastructure, so its own output holds the real thing: the same scan over it must light up.
        var dll = Path.Combine(AppContext.BaseDirectory, Infrastructure + ".dll");
        File.Exists(dll).Should().BeTrue();

        var findings = AssemblyBoundaryScanner.Scan([dll], ForbiddenInfrastructure());
        findings.Should().Contain(f => f.Kind == "defines type" && f.Detail == "BeeMemoryBank.Infrastructure.Secrets.MacOsKeychainUserSecretStore");
        findings.Should().Contain(f => f.Kind == "defines type" && f.Detail == "BeeMemoryBank.Infrastructure.Secrets.WindowsDpapiUserSecretStore");

        var thisTests = Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.Core.Tests.dll");
        AssemblyBoundaryScanner.Scan([thisTests], ForbiddenInfrastructure()).Should().Contain(f => f.Kind == "references assembly" && f.Detail == Infrastructure);
    }

    [Fact]
    public void ThePublishedFolders_NamedInBMB_SCAN_DIRS_ContainNoInfrastructure()
    {
        // The packaged blind apps: the extracted .app / the Windows publish folder / the trimmed Android package, named by the release process.
        var dirs = (Environment.GetEnvironmentVariable("BMB_SCAN_DIRS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (dirs.Length == 0)
            Environment.GetEnvironmentVariable("BMB_REQUIRE_SCAN").Should().BeNullOrEmpty("BMB_REQUIRE_SCAN is set, so BMB_SCAN_DIRS must name the package folder");
        foreach (var dir in dirs)
        {
            Directory.Exists(dir).Should().BeTrue(dir);
            var files = Directory.GetFiles(dir, "BeeMemoryBank.*.dll", SearchOption.AllDirectories);
            files.Should().NotBeEmpty(dir);
            files.Select(Path.GetFileNameWithoutExtension).Should().NotContain(Infrastructure, dir);
            AssemblyBoundaryScanner.Scan(files, ForbiddenInfrastructure()).Should().BeEmpty(dir);
        }
    }
}
