using BeeMemoryBank.Boundary;

namespace BeeMemoryBank.MacFullPackage.Tests;

/// <summary>
/// Where the staged full app is. BMB_FULL_SCAN_DIR names the "Bee Memory Bank.app" (the release process and the Mac gate set it).
/// Not set: the tests that need it are SKIPPED, unless BMB_REQUIRE_SCAN is set - then they run and FAIL, so that a gate that is meant to
/// scan can never pass by scanning nothing (the same convention as BMB_SCAN_DIRS in the blind boundary tests).
/// </summary>
internal static class FullAppScan
{
    public const string DirVariable = "BMB_FULL_SCAN_DIR";
    public const string RequireVariable = "BMB_REQUIRE_SCAN";

    public static string? ConfiguredDir => Environment.GetEnvironmentVariable(DirVariable) is { Length: > 0 } d ? d.Trim() : null;
    public static bool ScanRequired => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RequireVariable));

    /// <summary>The .app folder.</summary>
    public static string App()
    {
        var dir = ConfiguredDir;
        if (dir is null) throw new Xunit.Sdk.XunitException($"{RequireVariable} is set, so {DirVariable} must name the staged 'Bee Memory Bank.app'");
        Directory.Exists(dir).Should().BeTrue($"{DirVariable} names an existing folder: {dir}");
        Directory.Exists(Path.Combine(dir, "Contents", "MacOS")).Should().BeTrue($"{dir} is an app bundle: it has Contents/MacOS");
        return dir;
    }

    public static string MacOs() => Path.Combine(App(), "Contents", "MacOS");

    /// <summary>The signing manifest the packer writes beside the app: "&lt;app name without .app&gt;.signing-manifest.txt".</summary>
    public static string ManifestPath()
    {
        var app = App().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Combine(Path.GetDirectoryName(app)!, Path.GetFileNameWithoutExtension(app) + ".signing-manifest.txt");
    }
}

/// <summary>A test of the staged app: skipped when BMB_FULL_SCAN_DIR is not set (and BMB_REQUIRE_SCAN is not either).</summary>
public sealed class FullAppFactAttribute : FactAttribute
{
    public FullAppFactAttribute()
    {
        if (FullAppScan.ConfiguredDir is null && !FullAppScan.ScanRequired)
            Skip = $"set {FullAppScan.DirVariable} to the staged 'Bee Memory Bank.app' (scripts/pack-macos-full.sh builds it)";
    }
}

/// <summary>The staged full app (BMB_FULL_SCAN_DIR): its layout, its Vault, its plist, its Mach-O files against the signing manifest.</summary>
public class FullAppPackageTests
{
    [FullAppFact]
    public void TheStagedApp_HasTheFullLayout_FiveProducts_OneSharedRuntime_NoPdb_NoData()
    {
        FullAppLayout.DirectoryViolations(FullAppScan.MacOs()).Should().BeEmpty();
    }

    [FullAppFact]
    public void TheStagedApp_CarriesTheRealVault_InTheNodeAndInTheApi()
    {
        // The full app deliberately contains the Vault (it is the Vault's home); the blind rule is the opposite and is proved elsewhere.
        // "Real" means: the file defines the vault types, not an empty stand-in of the same name.
        var forbidden = VaultBoundary.Forbidden();
        foreach (var home in FullAppLayout.VaultHomes)
        {
            var dll = Path.Combine(FullAppScan.MacOs(), home, FullAppLayout.VaultDll);
            File.Exists(dll).Should().BeTrue($"{home}/{FullAppLayout.VaultDll}");
            var findings = AssemblyBoundaryScanner.Scan([dll], forbidden);
            findings.Should().Contain(f => f.Kind == "defines type" && f.Detail == "BeeMemoryBank.Core.Services.SessionService", dll);
        }
    }

    [FullAppFact]
    public void TheStagedApp_InfoPlist_NamesTheAppItsExecutableAndItsPrivacyKeys()
    {
        // the raw VERSION file: the rules trim it exactly as the packer does (2.0.3-preview1 -> 2.0.3)
        var versionFile = File.ReadAllText(Path.Combine(VaultBoundary.RepoRoot(), "VERSION"));
        var plist = Path.Combine(FullAppScan.App(), "Contents", "Info.plist");
        FullAppLayout.PlistViolations(plist, versionFile).Should().BeEmpty();
        File.ReadAllText(Path.Combine(FullAppScan.App(), "Contents", "PkgInfo")).Should().Be("APPL????");
    }

    [FullAppFact]
    public void TheStagedApp_EveryMachOFile_IsInTheSigningManifest_AndEveryEntryIsAMachOFile()
    {
        var app = FullAppScan.App();
        var manifestPath = FullAppScan.ManifestPath();
        File.Exists(manifestPath).Should().BeTrue("the packer writes the signing manifest beside the app: " + manifestPath);
        var manifest = File.ReadAllLines(manifestPath).Where(l => l.Length > 0).ToList();
        manifest.Should().NotBeEmpty();
        manifest.Should().OnlyHaveUniqueItems().And.BeInAscendingOrder(StringComparer.Ordinal);

        // the second, independent scan: the header of EVERY file, whatever its name (symbolic links are not followed)
        var all = FullAppLayout.Files(app);
        var machO = all.Where(f => { using var s = File.OpenRead(Path.Combine(app, f)); return MachO.Inspect(s).IsMachO; })
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        machO.Should().Equal(manifest, "the manifest lists exactly the Mach-O files of the bundle");

        // codesign will not seal the app until every OTHER file under Contents/MacOS is signed too: the second list names them, exactly
        var otherPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, Path.GetFileNameWithoutExtension(manifestPath) + "-other.txt");
        File.Exists(otherPath).Should().BeTrue("the packer writes the list of the other signable files beside the manifest: " + otherPath);
        var other = File.ReadAllLines(otherPath).Where(l => l.Length > 0).ToList();
        other.Should().OnlyHaveUniqueItems().And.BeInAscendingOrder(StringComparer.Ordinal);
        var expectedOther = all.Where(f => f.StartsWith("Contents/MacOS/", StringComparison.Ordinal) && !manifest.Contains(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        other.Should().Equal(expectedOther, "the second list is every file under Contents/MacOS that is not a Mach-O file");

        // every one of them must run on Apple silicon
        foreach (var rel in manifest)
        {
            using var s = File.OpenRead(Path.Combine(app, rel));
            MachO.Inspect(s).HasArm64.Should().BeTrue(rel + " has an arm64 slice");
        }

        // the programs of the five products and the runtime's muxer are in it
        var macOs = "Contents/MacOS/";
        foreach (var program in FullAppLayout.Hosts.Append("dotnet/dotnet"))
            manifest.Should().Contain(macOs + program);
    }

    [FullAppFact]
    public void TheStagedApp_IsNotInsideTheDataFolder_AndHoldsNoDataOfItsOwn()
    {
        var app = Path.GetFullPath(FullAppScan.App());
        // BmbPaths puts the data under LocalApplicationData/BeeMemoryBankData (~/Library/Application Support/BeeMemoryBankData on a Mac)
        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeeMemoryBankData");
        app.StartsWith(Path.GetFullPath(dataRoot) + Path.DirectorySeparatorChar, StringComparison.Ordinal).Should().BeFalse("the app is not inside the data folder");
        FullAppLayout.Violations(FullAppLayout.Files(FullAppScan.MacOs())).Should().NotContain(v => v.Contains("data-folder"));
    }

    // ---- controls: the rules must be able to fail ---------------------------------------------------------------------------------

    /// <summary>A complete listing of the full app, as the packer stages it (a few representative files of each part).</summary>
    private static List<string> CompleteListing()
    {
        var files = new List<string> { "BeeMemoryBank.Desktop.dll", "BeeMemoryBank.Desktop.deps.json", "BeeMemoryBank.Desktop.runtimeconfig.json" };
        foreach (var host in FullAppLayout.Hosts)
        {
            files.Add(host);
            files.Add(host + ".dll");
            files.Add(host + ".runtimeconfig.json");
            files.Add(host + ".deps.json");
        }
        files.AddRange(FullAppLayout.NativeLibraries);
        files.AddRange(FullAppLayout.VaultHomes.Select(h => $"{h}/{FullAppLayout.VaultDll}"));
        files.AddRange(["web/wwwroot/css/site.css", "web/wwwroot/js/site.js"]);
        files.AddRange(["dotnet/dotnet", "dotnet/host/fxr/v10-0-8/libhostfxr.dylib",
            "dotnet/shared/fx-netcore/v10-0-8/libcoreclr.dylib", "dotnet/shared/fx-netcore/v10-0-8/System.Private.CoreLib.dll",
            "dotnet/shared/fx-aspnetcore/v10-0-8/Microsoft.AspNetCore.dll"]);
        return files.Distinct(StringComparer.Ordinal).ToList();
    }

    [Theory]
    [InlineData("2.0.2", "2.0.2")]
    [InlineData("2.0.2\r\n", "2.0.2")]
    [InlineData("2.0.3-preview1", "2.0.3")]
    [InlineData("2.0.3+build.7", "2.0.3")]
    [InlineData("2.0.3.4", "2.0.3")]
    [InlineData(" 10.20.30-rc.1 ", "10.20.30")]
    public void Control_TheBundleVersion_IsTheFirstThreeNumbersOfVERSION_LikeThePacker(string versionFile, string expected)
    {
        FullAppLayout.BundleVersion(versionFile).Should().Be(expected);
    }

    private static string PlistText(string version) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>CFBundleIdentifier</key><string>com.beememorybank.desktop</string>
          <key>CFBundleExecutable</key><string>BeeMemoryBank.Desktop</string>
          <key>CFBundleName</key><string>Bee Memory Bank</string>
          <key>CFBundleDisplayName</key><string>Bee Memory Bank</string>
          <key>CFBundlePackageType</key><string>APPL</string>
          <key>CFBundleShortVersionString</key><string>{version}</string>
          <key>CFBundleVersion</key><string>{version}</string>
          <key>LSApplicationCategoryType</key><string>public.app-category.productivity</string>
          <key>LSMinimumSystemVersion</key><string>14.0</string>
          <key>LSMultipleInstancesProhibited</key><true/>
          <key>NSHighResolutionCapable</key><true/>
          <key>NSLocalNetworkUsageDescription</key><string>why</string>
          <key>NSBonjourServices</key><array><string>_beememorybank._tcp</string></array>
          <key>NSAppTransportSecurity</key><dict><key>NSAllowsLocalNetworking</key><true/></dict>
        </dict></plist>
        """;

    [Fact]
    public void Control_APlistWithTheTrimmedVersion_PassesForASuffixedVersionFile_AndAWrongVersionIsRefused()
    {
        FullAppLayout.PlistViolationsFromXml(PlistText("2.0.3"), "2.0.3-preview1\n").Should().BeEmpty();
        FullAppLayout.PlistViolationsFromXml(PlistText("2.0.3-preview1"), "2.0.3-preview1\n").Should().HaveCount(2, "the untrimmed version is not what the packer writes");
        FullAppLayout.PlistViolationsFromXml(PlistText("2.0.4"), "2.0.3-preview1\n").Should().HaveCount(2);
    }

    [Fact]
    public void Control_ACompleteListing_HasNoViolation()
    {
        FullAppLayout.Violations(CompleteListing()).Should().BeEmpty();
    }

    [Fact]
    public void Control_TheStaticWebFilesInResources_AreAcceptedOnlyThroughTheLink()
    {
        var withoutWwwroot = CompleteListing().Where(f => !f.StartsWith("web/wwwroot/", StringComparison.Ordinal)).ToList();
        FullAppLayout.Violations(withoutWwwroot, wwwrootLinked: true).Should().BeEmpty();
        FullAppLayout.Violations(withoutWwwroot, wwwrootLinked: false).Should().Equal("web/wwwroot is missing or empty");
    }

    [Fact]
    public void Control_AListingWithoutTheVault_IsRefused()
    {
        var files = CompleteListing().Where(f => !f.EndsWith("/" + FullAppLayout.VaultDll, StringComparison.Ordinal)).ToList();
        FullAppLayout.Violations(files).Should().BeEquivalentTo(FullAppLayout.VaultHomes.Select(FullAppLayout.VaultMessage));
    }

    [Theory]
    [InlineData("bmbd")]
    [InlineData("api")]
    public void Control_AListingWithTheVaultInOnlyOneHome_IsRefused(string missingHome)
    {
        var files = CompleteListing().Where(f => f != $"{missingHome}/{FullAppLayout.VaultDll}").ToList();
        FullAppLayout.Violations(files).Should().Equal(FullAppLayout.VaultMessage(missingHome));
    }

    [Fact]
    public void Control_ThisTestProjectsOwnOutputFolder_IsNotAFullApp_AndHasNoVault()
    {
        // A real folder without the Vault (and without everything else): the same rules must refuse it, naming the Vault.
        var violations = FullAppLayout.DirectoryViolations(AppContext.BaseDirectory);
        violations.Should().Contain(FullAppLayout.VaultMessage("bmbd")).And.Contain(FullAppLayout.VaultMessage("api"));
        violations.Count.Should().BeGreaterThan(5);
    }

    [Fact]
    public void Control_TheVaultScanOfAnAssemblyThatIsNotTheVault_FindsNothing()
    {
        // The positive test above ("defines SessionService") only means something if the scan does not flag every assembly.
        var thisAssembly = typeof(FullAppPackageTests).Assembly.Location;
        AssemblyBoundaryScanner.Scan([thisAssembly], VaultBoundary.Forbidden()).Should().BeEmpty();
    }

    [Fact]
    public void Control_APdbFile_IsRefused()
    {
        FullAppLayout.Violations(CompleteListing().Append("bmbd/BeeMemoryBank.Node.pdb")).Should().ContainSingle(v => v.Contains(".pdb"));
    }

    [Fact]
    public void Control_ASecondCopyOfTheRuntime_IsRefused()
    {
        FullAppLayout.Violations(CompleteListing().Append("api/libcoreclr.dylib")).Should().ContainSingle(v => v.Contains("libcoreclr.dylib"));
    }

    [Fact]
    public void Control_AMissingRuntime_IsRefused()
    {
        var files = CompleteListing().Where(f => !f.StartsWith("dotnet/shared/fx-netcore/", StringComparison.Ordinal)).ToList();
        FullAppLayout.Violations(files).Should().Contain(v => v.Contains("libcoreclr.dylib"));
    }

    [Fact]
    public void Control_TheRuntimeInTheStandardDottedLayout_IsRefused()
    {
        // The layout the host would like (real directories shared/Microsoft.NETCore.App/10.0.8): codesign takes them for bundles.
        var files = CompleteListing().Where(f => !f.StartsWith("dotnet/shared/", StringComparison.Ordinal) && !f.StartsWith("dotnet/host/", StringComparison.Ordinal)).ToList();
        files.AddRange(["dotnet/host/fxr/10.0.8/libhostfxr.dylib", "dotnet/shared/Microsoft.NETCore.App/10.0.8/libcoreclr.dylib",
            "dotnet/shared/Microsoft.NETCore.App/10.0.8/System.Private.CoreLib.dll", "dotnet/shared/Microsoft.AspNetCore.App/10.0.8/Microsoft.AspNetCore.dll"]);
        var violations = FullAppLayout.Violations(files);
        violations.Should().Contain(v => v.Contains("real directory with a dot"));
        violations.Should().Contain(v => v.Contains("libcoreclr.dylib is not in the shared runtime folder"));
    }

    [Fact]
    public void Control_AWebFolderWithADottedName_IsRefused()
    {
        FullAppLayout.Violations(CompleteListing().Append("web/wwwroot/lib/chart.js/chart.min.js")).Should().ContainSingle(v => v.Contains("real directory with a dot"));
    }

    private static List<FullAppLayout.Entry> CompleteTree() =>
    [
        new("dotnet", true, false), new("dotnet/host", true, false), new("dotnet/host/fxr", true, false),
        new("dotnet/host/fxr/v10-0-8", true, false), new("dotnet/host/fxr/10.0.8", true, true),
        new("dotnet/shared", true, false), new("dotnet/shared/fx-netcore", true, false), new("dotnet/shared/fx-netcore/v10-0-8", true, false),
        new("dotnet/shared/fx-netcore/10.0.8", true, true), new("dotnet/shared/Microsoft.NETCore.App", true, true),
        new("dotnet/shared/fx-aspnetcore", true, false), new("dotnet/shared/fx-aspnetcore/v10-0-8", true, false),
        new("dotnet/shared/fx-aspnetcore/10.0.8", true, true), new("dotnet/shared/Microsoft.AspNetCore.App", true, true),
        new("web", true, false), new("web/wwwroot", true, false), new("web/wwwroot/lib", true, false),
    ];

    [Fact]
    public void Control_TheTreeRules_AcceptTheLinkedRuntimeLayout()
    {
        FullAppLayout.TreeViolations(CompleteTree()).Should().BeEmpty();
    }

    [Fact]
    public void Control_ARealDottedDirectory_OrAMissingLink_IsRefused()
    {
        var withReal = CompleteTree().Where(e => e.Path != "dotnet/shared/Microsoft.NETCore.App").Append(new("dotnet/shared/Microsoft.NETCore.App", true, false)).ToList();
        FullAppLayout.TreeViolations(withReal).Should().Contain(v => v.Contains("dotnet/shared/Microsoft.NETCore.App is a real directory with a dot"))
            .And.Contain(v => v.Contains("dotnet/shared/Microsoft.NETCore.App must be a symbolic link"));
        var withoutVersionLink = CompleteTree().Where(e => e.Path != "dotnet/host/fxr/10.0.8").ToList();
        FullAppLayout.TreeViolations(withoutVersionLink).Should().ContainSingle(v => v.Contains("dotnet/host/fxr/<version> must be a symbolic link"));
    }

    [Fact]
    public void Control_ThisTestProjectsOwnOutputFolder_HasNoRealDottedDirectoryAndNoRuntimeLinks()
    {
        // a real tree: the rules read the file system the same way as for the staged app, and refuse a folder that is not the app
        var violations = FullAppLayout.TreeViolations(FullAppLayout.Tree(AppContext.BaseDirectory));
        violations.Should().Contain(v => v.Contains("must be a symbolic link"));
    }

    [Fact]
    public void Control_AMissingHost_IsRefused()
    {
        var files = CompleteListing().Where(f => f != "web/BeeMemoryBank.Web").ToList();
        FullAppLayout.Violations(files).Should().Equal("missing host: web/BeeMemoryBank.Web");
    }

    [Fact]
    public void Control_FilesOfTheBlindDesktopApp_AreRefused()
    {
        FullAppLayout.Violations(CompleteListing().Append("BeeMemoryBank.BlindDesktop.dll")).Should().ContainSingle(v => v.Contains("blind desktop"));
    }

    [Theory]
    [InlineData("api/beememorybank.db")]
    [InlineData("api/.runtime.json")]
    [InlineData("data/profiles.json")]
    [InlineData("BeeMemoryBankData/x.bin")]
    [InlineData("api/data/chat.db")]
    public void Control_DataInsideTheBundle_IsRefused(string dataFile)
    {
        FullAppLayout.Violations(CompleteListing().Append(dataFile)).Should().ContainSingle(v => v.Contains("data folder"));
    }

    [Fact]
    public void Control_TheModel_IsAcceptedBesideTheApiOnly()
    {
        FullAppLayout.Violations(CompleteListing().Append("api/model.onnx")).Should().BeEmpty();
        FullAppLayout.Violations(CompleteListing().Append("cli/model.onnx")).Should().ContainSingle(v => v.Contains("model.onnx"));
    }

    [Fact]
    public void Control_AnEmptyListing_IsRefused()
    {
        FullAppLayout.Violations([]).Should().Equal("the package holds no file");
    }

    [Fact]
    public void Control_TheMachOReader_TellsThinFatAndForeignFilesApart()
    {
        static MachO.Info Read(params byte[] bytes) { using var s = new MemoryStream(bytes); return MachO.Inspect(s); }
        // thin arm64 (cputype 0x0100000C, stored little-endian) and thin x86_64 (0x01000007)
        Read(0xCF, 0xFA, 0xED, 0xFE, 0x0C, 0x00, 0x00, 0x01).Should().Be(new MachO.Info(true, true));
        Read(0xCF, 0xFA, 0xED, 0xFE, 0x07, 0x00, 0x00, 0x01).Should().Be(new MachO.Info(true, false));
        // fat with two slices, x86_64 first and arm64 second; and fat with x86_64 only (entries are 20 bytes, big-endian)
        var fat = new byte[8 + 40];
        new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 2 }.CopyTo(fat, 0);
        new byte[] { 0x01, 0x00, 0x00, 0x07 }.CopyTo(fat, 8);
        new byte[] { 0x01, 0x00, 0x00, 0x0C }.CopyTo(fat, 28);
        Read(fat).Should().Be(new MachO.Info(true, true));
        new byte[] { 0x01, 0x00, 0x00, 0x07 }.CopyTo(fat, 28);
        Read(fat).Should().Be(new MachO.Info(true, false));
        // a Java class file starts with the same four bytes as a fat header; its next four bytes are a version, not a slice count
        Read(0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x41).Should().Be(new MachO.Info(false, false));
        // a managed assembly, a text file, an empty file
        Read(0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00).Should().Be(new MachO.Info(false, false));
        Read(0x7B, 0x22, 0x61, 0x22, 0x3A, 0x31, 0x7D, 0x0A).Should().Be(new MachO.Info(false, false));
        Read().Should().Be(new MachO.Info(false, false));
    }
}
