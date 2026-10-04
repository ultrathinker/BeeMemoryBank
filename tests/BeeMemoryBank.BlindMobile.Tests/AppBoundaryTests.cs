using BeeMemoryBank.Boundary;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// The Android blind app does not contain the vault (BMB-99), proved on its compiled output rather than on its project file: every
/// BeeMemoryBank.* assembly the app build produced is read at the metadata level and must not define a vault type, reference one, define
/// or call a member that hands out the master data key, or reference the vault assembly. Both Debug and Release outputs are scanned when
/// they exist (the Release one is what ships; the trimmed APK content is checked by the release process).
/// </summary>
public class AppBoundaryTests
{
    private static string RepoRoot() => VaultBoundary.RepoRoot();

    private static List<string> OutputFolders() =>
        new[] { "Debug", "Release" }
            .Select(c => Path.Combine(RepoRoot(), "mobile", "BeeMemoryBank.BlindMobile", "bin", c, "net10.0-android"))
            .Where(d => File.Exists(Path.Combine(d, "BeeMemoryBank.BlindMobile.dll")))
            .ToList();

    [Fact]
    public void TheAppOutput_ContainsNoVaultCode()
    {
        var folders = OutputFolders();
        folders.Should().NotBeEmpty("build the Android blind app first (Debug or Release)");
        foreach (var folder in folders)
        {
            var files = AssemblyBoundaryScanner.ApplicationAssemblies(folder);
            files.Select(Path.GetFileNameWithoutExtension).Should().Contain(["BeeMemoryBank.BlindMobile", "BeeMemoryBank.Core", "BeeMemoryBank.Sync"],
                "the scan must see the app and the shared libraries it carries: " + folder);
            files.Select(Path.GetFileNameWithoutExtension).Should().NotContain("BeeMemoryBank.Vault", folder);
            AssemblyBoundaryScanner.Scan(files, VaultBoundary.Forbidden()).Should().BeEmpty(folder);
            AssemblyBoundaryScanner.DuplicatedTypes(files).Should().BeEmpty(folder);
        }
    }

    [Fact]
    public void Control_TheScanFlagsTheVaultWhenItIsGivenTheVault()
    {
        // This test project references the Vault (the tests play the PC that pairs a phone), so its own output holds the real thing:
        // the same scan over it must light up. If it did not, the clean result above would mean nothing.
        var vault = Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.Vault.dll");
        File.Exists(vault).Should().BeTrue("the tests reference the vault");
        var findings = AssemblyBoundaryScanner.Scan([vault], VaultBoundary.Forbidden());
        findings.Should().Contain(f => f.Kind == "defines type" && f.Detail == "BeeMemoryBank.Core.Services.SessionService");
    }

    [Fact]
    public void ThePublishedFolders_NamedInBMB_SCAN_DIRS_ContainNoVaultCode()
    {
        // The trimmed Release package: the release process extracts the APK's assemblies (or points at the linker output) and names the folder.
        var dirs = (Environment.GetEnvironmentVariable("BMB_SCAN_DIRS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (dirs.Length == 0)
            Environment.GetEnvironmentVariable("BMB_REQUIRE_SCAN").Should().BeNullOrEmpty("BMB_REQUIRE_SCAN is set, so BMB_SCAN_DIRS must name the trimmed package folder");
        foreach (var dir in dirs)
        {
            Directory.Exists(dir).Should().BeTrue(dir);
            var files = Directory.GetFiles(dir, "BeeMemoryBank.*.dll", SearchOption.AllDirectories);
            files.Should().NotBeEmpty(dir);
            AssemblyBoundaryScanner.Scan(files, VaultBoundary.Forbidden()).Should().BeEmpty(dir);
        }
    }
}
