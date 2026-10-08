using System.Text.RegularExpressions;
using BeeMemoryBank.Boundary;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>Runs when an iOS build of the app exists (a Mac), or always when BMB_REQUIRE_IOS_SCAN is set; skipped elsewhere, with the reason.</summary>
public sealed class IosAppOutputFactAttribute : FactAttribute
{
    public IosAppOutputFactAttribute()
    {
        if (IosBoundaryTests.AppFolders().Count == 0 && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BMB_REQUIRE_IOS_SCAN")))
            Skip = "no iOS build of the app here: build it on a Mac (scripts/build-ios-blind.sh), or set BMB_REQUIRE_IOS_SCAN to make this fail";
    }
}

/// <summary>
/// The iPhone blind app does not contain the vault (BMB-99), the hosting layer, AppPaths or the macOS Keychain layer, proved three ways:
/// its project file references only the shared libraries, the phone's client and Blind.AppCore; the closure of those references is the
/// golden list docs/blind-node/IOS-APP.golden.txt (on any OS); and on a Mac every BeeMemoryBank.* assembly inside the built .app is that
/// list exactly and passes the same metadata scan as the Android and desktop apps (no vault type defined, referenced or called).
/// </summary>
public class IosBoundaryTests
{
    private const string AppName = "BeeMemoryBank.BlindIos";
    private const string GoldenFile = "docs/blind-node/IOS-APP.golden.txt";

    /// <summary>Never in the app, whatever the golden file says.</summary>
    private static readonly string[] NeverInTheApp =
    [
        "BeeMemoryBank.Vault", "BeeMemoryBank.Hosting", "BeeMemoryBank.Hosting.AspNetCore", "BeeMemoryBank.AppPaths",
        "BeeMemoryBank.Platforms.Apple", "BeeMemoryBank.Embeddings", "BeeMemoryBank.Media", "BeeMemoryBank.Infrastructure",
        "BeeMemoryBank.Profiles", "BeeMemoryBank.Rekey", "BeeMemoryBank.Desktop", "BeeMemoryBank.Node",
        "BeeMemoryBank.BlindDesktop", "BeeMemoryBank.BlindDesktop.MacOS", "BeeMemoryBank.BlindDesktop.Windows", "BeeMemoryBank.BlindMobile",
    ];

    private static string RepoRoot() => VaultBoundary.RepoRoot();

    private static string AppProject() => Path.Combine(RepoRoot(), "mobile", AppName, AppName + ".csproj");

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

    private static string WithoutComments(string xml) => Regex.Replace(xml, "<!--.*?-->", "", RegexOptions.Singleline);

    private static List<string> ProjectReferences(string csproj) =>
        Regex.Matches(WithoutComments(File.ReadAllText(csproj)), "<ProjectReference Include=\"([^\"]+)\"")
            .Select(m => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csproj)!, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar))))
            .ToList();

    [Fact]
    public void TheGoldenList_IsSorted_AndHoldsNothingThatMayNeverBeInTheApp()
    {
        var golden = Golden();
        golden.Should().NotBeEmpty().And.OnlyHaveUniqueItems().And.BeInAscendingOrder(StringComparer.Ordinal);
        golden.Should().Contain([AppName, "BeeMemoryBank.Blind.AppCore", "BeeMemoryBank.Core", "BeeMemoryBank.Sync"]);
        golden.Should().NotContain(NeverInTheApp);
    }

    [Fact]
    public void TheAppProject_ReferencesOnlyTheSharedLibraries_ThePhoneClient_AndBlindAppCore()
    {
        ProjectReferences(AppProject()).Select(Path.GetFileNameWithoutExtension).Should().BeEquivalentTo(
            "BeeMemoryBank.Core", "BeeMemoryBank.Crypto", "BeeMemoryBank.Search", "BeeMemoryBank.Storage", "BeeMemoryBank.Sync",
            "BeeMemoryBank.Blind.PhoneClient", "BeeMemoryBank.Blind.AppCore");

        var csproj = WithoutComments(File.ReadAllText(AppProject()));
        var packages = Regex.Matches(csproj, "<PackageReference Include=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        packages.Should().BeEquivalentTo("Microsoft.Maui.Controls", "Microsoft.Extensions.Http", "QRCoder");
        foreach (var name in NeverInTheApp.Where(n => n != "BeeMemoryBank.BlindDesktop" && n != "BeeMemoryBank.BlindDesktop.MacOS"))
            csproj.Should().NotContain(name + ".csproj", "the app must not reference " + name);
        // The two files it links from the desktop copies are source, not their assemblies.
        Regex.Matches(csproj, "<Compile Include=\"([^\"]+)\"").Select(m => Path.GetFileName(m.Groups[1].Value.Replace('\\', '/')))
            .Should().BeEquivalentTo("BlindTimerScheduler.cs", "MacOsBlindStateStore.cs");
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
    public void TheAppProject_UsesTheManagedHttpHandler_AndKeepsTheInterpreterForDapper()
    {
        var csproj = WithoutComments(File.ReadAllText(AppProject()));
        // The pin is checked by BlindHttpHandler's callback inside SslStream on every handshake, as on the other platforms.
        csproj.Should().Contain("<UseNativeHttpHandler>false</UseNativeHttpHandler>");
        // Dapper emits its row readers at run time; without the interpreter a device build fails at the first query.
        csproj.Should().Contain("<MtouchInterpreter>-all</MtouchInterpreter>");
        // The trimmer roots of the Android app (Dapper's ValueTuple readers) are the same file.
        csproj.Should().Contain(@"<TrimmerRootDescriptor Include=""..\BeeMemoryBank.BlindMobile\Linker.xml""");
        // No signing identity, profile or team in the repository.
        csproj.Should().NotContain("CodesignKey").And.NotContain("CodesignProvision").And.NotContain("TeamId");
    }

    [Fact]
    public void TheAppProject_KeepsTheRuntimesMessages_SoAFailedSyncSaysWhyInWords()
    {
        // iOS Release builds default to resource keys instead of messages; found on the iPhone, where a refused handshake read
        // "net_http_ssl_connection_failed (AuthenticationException: net_ssl_io_cert_custom_validation)".
        WithoutComments(File.ReadAllText(AppProject())).Should().Contain("<UseSystemResourceKeys>false</UseSystemResourceKeys>");
    }

    [Fact]
    public void TheEndToEndCallCodeAtLaunch_IsOnlyInDebugOrE2EBuilds_AndThePackagingNeverAsksForIt()
    {
        var sources = Directory.GetFiles(Path.Combine(RepoRoot(), "mobile", AppName), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
        var uses = 0;
        foreach (var file in sources)
        {
            var guarded = false;
            foreach (var line in File.ReadAllLines(file))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("#if ", StringComparison.Ordinal)) guarded = trimmed == "#if DEBUG || BMB_E2E";
                else if (trimmed.StartsWith("#endif", StringComparison.Ordinal)) guarded = false;
                if (!line.Contains("BMB_BLIND_E2E_", StringComparison.Ordinal) && !line.Contains("BMB_E2E_", StringComparison.Ordinal)) continue;
                uses++;
                guarded.Should().BeTrue($"{Path.GetFileName(file)}: the end-to-end hooks must be compiled out of a Release build: {trimmed}");
            }
        }
        uses.Should().BeGreaterThan(0, "the control: the hook this test guards is where it is expected");

        File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "build-ios-blind.sh")).Should().NotContain("BmbE2E");
    }

    [IosAppOutputFact]
    public void TheBuiltApp_CarriesExactlyTheGoldenAssemblies_AndNoVaultCode()
    {
        var folders = AppFolders().Concat(ScanDirs()).ToList();
        folders.Should().NotBeEmpty("BMB_REQUIRE_IOS_SCAN is set: build the app (scripts/build-ios-blind.sh) or name the extracted .app in BMB_SCAN_DIRS");
        foreach (var folder in folders)
        {
            var files = AssemblyBoundaryScanner.ApplicationAssemblies(folder);
            files.Select(Path.GetFileNameWithoutExtension).Should().Equal(Golden(), folder);
            AssemblyBoundaryScanner.Scan(files, VaultBoundary.Forbidden()).Should().BeEmpty(folder);
            AssemblyBoundaryScanner.DuplicatedTypes(files).Should().BeEmpty(folder);
            Directory.GetFiles(folder, "*.dll").Select(Path.GetFileNameWithoutExtension).Should().NotContain(NeverInTheApp, folder);
        }
    }

    [IosAppOutputFact]
    public void TheAppsOwnAssembly_NeverTouchesARepositoryOrTheEventApplier()
    {
        // The Android app's receive-only rule (BMB-91): only Blind.AppCore's composition holds the replicated-row repositories, for
        // EventApplier. The iPhone app composes through AddBlindAppCore and its screen, background and seams must not name one.
        var core = typeof(BeeMemoryBank.Core.Interfaces.IArticleRepository).Assembly;
        var restricted = core.GetTypes().Where(t => t.IsInterface && t.Name.EndsWith("Repository", StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .Append(typeof(BeeMemoryBank.Sync.EventApplier).FullName!)
            .ToHashSet(StringComparer.Ordinal);
        restricted.Should().Contain("BeeMemoryBank.Core.Interfaces.IArticleRepository");
        var forbidden = new AssemblyBoundaryScanner.Forbidden(restricted, new HashSet<string>(), new HashSet<string>());

        foreach (var folder in AppFolders())
            AssemblyBoundaryScanner.Scan([Path.Combine(folder, AppName + ".dll")], forbidden).Should().BeEmpty(folder);
    }

    [Fact]
    public void Control_TheScanFlagsTheVaultWhenItIsGivenTheVault()
    {
        // This test project references the Vault (only so that this control can exist): the same scan over it must light up, or the clean
        // results above would mean nothing.
        var vault = Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.Vault.dll");
        File.Exists(vault).Should().BeTrue("this test project references the vault for the control");
        AssemblyBoundaryScanner.Scan([vault], VaultBoundary.Forbidden())
            .Should().Contain(f => f.Kind == "defines type" && f.Detail == "BeeMemoryBank.Core.Services.SessionService");
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
