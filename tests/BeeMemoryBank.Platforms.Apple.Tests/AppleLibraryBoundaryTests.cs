using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using BeeMemoryBank.Boundary;

namespace BeeMemoryBank.Platforms.Apple.Tests;

/// <summary>
/// The Apple platform layer is shared by the blind macOS app (which must stay Vault-free) and the full macOS app (which has the Vault), and
/// so it may depend on neither: it references no application, no Vault, no Blind.AppCore or other blind project, no UI toolkit, no hosting
/// library and no package. Proved on the compiled output, its metadata, and its project file - the same scan the blind apps use
/// (tests/shared). The packaged apps are scanned by the blind and full boundary tests (BMB_SCAN_DIRS).
/// </summary>
public class AppleLibraryBoundaryTests
{
    private const string LibraryName = "BeeMemoryBank.Platforms.Apple";

    private static string RepoRoot() => VaultBoundary.RepoRoot();

    private static List<string> OutputFolders() =>
        new[] { "Debug", "Release" }
            .Select(c => Path.Combine(RepoRoot(), "libs", LibraryName, "bin", c, "net10.0"))
            .Where(d => File.Exists(Path.Combine(d, LibraryName + ".dll")))
            .ToList();

    [Fact]
    public void TheLibraryOutput_IsTheLibraryAlone_AndContainsNoVaultCode()
    {
        var folders = OutputFolders();
        folders.Should().NotBeEmpty("build the library first (Debug or Release)");
        foreach (var folder in folders)
        {
            Directory.GetFiles(folder, "*.dll").Select(Path.GetFileNameWithoutExtension).Should().Equal([LibraryName],
                "the library has no dependency of its own: nothing but itself is in its output - " + folder);
            var files = AssemblyBoundaryScanner.ApplicationAssemblies(folder);
            files.Select(Path.GetFileNameWithoutExtension).Should().Equal([LibraryName]);
            AssemblyBoundaryScanner.Scan(files, VaultBoundary.Forbidden()).Should().BeEmpty(folder);
        }
    }

    [Fact]
    public void TheLibrary_ReferencesOnlyTheFrameworkItself()
    {
        var dll = typeof(BeeMemoryBank.Platforms.Apple.Keychain.IKeychainBackend).Assembly.Location;
        Path.GetFileNameWithoutExtension(dll).Should().Be(LibraryName);
        using var stream = File.OpenRead(dll);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        var references = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();

        references.Should().NotBeEmpty();
        references.Should().NotContain(r => r.StartsWith("BeeMemoryBank.", StringComparison.Ordinal), "no application, no Vault, no blind project");
        references.Should().NotContain(r => r.StartsWith("Avalonia", StringComparison.Ordinal) || r.StartsWith("Microsoft.Maui", StringComparison.Ordinal)
            || r.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || r.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal),
            "no UI toolkit, no web stack, no hosting or DI package");
        AssemblyBoundaryScanner.Scan([dll], VaultBoundary.Forbidden()).Should().BeEmpty();
        AssemblyBoundaryScanner.DefinedTypes(dll).Should().NotBeEmpty();
    }

    [Fact]
    public void TheProjectFile_HasNoProjectReference_AndNoPackage()
    {
        // the comments of the project file explain what it must not reference, so they are not part of the check
        var csproj = Regex.Replace(File.ReadAllText(Path.Combine(RepoRoot(), "libs", LibraryName, LibraryName + ".csproj")), "<!--.*?-->", "", RegexOptions.Singleline);

        csproj.Should().NotContain("<ProjectReference");
        csproj.Should().NotContain("<PackageReference");
        csproj.Should().NotContain("Vault").And.NotContain("Avalonia").And.NotContain("Blind").And.NotContain("Hosting");
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
    public void Control_TheReferenceCheck_WouldSeeAnAssemblyReference_IfThereWasOne()
    {
        // The scanner's AssemblyRef path, on this library with an assembly it really references declared forbidden.
        var dll = typeof(BeeMemoryBank.Platforms.Apple.Keychain.IKeychainBackend).Assembly.Location;
        var forbidden = new AssemblyBoundaryScanner.Forbidden(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { "System.Runtime" });

        AssemblyBoundaryScanner.Scan([dll], forbidden).Should().Contain(f => f.Kind == "references assembly" && f.Detail == "System.Runtime");
    }
}
