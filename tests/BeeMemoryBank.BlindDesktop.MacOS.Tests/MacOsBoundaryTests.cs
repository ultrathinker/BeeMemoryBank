using System.Text.RegularExpressions;
using BeeMemoryBank.Boundary;

namespace BeeMemoryBank.BlindDesktop.MacOS.Tests;

/// <summary>
/// The macOS adapters of the blind app do not contain the vault (BMB-99 / BMB-107), proved on the compiled output rather than on the
/// project file alone: every BeeMemoryBank.* assembly the library's build produced is read at the metadata level and must not define a
/// vault type, reference one, define or call a member that hands out the master data key, or reference the vault assembly - the same scan
/// the Android app uses (tests/shared). The release process names the extracted <c>.app</c> / staging folder in BMB_SCAN_DIRS.
/// </summary>
public class MacOsBoundaryTests
{
    private const string LibraryName = "BeeMemoryBank.BlindDesktop.MacOS";

    /// <summary>The one assembly a macOS blind binary carries on top of AppCore and the shared libraries: the neutral Apple platform layer.</summary>
    private const string AppleLayer = "BeeMemoryBank.Platforms.Apple";

    private static string RepoRoot() => VaultBoundary.RepoRoot();

    private static List<string> OutputFolders() =>
        new[] { "Debug", "Release" }
            .Select(c => Path.Combine(RepoRoot(), "desktop", LibraryName, "bin", c, "net10.0"))
            .Where(d => File.Exists(Path.Combine(d, LibraryName + ".dll")))
            .ToList();

    [Fact]
    public void TheLibraryOutput_ContainsNoVaultCode()
    {
        var folders = OutputFolders();
        folders.Should().NotBeEmpty("build the macOS adapters library first (Debug or Release)");
        foreach (var folder in folders)
        {
            var files = AssemblyBoundaryScanner.ApplicationAssemblies(folder);
            files.Select(Path.GetFileNameWithoutExtension).Should().Contain([LibraryName, AppleLayer, "BeeMemoryBank.Blind.AppCore", "BeeMemoryBank.Core", "BeeMemoryBank.Sync"],
                "the scan must see the library and the shared libraries it carries: " + folder);
            files.Select(Path.GetFileNameWithoutExtension).Should().NotContain("BeeMemoryBank.Vault", folder);
            AssemblyBoundaryScanner.Scan(files, VaultBoundary.Forbidden()).Should().BeEmpty(folder);
            AssemblyBoundaryScanner.DuplicatedTypes(files).Should().BeEmpty(folder);
        }
    }

    [Fact]
    public void TheLibraryOutput_HoldsOnlyTheSharedAssembliesOfTheApplicationLibraries()
    {
        foreach (var folder in OutputFolders())
        {
            var own = Directory.GetFiles(folder, "BeeMemoryBank.*.dll").Select(Path.GetFileNameWithoutExtension).ToList();
            own.Should().BeEquivalentTo(new[]
            {
                "BeeMemoryBank.Core", "BeeMemoryBank.Crypto", "BeeMemoryBank.Search", "BeeMemoryBank.Storage", "BeeMemoryBank.Sync",
                "BeeMemoryBank.Blind.PhoneClient", "BeeMemoryBank.Blind.AppCore", AppleLayer, LibraryName,
            }, "the macOS adapters may carry their own assembly, the Apple platform layer, AppCore, the shared libraries and the phone's client, and no other BeeMemoryBank library");
        }
    }

    [Fact]
    public void TheLibraryItself_ReferencesNoHostOrFullNodeAssembly()
    {
        // Metadata of the library alone: no Vault, no Hosting, no AppPaths, no full Desktop or Node, no UI toolkit.
        var forbidden = new AssemblyBoundaryScanner.Forbidden(
            new HashSet<string>(),
            new HashSet<string>(),
            new HashSet<string>
            {
                "BeeMemoryBank.Vault", "BeeMemoryBank.Hosting", "BeeMemoryBank.Hosting.AspNetCore", "BeeMemoryBank.AppPaths",
                "BeeMemoryBank.Desktop", "BeeMemoryBank.Node", "BeeMemoryBank.Infrastructure", "BeeMemoryBank.Embeddings",
                "BeeMemoryBank.Media", "BeeMemoryBank.Profiles", "BeeMemoryBank.Rekey",
                "Avalonia", "Avalonia.Base", "Avalonia.Controls", "Avalonia.Desktop", "Microsoft.Maui", "Microsoft.Maui.Controls",
            });
        var dll = typeof(BeeMemoryBank.BlindDesktop.MacOS.MacOsBlindPaths).Assembly.Location;
        Path.GetFileNameWithoutExtension(dll).Should().Be(LibraryName);

        AssemblyBoundaryScanner.Scan([dll], forbidden).Should().BeEmpty();
        AssemblyBoundaryScanner.DefinedTypes(dll).Should().NotBeEmpty();
    }

    [Fact]
    public void TheLibraryCsproj_ReferencesOnlyBlindAppCoreAndTheApplePlatformLayer_AndNoForbiddenPackage()
    {
        // the comments of the project file explain what it must not reference, so they are not part of the check
        var csproj = Regex.Replace(File.ReadAllText(Path.Combine(RepoRoot(), "desktop", LibraryName, LibraryName + ".csproj")), "<!--.*?-->", "", RegexOptions.Singleline);

        var projects = Regex.Matches(csproj, "<ProjectReference Include=\"([^\"]+)\"")
            .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/'))).ToList();
        projects.Should().Equal("BeeMemoryBank.Blind.AppCore", AppleLayer);

        var packages = Regex.Matches(csproj, "<PackageReference Include=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        packages.Should().OnlyContain(p => p.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal));
        csproj.Should().NotContain("Avalonia").And.NotContain("Vault").And.NotContain("Hosting");
    }

    [Fact]
    public void ThePublishedFolders_NamedInBMB_SCAN_DIRS_ContainNoVaultCode()
    {
        // A packaged macOS app: the release process extracts the .app (or points at its staging folder) and names it.
        var dirs = (Environment.GetEnvironmentVariable("BMB_SCAN_DIRS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (dirs.Length == 0)
            Environment.GetEnvironmentVariable("BMB_REQUIRE_SCAN").Should().BeNullOrEmpty("BMB_REQUIRE_SCAN is set, so BMB_SCAN_DIRS must name the package folder");
        foreach (var dir in dirs)
        {
            Directory.Exists(dir).Should().BeTrue(dir);
            var files = Directory.GetFiles(dir, "BeeMemoryBank.*.dll", SearchOption.AllDirectories);
            files.Should().NotBeEmpty(dir);
            AssemblyBoundaryScanner.Scan(files, VaultBoundary.Forbidden()).Should().BeEmpty(dir);
        }
    }

    [Fact]
    public void TheApplePlatformLayer_IsInTheOutput_AsAnAssemblyOfItsOwn_NotAsACopyOfItsTypes()
    {
        foreach (var folder in OutputFolders())
        {
            var files = AssemblyBoundaryScanner.ApplicationAssemblies(folder);
            files.Select(Path.GetFileNameWithoutExtension).Should().Contain(AppleLayer, folder);
            // the types the blind adapters use from it are defined there and nowhere else
            AssemblyBoundaryScanner.DuplicatedTypes(files).Should().BeEmpty(folder);
            var own = AssemblyBoundaryScanner.DefinedTypes(files.Single(f => Path.GetFileNameWithoutExtension(f) == LibraryName));
            own.Should().NotContain(["BeeMemoryBank.Platforms.Apple.Keychain.SecurityFrameworkKeychain", "BeeMemoryBank.Platforms.Apple.Keychain.IKeychainBackend"]);
            own.Should().NotContain(t => t.EndsWith(".LaunchAgentPlist", StringComparison.Ordinal) || t.EndsWith(".SecurityFrameworkKeychain", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Control_TheScanFlagsTheVaultWhenItIsGivenTheVault()
    {
        // This test project references the Vault (only so that this control can exist), so its own output holds the real thing:
        // the same scan over it must light up. If it did not, the clean results above would mean nothing.
        var vault = Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.Vault.dll");
        File.Exists(vault).Should().BeTrue("this test project references the vault for the control");
        var findings = AssemblyBoundaryScanner.Scan([vault], VaultBoundary.Forbidden());
        findings.Should().Contain(f => f.Kind == "defines type" && f.Detail == "BeeMemoryBank.Core.Services.SessionService");
    }

    [Fact]
    public void Control_TheLibraryScanWouldFlagAnAssemblyReference_IfThereWasOne()
    {
        // The same library DLL, with an assembly it really references declared forbidden: the AssemblyRef path of the scanner fires.
        var dll = typeof(BeeMemoryBank.BlindDesktop.MacOS.MacOsBlindPaths).Assembly.Location;
        var forbidden = new AssemblyBoundaryScanner.Forbidden(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { "BeeMemoryBank.Blind.AppCore" });

        AssemblyBoundaryScanner.Scan([dll], forbidden).Should().Contain(f => f.Kind == "references assembly" && f.Detail == "BeeMemoryBank.Blind.AppCore");
    }

    [Fact]
    public void Control_AnEmptyScanFailsInsteadOfPassing()
    {
        var act = () => AssemblyBoundaryScanner.Scan([], VaultBoundary.Forbidden());
        act.Should().Throw<InvalidOperationException>();
    }
}
