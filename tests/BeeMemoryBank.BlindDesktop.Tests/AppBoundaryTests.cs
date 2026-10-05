using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using BeeMemoryBank.Boundary;
using BeeMemoryBank.BlindMobile.Tests;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// The desktop blind app (Windows and macOS builds alike) does not contain the vault, and is not a node, proved on its compiled and
/// published output (BMB-99 style, as the Android <c>AppBoundaryTests</c>): every BeeMemoryBank.* assembly the app carries is read at the
/// metadata level and must not define a vault type, reference one, define or call a member that hands out the master data key, or
/// reference the vault assembly. On top of that the published folder must hold none of the full app's or the node's code, no web server
/// and no second program. The platform part is the Windows assembly in a Windows build and the macOS one in a macOS build, and each build
/// must carry only its own.
/// </summary>
public sealed class AppBoundaryTests
{
    private static readonly bool OnMac = OperatingSystem.IsMacOS();

    /// <summary>This build's platform assembly, and the other one, which must be absent.</summary>
    private static readonly string PlatformAssembly = OnMac ? "BeeMemoryBank.BlindDesktop.MacOS" : "BeeMemoryBank.BlindDesktop.Windows";
    private static readonly string OtherPlatformAssembly = OnMac ? "BeeMemoryBank.BlindDesktop.Windows" : "BeeMemoryBank.BlindDesktop.MacOS";

    /// <summary>
    /// The neutral Apple platform layer (Keychain, LaunchAgent text) is the one extra assembly a macOS blind build carries, through the
    /// macOS adapters. A Windows build (and the Android app, see its own tests) must not contain it.
    /// </summary>
    private const string AppleLayer = "BeeMemoryBank.Platforms.Apple";

    /// <summary>What this build must not carry: the other operating system's adapters and, off a Mac, the Apple platform layer.</summary>
    private static readonly string[] NotInThisBuild = OnMac ? [OtherPlatformAssembly] : [OtherPlatformAssembly, AppleLayer];

    private static readonly string BuildFolder = OnMac ? "net10.0" : "net10.0-windows10.0.19041.0";

    /// <summary>The only BeeMemoryBank assemblies the app may carry: itself, its platform part, AppCore, the phone client and the shared libraries.</summary>
    private static readonly string[] AllowedAssemblies =
    [
        "BeeMemoryBank.BlindDesktop", PlatformAssembly, .. (OnMac ? new[] { AppleLayer } : []), "BeeMemoryBank.Blind.AppCore", "BeeMemoryBank.Blind.PhoneClient",
        "BeeMemoryBank.Core", "BeeMemoryBank.Crypto", "BeeMemoryBank.Storage", "BeeMemoryBank.Sync", "BeeMemoryBank.Search",
    ];

    /// <summary>Assemblies of the full app and of nodes: none may be in the app's output, referenced by it, or in its project graph.</summary>
    private static readonly string[] ForbiddenAssemblies =
    [
        "BeeMemoryBank.Vault", "BeeMemoryBank.Hosting", "BeeMemoryBank.Hosting.AspNetCore", "BeeMemoryBank.Desktop", "BeeMemoryBank.Node",
        "BeeMemoryBank.Api", "BeeMemoryBank.Web", "BeeMemoryBank.BlindNode", "BeeMemoryBank.BlindConsole", "BeeMemoryBank.Cli",
        "BeeMemoryBank.AppPaths", "BeeMemoryBank.Profiles", "BeeMemoryBank.Infrastructure", "BeeMemoryBank.Rekey", "BeeMemoryBank.Embeddings",
        "BeeMemoryBank.Media",
    ];

    /// <summary>The host and its platform part reach the blind logic only through AppCore: they must not use these libraries directly.</summary>
    private static readonly string[] NotForTheHostItself = ["BeeMemoryBank.Storage", "BeeMemoryBank.Sync", "BeeMemoryBank.Crypto", "BeeMemoryBank.Search"];

    private static string RepoRoot() => VaultBoundary.RepoRoot();

    private static string HostProject() => Path.Combine(RepoRoot(), "desktop", "BeeMemoryBank.BlindDesktop");

    private static string PlatformProject() => Path.Combine(RepoRoot(), "desktop", PlatformAssembly);

    /// <summary>The runtime identifier of the publish this test makes: the computer it runs on.</summary>
    private static string Rid() =>
        OnMac ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64") : "win-x64";

    // ---- the build output ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TheBuildOutputs_ContainNoVaultCode()
    {
        var folders = new[] { "Debug", "Release" }
            .Select(c => Path.Combine(HostProject(), "bin", c, BuildFolder))
            .Where(d => File.Exists(Path.Combine(d, "BeeMemoryBank.BlindDesktop.dll")))
            .ToList();
        folders.Should().NotBeEmpty("build the desktop blind app first (Debug or Release)");
        foreach (var folder in folders) AssertCleanFolder(folder, publish: false);
    }

    [Fact]
    public void TheAppAndItsPlatformPart_UseTheBlindLogicOnlyThroughAppCore_AndNameNoReceiveOnlyRepository()
    {
        var host = Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.BlindDesktop.dll");
        var platform = Path.Combine(AppContext.BaseDirectory, PlatformAssembly + ".dll");
        File.Exists(host).Should().BeTrue();
        File.Exists(platform).Should().BeTrue();

        foreach (var dll in new[] { host, platform })
        {
            ReferencedAssemblies(dll).Intersect(NotForTheHostItself).Should().BeEmpty(
                Path.GetFileName(dll) + " must not call the storage, sync or crypto libraries itself: that is what AppCore is for");
            ReferencedAssemblies(dll).Intersect(ForbiddenAssemblies).Should().BeEmpty(Path.GetFileName(dll));
            // The repositories EventApplier needs are named by the AppCore composition class and by nobody else (the Android rule, kept here).
            BoundaryScanner.Scan(dll, _ => true, ReceiveOnlyTypes.RestrictedNames).Should().BeEmpty(Path.GetFileName(dll));
        }
    }

    [Fact]
    public void TheHost_ReferencesItsOwnPlatformPart_AndNotTheOtherOne()
    {
        var refs = ReferencedAssemblies(Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.BlindDesktop.dll"));
        refs.Should().Contain(PlatformAssembly);
        refs.Should().NotContain(NotInThisBuild, "a build for one operating system never compiles or carries the other one's adapters");
    }

    [Fact]
    public void TheProjectGraph_HasNoVault_NoHosting_NoFullDesktop_NoNode_AndNoWebServerPackage()
    {
        var projects = new[] { HostProject(), PlatformProject() };
        foreach (var project in projects)
        {
            var csproj = File.ReadAllText(Path.Combine(project, Path.GetFileName(project) + ".csproj"));
            foreach (var name in ForbiddenAssemblies) csproj.Should().NotContain(name + ".csproj", Path.GetFileName(project));

            var assets = Path.Combine(project, "obj", "project.assets.json");
            File.Exists(assets).Should().BeTrue("restore the project first: " + assets);
            using var json = JsonDocument.Parse(File.ReadAllText(assets));
            var libraries = json.RootElement.GetProperty("libraries").EnumerateObject().Select(l => l.Name.Split('/')[0]).ToList();
            libraries.Intersect(ForbiddenAssemblies).Should().BeEmpty("no direct or transitive reference: " + Path.GetFileName(project));
            if (!OnMac) libraries.Should().NotContain(AppleLayer, "only a macOS build reaches the Apple platform layer: " + Path.GetFileName(project));
            libraries.Should().NotContain(l => l.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal), "no web server");
            libraries.Should().NotContain(l => l.Contains("WebView", StringComparison.OrdinalIgnoreCase), "no embedded browser");
            libraries.Should().NotContain(l => l.Equals("Velopack", StringComparison.OrdinalIgnoreCase), "no updater of the full app");
        }
    }

    // ---- the published folder -----------------------------------------------------------------------------------------------

    [Fact]
    public void ThePublishedApp_ContainsNoVaultCode_NoWebServer_AndNoSecondProgram()
    {
        if (Environment.GetEnvironmentVariable("BMB_SKIP_PUBLISH_SCAN") == "1")
        {
            Environment.GetEnvironmentVariable("BMB_REQUIRE_SCAN").Should().BeNullOrEmpty("BMB_REQUIRE_SCAN is set, so the published folder must be scanned");
            return;
        }

        var output = Path.Combine(AppContext.BaseDirectory, "boundary-publish", Rid());
        Publish(output);

        AssertCleanFolder(output, publish: true);
        AssertOneProgram(output);
        Directory.GetFiles(output, "*.dll").Select(Path.GetFileName)
            .Where(n => n!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || n.Contains("Kestrel", StringComparison.Ordinal))
            .Should().BeEmpty("nothing listens on a port: no ASP.NET Core in the app");
    }

    [Fact]
    public void ThePublishedFolders_NamedInBMB_SCAN_DIRS_ContainNoVaultCode()
    {
        // The release process names the folder of the published (or installer-staged, or the extracted .app) app; ';' separates folders.
        var dirs = (Environment.GetEnvironmentVariable("BMB_SCAN_DIRS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (dirs.Length == 0)
            Environment.GetEnvironmentVariable("BMB_REQUIRE_SCAN").Should().BeNullOrEmpty("BMB_REQUIRE_SCAN is set, so BMB_SCAN_DIRS must name the published app folder");
        foreach (var dir in dirs)
        {
            Directory.Exists(dir).Should().BeTrue(dir);
            var files = Directory.GetFiles(dir, "BeeMemoryBank.*.dll", SearchOption.AllDirectories);
            files.Should().NotBeEmpty(dir);
            AssemblyBoundaryScanner.Scan(files, VaultBoundary.Forbidden()).Should().BeEmpty(dir);
            var names = files.Select(Path.GetFileNameWithoutExtension).ToList();
            names.Should().NotContain(ForbiddenAssemblies, dir);
            names.Should().NotContain(NotInThisBuild, "this operating system's package carries only its own platform part: " + dir);
            if (OnMac) names.Should().Contain(AppleLayer, "a macOS blind package carries the Apple platform layer its adapters use: " + dir);
            Directory.GetFiles(dir, "BeeMemoryBank.Vault.dll", SearchOption.AllDirectories).Should().BeEmpty(dir);
            AssertOneProgramIn(dir);
        }
    }

    // ---- controls: the scan must find what is there -------------------------------------------------------------------------

    [Fact]
    public void Control_TheScanFlagsTheVaultWhenItIsGivenTheVault()
    {
        // This test project references the Vault (for this control only), so its own output holds the real thing.
        var vault = Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.Vault.dll");
        File.Exists(vault).Should().BeTrue("the tests reference the vault");
        var findings = AssemblyBoundaryScanner.Scan([vault], VaultBoundary.Forbidden());
        findings.Should().Contain(f => f.Kind == "defines type" && f.Detail == "BeeMemoryBank.Core.Services.SessionService");
    }

    [Fact]
    public void Control_TheFolderCheck_FailsOnAFolderThatHoldsTheVault()
    {
        // A folder made of the app's own output plus the vault must be refused by the same check that passes the real one.
        var folder = TestFolders.New("with-vault");
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "BeeMemoryBank.*.dll")
                     .Where(f => AllowedAssemblies.Contains(Path.GetFileNameWithoutExtension(f))))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
        AssertCleanFolder(folder, publish: false); // the same folder without the vault passes

        File.Copy(Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.Vault.dll"), Path.Combine(folder, "BeeMemoryBank.Vault.dll"), overwrite: true);
        var act = () => AssertCleanFolder(folder, publish: false);

        act.Should().Throw<Exception>("the vault in the folder must be found").Which.Message.Should().Contain("BeeMemoryBank.Vault");
    }

    [Fact]
    public void Control_TheFolderCheck_FailsOnAFolderThatHoldsTheOtherPlatformsAdapters()
    {
        var folder = TestFolders.New("with-other-platform");
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "BeeMemoryBank.*.dll")
                     .Where(f => AllowedAssemblies.Contains(Path.GetFileNameWithoutExtension(f))))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
        // any assembly file under the other platform's name stands in for it: the check goes by name
        File.Copy(Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.BlindDesktop.dll"), Path.Combine(folder, OtherPlatformAssembly + ".dll"), overwrite: true);

        var act = () => AssertCleanFolder(folder, publish: false);

        act.Should().Throw<Exception>().Which.Message.Should().Contain(OtherPlatformAssembly);
    }

    [Fact]
    public void Control_TheFolderCheck_FailsOnTheApplePlatformLayerOffAMac_AndWithoutItOnAMac()
    {
        var folder = TestFolders.New("apple-layer");
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory, "BeeMemoryBank.*.dll")
                     .Where(f => AllowedAssemblies.Contains(Path.GetFileNameWithoutExtension(f))))
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
        AssertCleanFolder(folder, publish: false);
        var apple = Path.Combine(folder, AppleLayer + ".dll");

        if (OnMac)
        {
            // the layer is required on a Mac: a folder without it is not a complete macOS blind build
            File.Exists(apple).Should().BeTrue("the macOS test build carries it");
            var without = TestFolders.New("apple-layer-missing");
            foreach (var file in Directory.GetFiles(folder, "*.dll").Where(f => Path.GetFileNameWithoutExtension(f) != AppleLayer))
                File.Copy(file, Path.Combine(without, Path.GetFileName(file)));
            var act = () => AssertCleanFolder(without, publish: false);
            act.Should().Throw<Exception>().Which.Message.Should().Contain(AppleLayer);
        }
        else
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.BlindDesktop.dll"), apple, overwrite: true);   // any assembly under that name: the check goes by name
            var act = () => AssertCleanFolder(folder, publish: false);
            act.Should().Throw<Exception>().Which.Message.Should().Contain(AppleLayer);
        }
    }

    [Fact]
    public void Control_ReferencedAssemblies_SeeTheHostsRealReferences()
    {
        var refs = ReferencedAssemblies(Path.Combine(AppContext.BaseDirectory, "BeeMemoryBank.BlindDesktop.dll"));
        refs.Should().Contain("BeeMemoryBank.Blind.AppCore").And.Contain(PlatformAssembly);
    }

    [Fact]
    public void Control_TheProgramCheck_FindsASecondProgram()
    {
        var folder = TestFolders.New("two-programs");
        if (OnMac)
        {
            WriteMachO(Path.Combine(folder, "BeeMemoryBank.BlindDesktop"), executable: true);
            AssertOneProgramIn(folder);
            WriteMachO(Path.Combine(folder, "helper"), executable: true);
        }
        else
        {
            File.WriteAllBytes(Path.Combine(folder, "BeeMemoryBank.BlindDesktop.exe"), [1]);
            AssertOneProgramIn(folder);
            File.WriteAllBytes(Path.Combine(folder, "helper.exe"), [1]);
        }

        var act = () => AssertOneProgramIn(folder);

        act.Should().Throw<Exception>("a second program in the folder must be found");
    }

    [Fact]
    public void MachOKind_TellsAnExecutableFromALibrary()
    {
        var folder = TestFolders.New("macho");
        var exe = Path.Combine(folder, "exe");
        var lib = Path.Combine(folder, "lib.dylib");
        var text = Path.Combine(folder, "notes.txt");
        WriteMachO(exe, executable: true);
        WriteMachO(lib, executable: false);
        File.WriteAllText(text, "hello");

        MachOKind(exe).Should().Be("executable");
        MachOKind(lib).Should().Be("library");
        MachOKind(text).Should().BeNull();
        MachOKind(Path.Combine(folder, "missing")).Should().BeNull();
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------

    private static void AssertCleanFolder(string folder, bool publish)
    {
        var files = AssemblyBoundaryScanner.ApplicationAssemblies(folder);
        var names = files.Select(Path.GetFileNameWithoutExtension).ToList();
        names.Should().Contain(["BeeMemoryBank.BlindDesktop", PlatformAssembly, "BeeMemoryBank.Blind.AppCore", "BeeMemoryBank.Core", "BeeMemoryBank.Sync"],
            "the scan must see the app and the libraries it carries: " + folder);
        names.Should().NotContain(ForbiddenAssemblies, folder);
        names.Should().NotContain(NotInThisBuild, "the other operating system's adapters are not part of this build: " + folder);
        if (OnMac) names.Should().Contain(AppleLayer, "the macOS adapters use it: " + folder);
        names.Should().OnlyContain(n => AllowedAssemblies.Contains(n), "an assembly of the repository that is not on the list is in the app: " + folder);
        AssemblyBoundaryScanner.Scan(files, VaultBoundary.Forbidden()).Should().BeEmpty(folder);
        AssemblyBoundaryScanner.DuplicatedTypes(files).Should().BeEmpty(folder);
        if (publish) Directory.GetFiles(folder, "BeeMemoryBank.Vault.dll", SearchOption.AllDirectories).Should().BeEmpty(folder);
    }

    /// <summary>The app's own executable and no other program in a publish folder.</summary>
    private static void AssertOneProgram(string folder)
    {
        AssertOneProgramIn(folder);
        if (OnMac) Directory.GetFiles(folder, "BeeMemoryBank.BlindDesktop").Should().ContainSingle("the app's own executable");
        else Directory.GetFiles(folder, "BeeMemoryBank.BlindDesktop.exe").Should().ContainSingle("the app's own executable");
    }

    /// <summary>Windows: one .exe. macOS: one Mach-O executable (libraries and the managed dlls are not programs), anywhere under the folder.</summary>
    private static void AssertOneProgramIn(string folder)
    {
        if (!OnMac)
        {
            Directory.GetFiles(folder, "*.exe", SearchOption.AllDirectories).Should().HaveCount(1, "no second program, no child process binary: " + folder);
            return;
        }

        var programs = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".dll", StringComparison.Ordinal) && MachOKind(f) == "executable")
            .Select(f => Path.GetRelativePath(folder, f)).ToList();
        programs.Should().HaveCount(1, "no second program (createdump, helpers) in " + folder + ": " + string.Join(", ", programs));
        Path.GetFileName(programs[0]).Should().Be("BeeMemoryBank.BlindDesktop");
    }

    /// <summary>"executable" (MH_EXECUTE) or "library" (dylib, bundle) for a Mach-O file, thin or fat; null for anything else.</summary>
    internal static string? MachOKind(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[16];
            if (stream.Read(header, 0, header.Length) < 16) return null;
            var magicLittle = BitConverter.ToUInt32(header, 0);
            var magicBig = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
            if (magicBig is 0xCAFEBABE or 0xBEBAFECA)
            {
                // fat: the first architecture's offset, then its header
                var offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8));
                stream.Seek(offset, SeekOrigin.Begin);
                if (stream.Read(header, 0, header.Length) < 16) return null;
                magicLittle = BitConverter.ToUInt32(header, 0);
            }
            if (magicLittle is not (0xFEEDFACF or 0xFEEDFACE)) return null;
            var fileType = BitConverter.ToUInt32(header, 12);
            return fileType switch { 2 => "executable", 6 or 8 => "library", _ => "other" };
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>A minimal 64-bit little-endian Mach-O header of the given kind (the check reads only the header).</summary>
    private static void WriteMachO(string path, bool executable)
    {
        var bytes = new byte[32];
        BitConverter.GetBytes(0xFEEDFACFu).CopyTo(bytes, 0);
        BitConverter.GetBytes(0x0100000Cu).CopyTo(bytes, 4);   // arm64
        BitConverter.GetBytes(executable ? 2u : 6u).CopyTo(bytes, 12);
        File.WriteAllBytes(path, bytes);
    }

    private static IReadOnlyList<string> ReferencedAssemblies(string dll)
    {
        using var stream = File.OpenRead(dll);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        return md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
    }

    /// <summary>
    /// Publishes the app for this computer's RID (framework-dependent, Release) into <paramref name="output"/> with the dotnet CLI, as a
    /// release build would. The CLI is started so that it leaves no worker process behind (no node reuse, no build server, no shared
    /// compiler), and its output is read as it arrives and looked at only if the publish failed: see <see cref="ChildProcess"/>.
    /// </summary>
    private static void Publish(string output)
    {
        Directory.CreateDirectory(output);
        var start = ChildProcess.DotnetWithoutLeftovers(RepoRoot(),
            "publish", Path.Combine("desktop", "BeeMemoryBank.BlindDesktop", "BeeMemoryBank.BlindDesktop.csproj"),
            "-c", "Release", "-r", Rid(), "--self-contained", "false", "-o", output, "-nologo", "-v", "q");

        var result = ChildProcess.Run(start, TimeSpan.FromMinutes(8));

        if (result.TimedOut) throw new TimeoutException("dotnet publish took longer than eight minutes:" + Environment.NewLine + result.Both);
        if (result.ExitCode != 0)
            throw new Xunit.Sdk.XunitException("dotnet publish must succeed (exit " + result.ExitCode + "):" + Environment.NewLine + result.Both);
    }
}
