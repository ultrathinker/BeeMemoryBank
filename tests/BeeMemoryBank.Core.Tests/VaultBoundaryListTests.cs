using System.Reflection;
using BeeMemoryBank.Boundary;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Keeps the vault boundary honest (BMB-99). This project references the Vault and every shared library, so it is where the list of
/// vault types is generated and checked, and where the scanner that the blind projects rely on is shown to work on real assemblies:
/// the controls below fail if the scanner stops seeing what it is there to see.
/// </summary>
public class VaultBoundaryListTests
{
    private static readonly Assembly Vault = typeof(SessionService).Assembly;

    private static string Location(Assembly a) => a.Location;

    private static readonly Assembly[] Shared =
    [
        typeof(BeeMemoryBank.Core.Models.NodeIdentity).Assembly,
        typeof(BeeMemoryBank.Crypto.Ed25519Signer).Assembly,
        typeof(BeeMemoryBank.Storage.Sqlite.MigrationRunner).Assembly,
        typeof(BeeMemoryBank.Sync.EventApplier).Assembly,
        typeof(BeeMemoryBank.Search.DefaultTokenizer).Assembly
    ];

    [Fact]
    public void TheVaultIsItsOwnAssembly_AndNotOneOfTheSharedOnes()
    {
        Vault.GetName().Name.Should().Be("BeeMemoryBank.Vault");
        Shared.Select(a => a.GetName().Name).Should().NotContain("BeeMemoryBank.Vault");
        Shared.Select(a => a.GetName().Name).Should().BeEquivalentTo(
            ["BeeMemoryBank.Core", "BeeMemoryBank.Crypto", "BeeMemoryBank.Storage", "BeeMemoryBank.Sync", "BeeMemoryBank.Search"]);
    }

    [Fact]
    public void TheListedVaultTypesAreExactlyTheTypesTheVaultDefines()
    {
        var actual = AssemblyBoundaryScanner.DefinedTypes(Location(Vault)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        actual.Should().NotBeEmpty();
        var file = Path.Combine(VaultBoundary.RepoRoot(), VaultBoundary.ListFile.Replace('/', Path.DirectorySeparatorChar));
        if (Environment.GetEnvironmentVariable("BMB_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(file,
                "# GENERATED from BeeMemoryBank.Vault.dll by VaultBoundaryListTests (BMB_UPDATE_GOLDEN=1). One full type name per line.\n" +
                "# A blind node (host, console, CLI, Android app) must contain none of these types and reference none of them.\n" +
                string.Join("\n", actual) + "\n");
            return;
        }
        VaultBoundary.ListedVaultTypes().Should().Equal(actual,
            "a new or removed vault type changes the boundary the blind guards enforce; regenerate with BMB_UPDATE_GOLDEN=1 and review the diff");
    }

    [Fact]
    public void NoSharedAssemblyDefinesAVaultType_AndNoTypeIsDefinedTwice()
    {
        var listed = VaultBoundary.ListedVaultTypes().ToHashSet(StringComparer.Ordinal);
        foreach (var assembly in Shared)
            AssemblyBoundaryScanner.DefinedTypes(Location(assembly)).Where(listed.Contains).Should().BeEmpty(
                assembly.GetName().Name + " must not define a type of the vault");
        AssemblyBoundaryScanner.DuplicatedTypes(Shared.Append(Vault).Select(Location)).Should().BeEmpty(
            "a type name defined by two assemblies could be loaded from either");
    }

    [Fact]
    public void TheSharedAssemblies_ContainNoVaultCode()
    {
        var findings = AssemblyBoundaryScanner.Scan(Shared.Select(Location), VaultBoundary.Forbidden());
        findings.Should().BeEmpty("the shared libraries are what a blind node contains");
    }

    // ---- controls: the scanner must find what is there -------------------------------------------------------------------

    [Fact]
    public void Control_TheVaultItselfIsFlaggedByEveryKindOfFinding()
    {
        var forbidden = VaultBoundary.Forbidden();
        var findings = AssemblyBoundaryScanner.Scan([Location(Vault)], forbidden);
        findings.Where(f => f.Kind == "defines type").Select(f => f.Detail).Should().Contain("BeeMemoryBank.Core.Services.SessionService")
            .And.Contain("BeeMemoryBank.Crypto.MasterKeyManager");
        findings.Count(f => f.Kind == "defines type").Should().BeGreaterThan(100, "the vault defines well over a hundred types");
        findings.Should().Contain(f => f.Kind == "defines member" && f.Detail.EndsWith(".GetMasterDek"),
            "SessionService.GetMasterDek is a forbidden member name");
    }

    [Fact]
    public void Control_AForbiddenNameInjectedIntoAnAllowedAssemblyIsFound()
    {
        // The real shared assemblies, with names that really exist in them declared forbidden: each path of the scanner must fire.
        var sync = Location(typeof(BeeMemoryBank.Sync.EventApplier).Assembly);
        var storage = Location(typeof(BeeMemoryBank.Storage.Sqlite.MigrationRunner).Assembly);
        var forbidden = new AssemblyBoundaryScanner.Forbidden(
            new HashSet<string> { "BeeMemoryBank.Sync.EventApplier", "BeeMemoryBank.Storage.Sqlite.MigrationRunner" },
            new HashSet<string> { "RunMigrationsAsync", "GetByIdAsync" },
            new HashSet<string> { "BeeMemoryBank.Core" });

        var findings = AssemblyBoundaryScanner.Scan([sync, storage, Location(Vault)], forbidden);
        findings.Should().Contain(f => f.Kind == "defines type" && f.Assembly == "BeeMemoryBank.Sync" && f.Detail == "BeeMemoryBank.Sync.EventApplier");
        findings.Should().Contain(f => f.Kind == "defines type" && f.Assembly == "BeeMemoryBank.Storage");
        findings.Should().Contain(f => f.Kind == "defines member" && f.Assembly == "BeeMemoryBank.Storage" && f.Detail.EndsWith(".RunMigrationsAsync"));
        findings.Should().Contain(f => f.Kind == "references type" && f.Assembly == "BeeMemoryBank.Vault", "the vault mentions shared types");
        findings.Should().Contain(f => f.Kind == "references member" && f.Detail == "GetByIdAsync", "shared and vault code call repositories across assemblies");
        findings.Should().Contain(f => f.Kind == "references assembly" && f.Detail == "BeeMemoryBank.Core");
    }

    [Fact]
    public void Control_AnEmptyScanFailsInsteadOfPassing()
    {
        var act = () => AssemblyBoundaryScanner.Scan([], VaultBoundary.Forbidden());
        act.Should().Throw<InvalidOperationException>();
    }
}
