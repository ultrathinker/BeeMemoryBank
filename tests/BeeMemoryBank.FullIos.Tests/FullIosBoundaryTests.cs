using System.Text.RegularExpressions;
using BeeMemoryBank.Boundary;

namespace BeeMemoryBank.FullIos.Tests;

/// <summary>Runs when an iOS build of the app exists (a Mac), or always when BMB_REQUIRE_IOS_SCAN is set; skipped elsewhere, with the reason.</summary>
public sealed class FullIosAppOutputFactAttribute : FactAttribute
{
    public FullIosAppOutputFactAttribute()
    {
        if (FullIosBoundaryTests.AppFolders().Count == 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BMB_REQUIRE_IOS_SCAN")))
            Skip = "no iOS build of the app here: build it on a Mac (scripts/build-ios-full.sh), or set BMB_REQUIRE_IOS_SCAN to make this fail";
    }
}

/// <summary>
/// The iPhone full app carries the full node's libraries and nothing of the desktop host, the web server or the blind copies, proved three
/// ways: its project file references only the shared libraries and the vault; the closure of those references is the golden list
/// docs/full-node/IOS-APP.golden.txt (on any OS); and on a Mac every BeeMemoryBank.* assembly inside the built .app is that list exactly.
/// The other side of the line - the blind iPhone app stays free of the vault - is IosBoundaryTests', unchanged.
/// </summary>
public class FullIosBoundaryTests
{
    private const string AppName = "BeeMemoryBank.FullIos";
    private const string GoldenFile = "docs/full-node/IOS-APP.golden.txt";

    /// <summary>Never in the app, whatever the golden file says.</summary>
    private static readonly string[] NeverInTheApp =
    [
        "BeeMemoryBank.Hosting", "BeeMemoryBank.Hosting.AspNetCore", "BeeMemoryBank.AppPaths", "BeeMemoryBank.Infrastructure",
        "BeeMemoryBank.Platforms.Apple", "BeeMemoryBank.Profiles", "BeeMemoryBank.Rekey", "BeeMemoryBank.Api", "BeeMemoryBank.Web",
        "BeeMemoryBank.Cli", "BeeMemoryBank.Desktop", "BeeMemoryBank.Node", "BeeMemoryBank.Mobile", "BeeMemoryBank.Blind.AppCore",
        "BeeMemoryBank.Blind.PhoneClient", "BeeMemoryBank.BlindNode", "BeeMemoryBank.BlindIos", "BeeMemoryBank.BlindMobile",
        "BeeMemoryBank.BlindDesktop", "BeeMemoryBank.BlindDesktop.MacOS", "BeeMemoryBank.BlindDesktop.Windows",
    ];

    internal static string RepoRoot() => VaultBoundary.RepoRoot();

    private static string AppDirectory() => Path.Combine(RepoRoot(), "mobile", AppName);

    private static string AppProject() => Path.Combine(AppDirectory(), AppName + ".csproj");

    /// <summary>Every built .app of the project (Debug/Release, simulator/device).</summary>
    internal static IReadOnlyList<string> AppFolders()
    {
        var bin = Path.Combine(RepoRoot(), "mobile", AppName, "bin");
        if (!Directory.Exists(bin)) return [];
        return Directory.GetDirectories(bin, AppName + ".app", SearchOption.AllDirectories)
            .Where(d => File.Exists(Path.Combine(d, AppName + ".dll")))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    private static List<string> Golden() =>
        File.ReadAllLines(Path.Combine(RepoRoot(), GoldenFile))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

    internal static string WithoutComments(string xml) => Regex.Replace(xml, "<!--.*?-->", "", RegexOptions.Singleline);

    private static List<string> ProjectReferences(string csproj) =>
        Regex.Matches(WithoutComments(File.ReadAllText(csproj)), "<ProjectReference Include=\"([^\"]+)\"")
            .Select(m => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csproj)!, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar))))
            .ToList();

    /// <summary>The app's own C# sources (no build output).</summary>
    internal static IEnumerable<string> AppSources() =>
        Directory.GetFiles(AppDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    [Fact]
    public void TheGoldenList_IsSorted_AndHoldsNothingThatMayNeverBeInTheApp()
    {
        var golden = Golden();
        golden.Should().NotBeEmpty().And.OnlyHaveUniqueItems().And.BeInAscendingOrder(StringComparer.Ordinal);
        golden.Should().Contain([AppName, "BeeMemoryBank.Vault", "BeeMemoryBank.Core", "BeeMemoryBank.Sync"]);
        golden.Should().NotContain(NeverInTheApp);
    }

    [Fact]
    public void TheAppProject_ReferencesOnlyTheSharedLibraries_AndTheVault()
    {
        ProjectReferences(AppProject()).Select(Path.GetFileNameWithoutExtension).Should().BeEquivalentTo(
            "BeeMemoryBank.Core", "BeeMemoryBank.Crypto", "BeeMemoryBank.Search", "BeeMemoryBank.Storage", "BeeMemoryBank.Sync",
            "BeeMemoryBank.Vault");

        var csproj = WithoutComments(File.ReadAllText(AppProject()));
        var packages = Regex.Matches(csproj, "<PackageReference Include=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        packages.Should().BeEquivalentTo("Microsoft.Maui.Controls", "Microsoft.Extensions.Http", "Markdig");
        foreach (var name in NeverInTheApp)
            csproj.Should().NotContain(name + ".csproj", "the app must not reference " + name);
        // What it links is source of the Android ordinary app, not an assembly of it.
        Regex.Matches(csproj, "<Compile Include=\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .Should().BeEquivalentTo("../BeeMemoryBank.Mobile/Services/NodeSetupService.cs");
    }

    [Fact]
    public void TheClosureOfTheAppsProjectReferences_IsTheGoldenList()
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal) { AppName };
        var queue = new Queue<string>(ProjectReferences(AppProject()));
        while (queue.Count > 0)
        {
            var project = queue.Dequeue();
            File.Exists(project).Should().BeTrue(project);
            if (!seen.Add(Path.GetFileNameWithoutExtension(project))) continue;
            foreach (var next in ProjectReferences(project)) queue.Enqueue(next);
        }

        seen.Should().Equal(Golden(), $"the app's reference closure changed: update {GoldenFile} only if that is intended");
    }

    [Fact]
    public void TheAppProject_UsesTheManagedHttpHandler_AndKeepsTheInterpreterForDapperOnADevice()
    {
        var csproj = WithoutComments(File.ReadAllText(AppProject()));
        // A node joined by code is dialled on its pinned key: SpkiPinRegistry's callback inside SslStream, on every handshake.
        csproj.Should().Contain("<UseNativeHttpHandler>false</UseNativeHttpHandler>");
        // Dapper emits its row readers at run time; without the interpreter a device build fails at the first query.
        csproj.Should().Contain("<MtouchInterpreter Condition=\"'$(RuntimeIdentifier)' == 'ios-arm64'\">-all</MtouchInterpreter>");
        // The runtime's messages, not their keys: a Release build would show "net_http_ssl_connection_failed" (found on the iPhone).
        csproj.Should().Contain("<UseSystemResourceKeys>false</UseSystemResourceKeys>");
        // The trimmer roots of the Android apps (Dapper's ValueTuple readers) are the same file.
        csproj.Should().Contain(@"<TrimmerRootDescriptor Include=""..\BeeMemoryBank.BlindMobile\Linker.xml""");
        // No signing identity, profile or team in the repository.
        csproj.Should().NotContain("CodesignKey").And.NotContain("CodesignProvision").And.NotContain("TeamId");
    }

    [Fact]
    public void TheEndToEndHooks_AreOnlyInDebugOrE2EBuilds_AndThePackagingNeverAsksForThem()
    {
        var uses = 0;
        foreach (var file in AppSources())
        {
            var guarded = false;
            foreach (var line in File.ReadAllLines(file))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("#if ", StringComparison.Ordinal)) guarded = trimmed == "#if DEBUG || BMB_E2E";
                else if (trimmed.StartsWith("#endif", StringComparison.Ordinal)) guarded = false;
                if (!line.Contains("BMB_E2E_", StringComparison.Ordinal)) continue;
                uses++;
                guarded.Should().BeTrue($"{Path.GetFileName(file)}: the end-to-end hooks must be compiled out of a Release build: {trimmed}");
            }
        }
        uses.Should().BeGreaterThan(0, "the control: the hooks this test guards are where they are expected");

        var script = Path.Combine(RepoRoot(), "scripts", "build-ios-full.sh");
        File.Exists(script).Should().BeTrue();
        File.ReadAllText(script).Should().NotContain("BmbE2E");
    }

    [FullIosAppOutputFact]
    public void TheBuiltApp_CarriesExactlyTheGoldenAssemblies()
    {
        var folders = AppFolders().Concat(ScanDirs()).ToList();
        folders.Should().NotBeEmpty("BMB_REQUIRE_IOS_SCAN is set: build the app (scripts/build-ios-full.sh) or name the extracted .app in BMB_SCAN_DIRS");
        foreach (var folder in folders)
        {
            var files = AssemblyBoundaryScanner.ApplicationAssemblies(folder);
            files.Select(Path.GetFileNameWithoutExtension).Should().Equal(Golden(), folder);
            AssemblyBoundaryScanner.DuplicatedTypes(files).Should().BeEmpty(folder);
            Directory.GetFiles(folder, "*.dll").Select(Path.GetFileNameWithoutExtension).Should().NotContain(NeverInTheApp, folder);
        }
    }

    /// <summary>An extracted .ipa (Payload/*.app) the release process names.</summary>
    private static IEnumerable<string> ScanDirs() =>
        (Environment.GetEnvironmentVariable("BMB_SCAN_DIRS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(dir => Directory.Exists(dir)
                ? Directory.GetDirectories(dir, AppName + ".app", SearchOption.AllDirectories).Append(dir)
                : [dir])
            .Where(d => File.Exists(Path.Combine(d, AppName + ".dll")) || !Directory.Exists(d));
}
