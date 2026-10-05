using System;
using System.IO;
using System.Linq;
using BeeMemoryBank.Desktop.MacOS;
using BeeMemoryBank.Platforms.Apple.LaunchAgents;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// The login item's build/temp refusal with REAL symbolic links on a Mac, in folders under BMB_MAC_TEST_ROOT (see <see cref="MacTestRoot"/>).
/// Nothing is loaded into launchd here and the owner's LaunchAgents folder is not touched: the plist (when one is written) goes to a folder
/// of the test's own. Skipped, with the reason, when the root is not set.
/// </summary>
public sealed class MacOsAutostartSymlinkMacTests(ITestOutputHelper output)
{
    private static string Executable(string folder)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "BeeMemoryBank.Desktop");
        File.WriteAllText(file, "#!/bin/sh\n");
        return file;
    }

    private static MacOsAutostart AutostartFor(string root, string program) =>
        new(new MacOsAutostartOptions { LaunchAgentsDirectory = Path.Combine(root, "LaunchAgents"), ProgramArguments = [program, "--minimized"] });

    private void ShowRefusal(string program, string? reason) => output.WriteLine($"{program}  ->  {reason ?? "(accepted)"}");

    [MacRealFact]
    public void AFileLink_InANormalFolder_ToAFileInBinDebug_IsRefused()
    {
        var root = MacTestRoot.New("link-file");
        var real = Executable(Path.Combine(root, "src", "bin", "Debug", "net10.0"));
        var apps = Path.Combine(root, "Applications");
        Directory.CreateDirectory(apps);
        var link = Path.Combine(apps, "BeeMemoryBank.Desktop");
        File.CreateSymbolicLink(link, real);

        var reason = ProgramLocationPolicy.Refusal(link);
        ShowRefusal(link, reason);

        reason.Should().Contain("build folder").And.Contain("leads to");
        var enable = () => AutostartFor(root, link).Enable();
        enable.Should().Throw<InvalidOperationException>();
        Directory.Exists(Path.Combine(root, "LaunchAgents")).Should().BeFalse("nothing was written");
    }

    [MacRealFact]
    public void AFolderLink_InANormalFolder_ToABinReleaseFolder_IsRefused()
    {
        var root = MacTestRoot.New("link-dir");
        Executable(Path.Combine(root, "src", "bin", "Release", "net10.0", "osx-arm64"));
        var apps = Path.Combine(root, "Applications");
        Directory.CreateDirectory(apps);
        Directory.CreateSymbolicLink(Path.Combine(apps, "Bee Memory Bank.app"), Path.Combine(root, "src", "bin", "Release", "net10.0", "osx-arm64"));
        var program = Path.Combine(apps, "Bee Memory Bank.app", "BeeMemoryBank.Desktop");
        File.Exists(program).Should().BeTrue("the file really is reachable through the link");

        var reason = ProgramLocationPolicy.Refusal(program);
        ShowRefusal(program, reason);

        reason.Should().Contain("build folder");
        var enable = () => AutostartFor(root, program).Enable();
        enable.Should().Throw<InvalidOperationException>();
    }

    [MacRealFact]
    public void DotDotAfterARealLink_IsJudgedWhereItReallyLeads()
    {
        var root = MacTestRoot.New("link-dotdot");
        var build = Path.Combine(root, "src", "bin", "Debug", "net10.0");
        Executable(build);                                    // .../bin/Debug/net10.0/BeeMemoryBank.Desktop
        Directory.CreateDirectory(Path.Combine(build, "sub"));
        var links = Path.Combine(root, "links");
        Directory.CreateDirectory(links);
        Directory.CreateSymbolicLink(Path.Combine(links, "up"), Path.Combine(build, "sub"));
        var program = Path.Combine(links, "up", "..", "BeeMemoryBank.Desktop");   // written: <root>/links/up/../BeeMemoryBank.Desktop
        // Asked of the operating system itself: it resolves "up/.." through the link and finds the file in the build folder. (.NET's own
        // File.Exists takes the dots out as text first and would look in <root>/links, so it cannot be used to prove this.)
        new ProcessCommandRunner().Run("/bin/test", ["-f", program], TimeSpan.FromSeconds(10)).Succeeded
            .Should().BeTrue("the operating system finds the file through the link");

        ProgramLocationPolicy.Refusal(ProgramPaths.Lexical(program), links: new FakeLinks()).Should().BeNull("with the dots taken out as text it looks harmless");
        var reason = ProgramLocationPolicy.Refusal(program);
        ShowRefusal(program, reason);

        reason.Should().Contain("build folder");
    }

    [MacRealFact]
    public void DotDotSegmentsThatLandInTmp_AreRefused()
    {
        var root = MacTestRoot.New("link-tmp-dots");
        var depth = root.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        var program = root + "/" + string.Concat(Enumerable.Repeat("../", depth)) + "tmp/bmb-fix1-never-created/BeeMemoryBank.Desktop";

        var reason = ProgramLocationPolicy.Refusal(program);
        ShowRefusal(program, reason);

        reason.Should().Contain("temporary").And.Contain("means '/tmp/bmb-fix1-never-created/BeeMemoryBank.Desktop'");
        Directory.Exists("/tmp/bmb-fix1-never-created").Should().BeFalse("the test only looked at the path");
    }

    [MacRealFact]
    public void ALinkToTmp_IsRefused_BecauseTmpIsReallyPrivateTmp()
    {
        var root = MacTestRoot.New("link-to-tmp");
        var apps = Path.Combine(root, "apps");
        Directory.CreateDirectory(apps);
        Directory.CreateSymbolicLink(Path.Combine(apps, "Tmp"), "/tmp");
        var program = Path.Combine(apps, "Tmp", "bmb-fix1-never-created", "BeeMemoryBank.Desktop");

        var reason = ProgramLocationPolicy.Refusal(program);
        ShowRefusal(program, reason);

        reason.Should().Contain("temporary");
    }

    [MacRealFact]
    public void ALinkLoop_IsRefused_NotAHang()
    {
        var root = MacTestRoot.New("link-loop");
        File.CreateSymbolicLink(Path.Combine(root, "one"), Path.Combine(root, "two"));
        File.CreateSymbolicLink(Path.Combine(root, "two"), Path.Combine(root, "one"));
        var program = Path.Combine(root, "one", "BeeMemoryBank.Desktop");

        var reason = ProgramLocationPolicy.Refusal(program);
        ShowRefusal(program, reason);

        reason.Should().Contain("symbolic links");
    }

    [MacRealFact]
    public void AnInstalledLookingPath_UnderTheRoot_IsAccepted_AndEnableWritesIt()
    {
        var root = MacTestRoot.New("installed");
        var program = Executable(Path.Combine(root, "Applications", "Bee Memory Bank.app", "Contents", "MacOS"));

        ProgramLocationPolicy.Refusal(program).Should().BeNull();
        var autostart = AutostartFor(root, program);
        autostart.Enable();

        LaunchAgentPlist.TryParse(File.ReadAllText(autostart.PlistPath))!.ProgramArguments.Should().Equal(program, "--minimized");
        autostart.IsEnabled.Should().BeTrue();
        output.WriteLine($"{program}  ->  (accepted) and written to {autostart.PlistPath}");
    }

    [MacRealFact]
    public void ALink_InANormalFolder_ToAnInstalledLookingBundle_IsAccepted()
    {
        var root = MacTestRoot.New("link-ok");
        Executable(Path.Combine(root, "Applications", "Bee Memory Bank.app", "Contents", "MacOS"));
        var shortcuts = Path.Combine(root, "Shortcuts");
        Directory.CreateDirectory(shortcuts);
        Directory.CreateSymbolicLink(Path.Combine(shortcuts, "BMB.app"), Path.Combine(root, "Applications", "Bee Memory Bank.app"));
        var program = Path.Combine(shortcuts, "BMB.app", "Contents", "MacOS", "BeeMemoryBank.Desktop");
        File.Exists(program).Should().BeTrue();

        ProgramLocationPolicy.Refusal(program).Should().BeNull("both the written path and the path it leads to are fine");
        var autostart = AutostartFor(root, program);
        autostart.Enable();

        LaunchAgentPlist.TryParse(File.ReadAllText(autostart.PlistPath))!.ProgramArguments.Should().Equal(program, "--minimized");
        output.WriteLine($"{program}  ->  (accepted)");
    }
}
