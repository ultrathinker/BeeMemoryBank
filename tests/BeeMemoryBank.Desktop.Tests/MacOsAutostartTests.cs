using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeeMemoryBank.Desktop.MacOS;
using BeeMemoryBank.Platforms.Apple.LaunchAgents;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>Where a login item may point.</summary>
public sealed class ProgramLocationPolicyTests
{
    [Theory]
    [InlineData("/Applications/Bee Memory Bank.app/Contents/MacOS/BeeMemoryBank.Desktop")]
    [InlineData("/Users/someone/Applications/Bee Memory Bank.app/Contents/MacOS/BeeMemoryBank.Desktop")]
    [InlineData("/Volumes/External/Apps/Bee Memory Bank.app/Contents/MacOS/BeeMemoryBank.Desktop")]
    [InlineData("/usr/bin/true")]
    [InlineData("/Users/someone/binary/Debugger/tool")]   // "bin" + "Debugger" is not bin/Debug
    public void AnInstalledApp_MayBeALoginItem(string program)
    {
        ProgramLocationPolicy.Refusal(program, tempPath: "/var/folders/ab/cdef/T/").Should().BeNull();
    }

    [Theory]
    [InlineData("/Users/someone/src/repo/desktop/BeeMemoryBank.Desktop/bin/Debug/net10.0/BeeMemoryBank.Desktop", "build folder")]
    [InlineData("/Users/someone/src/repo/desktop/BeeMemoryBank.Desktop/bin/Release/net10.0/osx-arm64/publish/BeeMemoryBank.Desktop", "build folder")]
    [InlineData("/Users/someone/src/repo/desktop/BeeMemoryBank.Desktop/obj/Debug/net10.0/apphost", "build folder")]
    [InlineData("/Users/someone/src/x/BIN/RELEASE/app", "build folder")]
    [InlineData("/tmp/app/BeeMemoryBank.Desktop", "temporary")]
    [InlineData("/private/tmp/app/BeeMemoryBank.Desktop", "temporary")]
    [InlineData("/var/folders/ab/cdef/T/app/BeeMemoryBank.Desktop", "temporary")]
    [InlineData("/private/var/folders/ab/cdef/T/app/BeeMemoryBank.Desktop", "temporary")]
    [InlineData("/var/tmp/app/BeeMemoryBank.Desktop", "temporary")]
    [InlineData("/Users/someone/Downloads/x/AppTranslocation/ABC/d/Bee Memory Bank.app/Contents/MacOS/BeeMemoryBank.Desktop", "quarantine")]
    [InlineData("/usr/local/share/dotnet/dotnet", "dotnet host")]
    [InlineData("relative/path/app", "absolute")]
    [InlineData("", "not known")]
    [InlineData(null, "not known")]
    public void ABuildFolderATempFolderTheDotnetHostOrNoPath_IsRefusedWithAReason(string? program, string reasonContains)
    {
        ProgramLocationPolicy.Refusal(program, tempPath: "/var/folders/ab/cdef/T/").Should().NotBeNull().And.Subject.Should().Contain(reasonContains);
    }

    [Fact]
    public void TheTempFolderOfTheRunningProcess_IsRefusedToo()
    {
        ProgramLocationPolicy.Refusal("/Users/someone/scratch/T/app", tempPath: "/Users/someone/scratch/T/").Should().Contain("temporary");
        ProgramLocationPolicy.Refusal("/Users/someone/scratch/T/app", tempPath: "/Users/someone/scratch/T").Should().Contain("temporary", "with or without the trailing slash");
        ProgramLocationPolicy.Refusal("/Users/someone/scratch/Tools/app", tempPath: "/Users/someone/scratch/T").Should().BeNull("a folder that only starts with the same letters is not the temp folder");
    }
}

/// <summary>
/// The full app's login item. Everything here runs on a fake <c>launchctl</c> and in a folder of the test's own, on any OS; the real
/// launchd round trip is in <see cref="MacOsServicesOnTheMacTests"/>.
/// </summary>
public sealed class MacOsAutostartTests
{
    private const string App = "/Applications/Bee Memory Bank.app/Contents/MacOS/BeeMemoryBank.Desktop";

    private readonly string _agents = TestScratch.New("agents");

    private sealed class Harness
    {
        public required MacOsAutostart Autostart { get; init; }
        public required FakeCommandRunner Runner { get; init; }
        public required string Path { get; init; }
    }

    private Harness Make(
        string? processPath = App,
        IReadOnlyList<string>? program = null,
        bool loadImmediately = false,
        Func<FakeCommandRunner.Call, CommandResult>? answer = null,
        string? xpcServiceName = null,
        Func<string, bool>? fileExists = null,
        string label = MacOsAutostartOptions.DefaultLabel,
        string? agents = null)
    {
        var runner = new FakeCommandRunner(answer ?? (_ => new CommandResult(113, "", "Could not find service", TimedOut: false)));
        var options = new MacOsAutostartOptions { Label = label, LaunchAgentsDirectory = agents ?? _agents, ProgramArguments = program, LoadImmediately = loadImmediately };
        var autostart = new MacOsAutostart(options, runner, () => 501, name => name == "XPC_SERVICE_NAME" ? xpcServiceName : null,
            () => processPath, fileExists ?? (_ => true), tempPath: "/var/folders/ab/cdef/T/");
        return new Harness { Autostart = autostart, Runner = runner, Path = Path.Combine(agents ?? _agents, label + ".plist") };
    }

    // ── enabling ────────────────────────────────────────────────────────────────

    [Fact]
    public void Enable_WritesTheAgent_TheAppFollowedByMinimized_AndItReadsBackAsEnabled()
    {
        var h = Make();

        h.Autostart.IsEnabled.Should().BeFalse();
        h.Autostart.Enable();

        File.ReadAllText(h.Path).Should().Be(LaunchAgentPlist.Build("com.beememorybank.desktop", [App, "--minimized"]));
        var info = LaunchAgentPlist.TryParse(File.ReadAllText(h.Path))!;
        info.Label.Should().Be("com.beememorybank.desktop");
        info.RunAtLoad.Should().BeTrue();
        info.ProgramArguments.Should().Equal(App, "--minimized");
        h.Autostart.IsEnabled.Should().BeTrue();
        h.Autostart.LastWarning.Should().BeNull();
        Directory.GetFiles(_agents).Should().ContainSingle("no temporary file is left next to the plist");
    }

    [Fact]
    public void Enable_DoesNotTouchLaunchd_ByDefault()
    {
        var h = Make();

        h.Autostart.Enable();

        h.Runner.Calls.Should().BeEmpty("the running app does not need a second copy of itself; the agent starts at the next sign-in");
    }

    [Fact]
    public void Enable_IsIdempotent_TheSecondCallLeavesTheFileAlone()
    {
        var h = Make();
        h.Autostart.Enable();
        var old = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(h.Path, old);
        var before = File.ReadAllBytes(h.Path);

        h.Autostart.Enable();
        h.Autostart.Enable();

        File.ReadAllBytes(h.Path).Should().Equal(before);
        File.GetLastWriteTimeUtc(h.Path).Should().Be(old, "an identical plist is not rewritten");
        h.Autostart.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Enable_AfterTheAppMoved_ReplacesTheOldProgram()
    {
        var oldApp = "/Users/someone/Downloads/Bee Memory Bank.app/Contents/MacOS/BeeMemoryBank.Desktop";
        Make(processPath: oldApp).Autostart.Enable();
        var moved = Make(processPath: App);

        moved.Autostart.IsEnabled.Should().BeFalse("the plist starts another copy than this one - like the Windows registry value check");
        moved.Autostart.Enable();

        LaunchAgentPlist.TryParse(File.ReadAllText(moved.Path))!.ProgramArguments.Should().Equal(App, "--minimized");
        moved.Autostart.IsEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("/Users/someone/src/repo/desktop/BeeMemoryBank.Desktop/bin/Debug/net10.0/BeeMemoryBank.Desktop", "build folder")]
    [InlineData("/tmp/BeeMemoryBank.Desktop", "temporary")]
    [InlineData("/var/folders/ab/cdef/T/x/BeeMemoryBank.Desktop", "temporary")]
    [InlineData("/usr/local/share/dotnet/dotnet", "dotnet host")]
    [InlineData(null, "not known")]
    public void Enable_RefusesToPointIntoABuildOrTempFolder_AndWritesNothing(string? processPath, string reason)
    {
        var h = Make(processPath: processPath);

        var enable = () => h.Autostart.Enable();

        enable.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain(reason);
        File.Exists(h.Path).Should().BeFalse("a refused plist is not written");
        Directory.GetFileSystemEntries(_agents).Should().BeEmpty();
        h.Autostart.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Enable_AnExplicitProgram_IsCheckedTheSameWay()
    {
        var h = Make(program: ["/tmp/evil", "--minimized"]);

        var enable = () => h.Autostart.Enable();

        enable.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("temporary");
        File.Exists(h.Path).Should().BeFalse();
    }

    [Fact]
    public void Enable_AProgramThatDoesNotExist_IsAnError_NotAFile()
    {
        var h = Make(fileExists: _ => false);

        var enable = () => h.Autostart.Enable();

        enable.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("does not exist");
        File.Exists(h.Path).Should().BeFalse();
    }

    [Fact]
    public void Enable_WhenTheFolderCannotBeUsed_ReportsTheErrorHonestly()
    {
        // a FILE where the LaunchAgents folder should be: nothing can be written there
        var blocker = Path.Combine(_agents, "LaunchAgents");
        File.WriteAllText(blocker, "not a folder");
        var h = Make(agents: blocker);

        var enable = () => h.Autostart.Enable();

        enable.Should().Throw<Exception>("success must never be claimed for a login item that was not written");
        h.Autostart.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void AProgramPathWithSpacesAmpersandsAndAngleBrackets_RoundTripsThroughThePlist()
    {
        var odd = "/Applications/Tom & Jerry <1> \"x\"/Bee Memory Bank.app/Contents/MacOS/BeeMemoryBank.Desktop";
        var h = Make(processPath: odd);

        h.Autostart.Enable();

        LaunchAgentPlist.TryParse(File.ReadAllText(h.Path))!.ProgramArguments.Should().Equal(odd, "--minimized");
        h.Autostart.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void ABadLabel_IsRefusedUpFront()
    {
        var make = () => Make(label: "../../evil");

        make.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ThePlistPath_IsInTheUsersLaunchAgents_UnlessTheOptionsSayOtherwise()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var real = new MacOsAutostart(new MacOsAutostartOptions());

        real.Label.Should().Be("com.beememorybank.desktop");
        real.PlistPath.Should().Be(Path.Combine(home, "Library", "LaunchAgents", "com.beememorybank.desktop.plist"));
    }

    // ── reading ─────────────────────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_NoFile_IsFalse_AndNothingIsCreated()
    {
        var h = Make();

        h.Autostart.IsEnabled.Should().BeFalse();

        Directory.GetFileSystemEntries(_agents).Should().BeEmpty();
    }

    [Theory]
    [InlineData("com.someone.else", true, true)]     // another job's label under our file name
    [InlineData("com.beememorybank.desktop", false, true)]   // no RunAtLoad
    [InlineData("com.beememorybank.desktop", true, false)]   // the program is gone
    public void IsEnabled_IsFalse_ForAPlistThatIsNotAWorkingLoginItemOfThisApp(string label, bool runAtLoad, bool programExists)
    {
        var h = Make(fileExists: _ => programExists);
        File.WriteAllText(h.Path, PlistText(label, [App, "--minimized"], runAtLoad));

        h.Autostart.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_GarbageAndUnreadableFiles_AreFalse()
    {
        var h = Make();
        File.WriteAllText(h.Path, "this is not a property list");
        h.Autostart.IsEnabled.Should().BeFalse();

        File.Delete(h.Path);
        Directory.CreateDirectory(h.Path);   // a folder where the file should be: cannot be read
        h.Autostart.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_ADevelopmentRun_CanStillSeeAValidLoginItem()
    {
        // started with `dotnet App.dll` the app cannot say what it would write; any valid plist of this label then counts
        Make().Autostart.Enable();
        var dev = Make(processPath: "/usr/local/share/dotnet/dotnet");

        dev.Autostart.IsEnabled.Should().BeTrue();
    }

    // ── disabling ───────────────────────────────────────────────────────────────

    [Fact]
    public void Disable_RemovesOnlyItsOwnPlist_AndLeavesTheNeighbours()
    {
        var h = Make();
        h.Autostart.Enable();
        var other = Path.Combine(_agents, "com.google.keystone.agent.plist");
        var similar = Path.Combine(_agents, "com.beememorybank.desktop.helper.plist");
        File.WriteAllText(other, "<plist/>");
        File.WriteAllText(similar, "<plist/>");

        h.Autostart.Disable();

        File.Exists(h.Path).Should().BeFalse();
        File.Exists(other).Should().BeTrue();
        File.Exists(similar).Should().BeTrue();
        h.Autostart.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void Disable_IsIdempotent_NothingToRemoveIsNotAnError()
    {
        var h = Make();

        h.Autostart.Disable();
        h.Autostart.Enable();
        h.Autostart.Disable();
        h.Autostart.Disable();

        File.Exists(h.Path).Should().BeFalse();
    }

    [Fact]
    public void Disable_AFileOfAnotherJobUnderOurName_IsLeftAlone_AndReported()
    {
        var h = Make();
        File.WriteAllText(h.Path, PlistText("com.someone.else", ["/bin/x"], true));

        var disable = () => h.Autostart.Disable();

        disable.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("com.someone.else");
        File.Exists(h.Path).Should().BeTrue("not ours, not removed");
    }

    [Fact]
    public void Disable_ABrokenFileUnderOurName_IsRemoved()
    {
        var h = Make();
        File.WriteAllText(h.Path, "garbage");

        h.Autostart.Disable();

        File.Exists(h.Path).Should().BeFalse();
    }

    // ── launchd ─────────────────────────────────────────────────────────────────

    private static string Domain => "gui/501";

    private static CommandResult Loaded => new(0, "", "", TimedOut: false);

    private static CommandResult NotLoaded => new(113, "", "Could not find service", TimedOut: false);

    [Fact]
    public void Enable_WithLoadImmediately_BootstrapsTheAgent_IntoTheUsersGuiDomain()
    {
        var bootstrapped = false;
        var h = Make(loadImmediately: true, answer: call =>
        {
            if (call.Arguments[0] == "bootstrap") { bootstrapped = true; return FakeCommandRunner.Ok; }
            return bootstrapped ? Loaded : NotLoaded;   // print
        });

        h.Autostart.Enable();

        h.Runner.Calls.Should().Contain(c => c.FileName == "/bin/launchctl" && c.Arguments.SequenceEqual(new[] { "bootstrap", Domain, h.Path }));
        h.Autostart.LastWarning.Should().BeNull();
    }

    [Fact]
    public void Enable_WhenLaunchdRefusesToLoad_StillHasTheFile_AndSaysWhy()
    {
        var h = Make(loadImmediately: true, answer: call =>
            call.Arguments[0] == "bootstrap" ? new CommandResult(5, "", "Bootstrap failed: 5: Input/output error", TimedOut: false) : NotLoaded);

        h.Autostart.Enable();

        File.Exists(h.Path).Should().BeTrue();
        h.Autostart.IsEnabled.Should().BeTrue();
        h.Autostart.LastWarning.Should().Contain("refused to load").And.Contain("Input/output error");
    }

    [Fact]
    public void Enable_AlreadyLoadedAndUnchanged_DoesNothingToLaunchd()
    {
        var h = Make(loadImmediately: true, answer: _ => Loaded);
        h.Autostart.Enable();
        h.Runner.Calls.Clear();

        h.Autostart.Enable();

        h.Runner.Calls.Should().OnlyContain(c => c.Arguments[0] == "print", "only the question 'is it loaded', no bootout and no second bootstrap");
    }

    [Fact]
    public void Enable_ChangedWhileLoaded_BootsTheOldOneOutAndLoadsTheNew()
    {
        var oldApp = "/Users/someone/Downloads/Bee Memory Bank.app/Contents/MacOS/BeeMemoryBank.Desktop";
        var loaded = true;
        var answer = (FakeCommandRunner.Call call) =>
        {
            switch (call.Arguments[0])
            {
                case "bootout": loaded = false; return FakeCommandRunner.Ok;
                case "bootstrap": loaded = true; return FakeCommandRunner.Ok;
                default: return loaded ? Loaded : NotLoaded;
            }
        };
        Make(processPath: oldApp, answer: answer).Autostart.Enable();   // old plist in place, job "loaded"
        var h = Make(processPath: App, loadImmediately: true, answer: answer);

        h.Autostart.Enable();

        var verbs = h.Runner.Calls.Select(c => c.Arguments[0]).Where(v => v != "print").ToList();
        verbs.Should().Equal("bootout", "bootstrap");
    }

    [Fact]
    public void Disable_BootsTheLoadedJobOut_ByItsLabel()
    {
        var loaded = true;
        var h = Make(answer: call =>
        {
            if (call.Arguments[0] == "bootout") { loaded = false; return FakeCommandRunner.Ok; }
            return loaded ? Loaded : NotLoaded;
        });
        h.Autostart.Enable();

        h.Autostart.Disable();

        h.Runner.Calls.Should().Contain(c => c.Arguments.SequenceEqual(new[] { "bootout", $"{Domain}/com.beememorybank.desktop" }));
        h.Autostart.LastWarning.Should().BeNull();
        File.Exists(h.Path).Should().BeFalse();
    }

    [Fact]
    public void Disable_NotLoaded_DoesNotCallBootout()
    {
        var h = Make();   // everything "not loaded"
        h.Autostart.Enable();

        h.Autostart.Disable();

        h.Runner.Calls.Should().OnlyContain(c => c.Arguments[0] == "print");
    }

    [Fact]
    public void Disable_WhenLaunchdKeepsTheJob_SaysSo_ButTheFileIsGone()
    {
        var h = Make(answer: call => call.Arguments[0] == "bootout" ? new CommandResult(1, "", "Boot-out failed", TimedOut: false) : Loaded);
        h.Autostart.Enable();

        h.Autostart.Disable();

        File.Exists(h.Path).Should().BeFalse();
        h.Autostart.LastWarning.Should().Contain("still has the job loaded").And.Contain("Boot-out failed");
    }

    [Fact]
    public void Disable_InsideTheJobItself_DoesNotKillItsOwnApp()
    {
        // launchd started this process from this agent: bootout would end the app that is switching autostart off
        var h = Make(xpcServiceName: "com.beememorybank.desktop", answer: _ => Loaded);
        h.Autostart.Enable();

        h.Autostart.Disable();

        h.Runner.Calls.Should().NotContain(c => c.Arguments[0] == "bootout");
        File.Exists(h.Path).Should().BeFalse("the next sign-in must not start the app");
        h.Autostart.LastWarning.Should().Contain("started by the login item");
    }

    [Fact]
    public void ADifferentLabel_NeverTouchesTheFullAppsAgent()
    {
        var full = Make();
        var other = Make(label: "com.beememorybank.desktop.test.abc");
        full.Autostart.Enable();
        other.Autostart.Enable();

        other.Autostart.Disable();

        File.Exists(full.Path).Should().BeTrue();
        File.Exists(other.Path).Should().BeFalse();
    }

    private static string PlistText(string label, string[] args, bool runAtLoad)
    {
        var text = LaunchAgentPlist.Build(label, args);
        return runAtLoad ? text : text.Replace("<true />", "<false />").Replace("<true/>", "<false/>");
    }
}
