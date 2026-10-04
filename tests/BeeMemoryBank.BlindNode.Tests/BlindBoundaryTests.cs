using System.Reflection;
using System.Reflection.Emit;
using BeeMemoryBank.Boundary;

namespace BeeMemoryBank.BlindNode.Tests;

/// <summary>
/// The proof that the blind host does not contain the vault (BMB-99), made on the compiled assemblies, not on project files: every
/// application assembly of the host's closure is read at the metadata level and must not DEFINE a vault type, REFERENCE one, define or call a member that
/// hands out the master data key, or reference the vault assembly. Controls below make sure the scan sees what it is meant to see.
/// The same scanner runs over real publish folders and the Docker image file system when BMB_SCAN_DIRS names them (release verification).
/// </summary>
public class BlindBoundaryTests
{
    private static IReadOnlyList<string> ClosureFiles() =>
        BlindNodeCompositionTests.ClosureApplicationAssemblies().Select(a => a.Location).ToList();

    [Fact]
    public void TheHostsApplicationAssemblies_ContainNoVaultCode()
    {
        var files = ClosureFiles();
        var findings = AssemblyBoundaryScanner.Scan(files, VaultBoundary.Forbidden());
        findings.Should().BeEmpty("the blind host is made of the host and the shared libraries; vault types, members and references must not appear in any of them");
        AssemblyBoundaryScanner.DuplicatedTypes(files).Should().BeEmpty("one definition per type across the host's assemblies");
    }

    [Fact]
    public void TheHostsOutputFolder_HasNoVaultAssembly()
    {
        Directory.GetFiles(AppContext.BaseDirectory, "BeeMemoryBank.Vault.dll").Should().BeEmpty();
        ClosureFiles().Select(f => Path.GetFileNameWithoutExtension(f)).Should().NotContain("BeeMemoryBank.Vault");
    }

    // ---- controls ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Control_TheScanSeesTheAssembliesAndTypesItIsMeantToSee()
    {
        var files = ClosureFiles();
        var names = files.Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
        names.Should().Contain(["BeeMemoryBank.BlindNode", "BeeMemoryBank.Core", "BeeMemoryBank.Storage", "BeeMemoryBank.Sync", "BeeMemoryBank.Crypto", "BeeMemoryBank.Search"]);

        var defined = files.SelectMany(AssemblyBoundaryScanner.DefinedTypes).ToHashSet(StringComparer.Ordinal);
        defined.Should().Contain("BeeMemoryBank.Sync.EventApplier", "the shared sync engine");
        defined.Should().Contain("BeeMemoryBank.Storage.Sqlite.MigrationRunner", "migrations");
        defined.Should().Contain("BeeMemoryBank.Api.Services.SnapshotService", "the package engine (linked Api source) is inside the host assembly");
        defined.Should().Contain("BeeMemoryBank.Sync.BlindEventLogger", "the refusing event logger stands in for the vault's");
        VaultBoundary.ListedVaultTypes().Count.Should().BeGreaterThan(100);
        defined.Intersect(VaultBoundary.ListedVaultTypes()).Should().BeEmpty();
    }

    [Fact]
    public void Control_ANameThatReallyExistsInTheHostIsFoundByEveryPath()
    {
        var files = ClosureFiles();
        var forbidden = new AssemblyBoundaryScanner.Forbidden(
            new HashSet<string> { "BeeMemoryBank.Sync.EventApplier" },
            new HashSet<string> { "RunMigrationsAsync" },
            new HashSet<string> { "BeeMemoryBank.Crypto" });
        var findings = AssemblyBoundaryScanner.Scan(files, forbidden);
        findings.Should().Contain(f => f.Kind == "defines type" && f.Assembly == "BeeMemoryBank.Sync");
        findings.Should().Contain(f => f.Kind == "references type" && f.Assembly == "BeeMemoryBank.BlindNode", "the host's own code mentions the event applier");
        findings.Should().Contain(f => f.Kind == "defines member" && f.Assembly == "BeeMemoryBank.Storage");
        findings.Should().Contain(f => f.Kind == "references member");
        findings.Should().Contain(f => f.Kind == "references assembly", "the shared libraries reference Crypto");
    }

    [Fact]
    public void Control_AForbiddenTypeBuiltIntoAnAssemblyIsFound()
    {
        // A fixture assembly with a vault type in it, named like an allowed one: the scan of a "publish" containing it must fail.
        var dir = Path.Combine(Path.GetTempPath(), "bmb_boundary_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "BeeMemoryBank.Fixture.dll");
            var builder = new PersistedAssemblyBuilder(new AssemblyName("BeeMemoryBank.Fixture"), typeof(object).Assembly);
            var module = builder.DefineDynamicModule("BeeMemoryBank.Fixture");
            module.DefineType("BeeMemoryBank.Core.Services.SessionService", TypeAttributes.Public | TypeAttributes.Class).CreateType();
            builder.Save(path);

            var findings = AssemblyBoundaryScanner.Scan([path], VaultBoundary.Forbidden());
            findings.Should().ContainSingle(f => f.Kind == "defines type" && f.Detail == "BeeMemoryBank.Core.Services.SessionService");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* a handle on Windows; the temp folder is reaped */ }
        }
    }

    [Fact]
    public void Control_TheSameTypeInTwoAssembliesIsReportedAsADuplicate()
    {
        // The duplicate check must fire on real input: the same assembly twice is the same types twice.
        var core = typeof(BeeMemoryBank.Core.Models.NodeIdentity).Assembly.Location;
        AssemblyBoundaryScanner.DuplicatedTypes([core, core]).Should().NotBeEmpty();
        AssemblyBoundaryScanner.DuplicatedTypes([core]).Should().BeEmpty();
    }

    // ---- release verification: publish folders and the image file system ------------------------------------------------

    /// <summary>
    /// Scans the folders named in BMB_SCAN_DIRS (separated by ';'): publish outputs of the blind host, console and CLI, an extracted image file
    /// system. Without the variable there is nothing to scan and the test only records that; the release process sets it (S5).
    /// </summary>
    [Fact]
    public void PublishedFolders_NamedInBMB_SCAN_DIRS_ContainNoVaultCode()
    {
        var dirs = (Environment.GetEnvironmentVariable("BMB_SCAN_DIRS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (dirs.Length == 0)
        {
            // Opt-in locally; the CI job and the release process set BMB_REQUIRE_SCAN so that a missing variable fails instead of passing quietly.
            Environment.GetEnvironmentVariable("BMB_REQUIRE_SCAN").Should().BeNullOrEmpty("BMB_REQUIRE_SCAN is set, so BMB_SCAN_DIRS must name the published folders");
            return;
        }
        foreach (var dir in dirs)
        {
            Directory.Exists(dir).Should().BeTrue(dir + " must exist");
            // The application's own assemblies: BeeMemoryBank.*.dll, and the blind CLI's `bmb.dll`.
            var files = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories)
                .Where(f => Path.GetFileName(f).StartsWith("BeeMemoryBank.", StringComparison.OrdinalIgnoreCase) ||
                            Path.GetFileName(f).Equals("bmb.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
            files.Should().NotBeEmpty(dir + " must contain application assemblies, or the scan proves nothing");
            var found = AssemblyBoundaryScanner.Scan(files, VaultBoundary.Forbidden());
            // Evidence for the release report: BMB_SCAN_REPORT names a file that receives one line per folder and one per finding.
            if (Environment.GetEnvironmentVariable("BMB_SCAN_REPORT") is { Length: > 0 } report)
                File.AppendAllLines(report, new[] { $"{dir}: {files.Length} application assemblies, {found.Count} findings" }.Concat(found.Select(f => "  " + f)));
            files.Select(Path.GetFileName).Should().NotContain("BeeMemoryBank.Vault.dll", dir);
            found.Should().BeEmpty(dir);
            // The same assembly may be copied into several sub-folders of an image (api/, console/, cli/): duplicates are judged per folder.
            foreach (var group in files.GroupBy(f => Path.GetDirectoryName(f)))
                AssemblyBoundaryScanner.DuplicatedTypes(group).Should().BeEmpty(group.Key);
        }
    }
}
