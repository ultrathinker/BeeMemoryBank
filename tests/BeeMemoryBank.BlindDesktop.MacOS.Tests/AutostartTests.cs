using BeeMemoryBank.BlindDesktop.MacOS;
using BeeMemoryBank.Platforms.Apple.LaunchAgents;

namespace BeeMemoryBank.BlindDesktop.MacOS.Tests;

public class LaunchAgentPlistTests
{
    private static readonly string[] Arguments = ["/Applications/Bee Memory Bank Blind.app/Contents/MacOS/BeeMemoryBankBlind", "--minimized"];

    [Fact]
    public void Build_WritesAWellFormedPlist_WithTheLabelTheArgumentsAndRunAtLoad()
    {
        var text = LaunchAgentPlist.Build("com.beememorybank.blind", Arguments);

        text.Should().StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        text.Should().Contain("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
        text.Should().Contain("<key>Label</key>").And.Contain("<string>com.beememorybank.blind</string>");
        text.Should().Contain("<key>RunAtLoad</key>");
        text.Should().Contain("<string>--minimized</string>");
        text.Should().Contain("<string>/Applications/Bee Memory Bank Blind.app/Contents/MacOS/BeeMemoryBankBlind</string>");
        text.Should().NotContain("\r");
    }

    [Fact]
    public void Build_OnlyStartsAtLogin_InAGraphicalSession_AndIsNotKeptAlive()
    {
        var text = LaunchAgentPlist.Build("com.beememorybank.blind", Arguments);

        // KeepAlive false: Quit means quit; Aqua: a menu-bar app only makes sense in a graphical login session
        text.Should().MatchRegex(@"<key>RunAtLoad</key>\s*<true\s*/>");
        text.Should().MatchRegex(@"<key>KeepAlive</key>\s*<false\s*/>");
        text.Should().MatchRegex(@"<key>LimitLoadToSessionType</key>\s*<string>Aqua</string>");
    }

    [Fact]
    public void ParseOfBuild_GivesBackTheSameThing()
    {
        var info = LaunchAgentPlist.TryParse(LaunchAgentPlist.Build("com.beememorybank.blind", Arguments));

        info.Should().NotBeNull();
        info!.Label.Should().Be("com.beememorybank.blind");
        info.ProgramArguments.Should().Equal(Arguments);
        info.RunAtLoad.Should().BeTrue();
    }

    [Fact]
    public void SpecialCharacters_InPaths_AreEscaped_AndSurviveTheRoundTrip()
    {
        string[] args = ["/Users/a&b/<odd> \"name\"/app", "--minimized"];

        var text = LaunchAgentPlist.Build("com.beememorybank.blind", args);

        text.Should().Contain("a&amp;b").And.Contain("&lt;odd&gt;");
        text.Should().NotContain("a&b");
        LaunchAgentPlist.TryParse(text)!.ProgramArguments.Should().Equal(args);
    }

    [Fact]
    public void TryParse_ReadsAHandWrittenPlist_AndIgnoresKeysItDoesNotKnow()
    {
        const string text = """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key><string>test.label</string>
                <key>Nice</key><integer>5</integer>
                <key>ProgramArguments</key><array><string>/bin/echo</string><string>hi</string></array>
                <key>RunAtLoad</key><true/>
            </dict>
            </plist>
            """;

        var info = LaunchAgentPlist.TryParse(text);

        info.Should().BeEquivalentTo(new LaunchAgentInfo("test.label", ["/bin/echo", "hi"], true));
    }

    [Fact]
    public void TryParse_WithoutRunAtLoad_SaysSo()
    {
        const string text = "<plist version=\"1.0\"><dict><key>Label</key><string>x</string><key>ProgramArguments</key><array><string>/a</string></array></dict></plist>";

        LaunchAgentPlist.TryParse(text)!.RunAtLoad.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<plist><dict></dict></plist>")]
    [InlineData("<plist><array/></plist>")]
    [InlineData("<plist><dict><key>Label</key><string>x</string></dict></plist>")]
    [InlineData("<plist><dict><key>Label</key><string>x</string><key>ProgramArguments</key><array/></dict></plist>")]
    [InlineData("<plist><dict><string>stray</string><string>x</string></dict></plist>")]
    public void TryParse_OfGarbage_IsNull(string? text) => LaunchAgentPlist.TryParse(text).Should().BeNull();

    [Fact]
    public void TryParse_DoesNotFetchOrExpandAnything_FromADoctype()
    {
        const string text = """
            <?xml version="1.0"?>
            <!DOCTYPE plist [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <plist version="1.0"><dict><key>Label</key><string>&xxe;</string><key>ProgramArguments</key><array><string>/a</string></array></dict></plist>
            """;

        var info = LaunchAgentPlist.TryParse(text);

        // the entity is not resolved (or the parse is refused): either way no file content comes out
        (info is null || !info.Label.Contains("root:")).Should().BeTrue();
    }

    [Theory]
    [InlineData("com.beememorybank.blind", true)]
    [InlineData("a", true)]
    [InlineData("test.label-1_x", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("../evil", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("a\\b", false)]
    [InlineData(".hidden", false)]
    [InlineData("trailing.", false)]
    public void Labels_AreSafeFileNames(string? label, bool valid) => LaunchAgentPlist.IsValidLabel(label).Should().Be(valid);

    [Fact]
    public void Build_RefusesABadLabel_NoProgram_AndControlCharacters()
    {
        ((Action)(() => LaunchAgentPlist.Build("../x", Arguments))).Should().Throw<ArgumentException>();
        ((Action)(() => LaunchAgentPlist.Build("ok.label", []))).Should().Throw<ArgumentException>();
        ((Action)(() => LaunchAgentPlist.Build("ok.label", [" "]))).Should().Throw<ArgumentException>();
        ((Action)(() => LaunchAgentPlist.Build("ok.label", ["/a\nb"]))).Should().Throw<ArgumentException>();
    }
}

public class AutostartTests
{
    private const string Label = "test.beememorybank.blind.autostart";
    private static readonly string[] Program = ["/Applications/Blind.app/Contents/MacOS/Blind", "--minimized"];

    private sealed class Rig : IDisposable
    {
        public TempFolder Folder { get; } = new();
        public FakeCommandRunner Runner { get; } = new();
        public string? ServiceName { get; set; }
        public bool Loaded { get; set; }
        public MacOsBlindAutostart Autostart { get; }

        public Rig(bool loadImmediately = true, IReadOnlyList<string>? program = null)
        {
            Autostart = new MacOsBlindAutostart(
                new MacOsBlindAutostartOptions
                {
                    Label = Label,
                    LaunchAgentsDirectory = Folder.File("LaunchAgents"),
                    ProgramArguments = program ?? Program,
                    LoadImmediately = loadImmediately,
                },
                new LaunchctlSimulator(this), () => 501u, name => name == "XPC_SERVICE_NAME" ? ServiceName : null);
        }

        public void Dispose() => Folder.Dispose();
    }

    /// <summary>launchd's side of the three calls: print (loaded?), bootstrap, bootout.</summary>
    private sealed class LaunchctlSimulator(Rig rig) : ICommandRunner
    {
        public List<string> Calls { get; } = [];

        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout)
        {
            var line = string.Join(' ', arguments);
            fileName.Should().Be("/bin/launchctl");
            rig.Runner.Calls.Add(line);
            switch (arguments[0])
            {
                case "print":
                    return rig.Loaded ? new CommandResult(0, "service = " + Label, "", false) : new CommandResult(113, "", "Could not find service", false);
                case "bootstrap":
                    if (rig.Loaded) return new CommandResult(5, "", "Bootstrap failed: 5: Input/output error", false);
                    rig.Loaded = true;
                    return new CommandResult(0, "", "", false);
                case "bootout":
                    if (!rig.Loaded) return new CommandResult(3, "", "Boot-out failed: 3: No such process", false);
                    rig.Loaded = false;
                    return new CommandResult(0, "", "", false);
            }
            return new CommandResult(1, "", "unknown", false);
        }
    }

    [Fact]
    public void Enable_WritesThePlist_AndBootstrapsItIntoTheGuiDomainOfTheUser()
    {
        using var rig = new Rig();

        var result = rig.Autostart.Apply(true);

        result.Should().Be(new MacOsAutostartResult(Enabled: true, Loaded: true, Warning: null));
        rig.Autostart.PlistPath.Should().Be(Path.Combine(rig.Folder.Path, "LaunchAgents", Label + ".plist"));
        File.Exists(rig.Autostart.PlistPath).Should().BeTrue();
        LaunchAgentPlist.TryParse(File.ReadAllText(rig.Autostart.PlistPath))!.ProgramArguments.Should().Equal(Program);
        rig.Runner.Calls.Should().Equal("print gui/501/" + Label, "bootstrap gui/501 " + rig.Autostart.PlistPath);
        rig.Autostart.IsEnabled.Should().BeTrue();
        rig.Autostart.IsCurrent.Should().BeTrue();
    }

    [Fact]
    public void Disable_BootsItOut_AndRemovesThePlist()
    {
        using var rig = new Rig();
        rig.Autostart.Apply(true);
        rig.Runner.Calls.Clear();

        var result = rig.Autostart.Apply(false);

        result.Should().Be(new MacOsAutostartResult(Enabled: false, Loaded: false, Warning: null));
        File.Exists(rig.Autostart.PlistPath).Should().BeFalse();
        rig.Runner.Calls.Should().Contain("bootout gui/501/" + Label);
        rig.Autostart.IsEnabled.Should().BeFalse();
        rig.Loaded.Should().BeFalse();
    }

    [Fact]
    public void Disable_WhenNothingWasEnabled_DoesNothing_AndCallsNoLaunchctlButAProbe()
    {
        using var rig = new Rig();

        var result = rig.Autostart.Apply(false);

        result.Enabled.Should().BeFalse();
        rig.Runner.Calls.Should().Equal("print gui/501/" + Label);
    }

    [Fact]
    public void Disable_FromTheCopyLaunchdStarted_RemovesThePlist_ButDoesNotKillItself()
    {
        using var rig = new Rig();
        rig.Autostart.Apply(true);
        rig.ServiceName = Label;   // launchd sets XPC_SERVICE_NAME to the job's label
        rig.Runner.Calls.Clear();

        var result = rig.Autostart.Apply(false);

        File.Exists(rig.Autostart.PlistPath).Should().BeFalse("the next login must not start the app");
        rig.Runner.Calls.Should().NotContain(c => c.StartsWith("bootout"), "bootout would end the very process that is switching autostart off");
        result.Enabled.Should().BeFalse();
        result.Loaded.Should().BeTrue();
        result.Warning.Should().Contain("started by the login item");
    }

    [Fact]
    public void Enable_Twice_WritesNothingNew_AndDoesNotReload()
    {
        using var rig = new Rig();
        rig.Autostart.Apply(true);
        var written = File.GetLastWriteTimeUtc(rig.Autostart.PlistPath);
        rig.Runner.Calls.Clear();

        var again = rig.Autostart.Apply(true);

        again.Loaded.Should().BeTrue();
        rig.Runner.Calls.Should().Equal("print gui/501/" + Label);
        File.GetLastWriteTimeUtc(rig.Autostart.PlistPath).Should().Be(written);
    }

    [Fact]
    public void AppMoved_Enable_RewritesThePlist_AndReloadsTheJob()
    {
        using var rig = new Rig();
        rig.Autostart.Apply(true);
        // same folder and simulated launchd state, new program path
        var second = new MacOsBlindAutostart(
            new MacOsBlindAutostartOptions { Label = Label, LaunchAgentsDirectory = rig.Folder.File("LaunchAgents"), ProgramArguments = ["/Users/someone/Apps/Blind", "--minimized"] },
            new LaunchctlSimulator(rig), () => 501u, _ => null);
        second.IsCurrent.Should().BeFalse();
        rig.Runner.Calls.Clear();

        var result = second.Apply(true);

        result.Loaded.Should().BeTrue();
        LaunchAgentPlist.TryParse(File.ReadAllText(second.PlistPath))!.ProgramArguments.Should().Equal("/Users/someone/Apps/Blind", "--minimized");
        rig.Runner.Calls.Should().Contain("bootout gui/501/" + Label).And.Contain(c => c.StartsWith("bootstrap gui/501"));
        second.IsCurrent.Should().BeTrue();
    }

    [Fact]
    public void LaunchdRefusing_TheLoad_IsAWarning_NotAnError_TheFileIsInPlace()
    {
        using var rig = new Rig();
        var refusing = new MacOsBlindAutostart(
            new MacOsBlindAutostartOptions { Label = Label, LaunchAgentsDirectory = rig.Folder.File("LaunchAgents"), ProgramArguments = Program },
            new FakeCommandRunner()
                .On("/bin/launchctl", "print gui/501/" + Label, "", exitCode: 113)
                .On("/bin/launchctl", "bootstrap gui/501 " + Path.Combine(rig.Folder.Path, "LaunchAgents", Label + ".plist"), "", exitCode: 125, error: "Domain does not support specified action"),
            () => 501u, _ => null);

        var result = refusing.Apply(true);

        result.Enabled.Should().BeTrue();
        result.Loaded.Should().BeFalse();
        result.Warning.Should().Contain("starts at the next login").And.Contain("125");
        refusing.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void WithoutLoadImmediately_OnlyThePlistIsWritten()
    {
        using var rig = new Rig(loadImmediately: false);

        var result = rig.Autostart.Apply(true);

        result.Enabled.Should().BeTrue();
        rig.Runner.Calls.Should().NotContain(c => c.StartsWith("bootstrap") || c.StartsWith("bootout"));
    }

    [Fact]
    public void IsEnabled_IsFalse_ForAPlistThatIsNotOurs()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.Autostart.PlistPath)!);

        File.WriteAllText(rig.Autostart.PlistPath, LaunchAgentPlist.Build("someone.elses.label", ["/bin/true"]));
        rig.Autostart.IsEnabled.Should().BeFalse("the label inside is not ours");

        File.WriteAllText(rig.Autostart.PlistPath, "garbage");
        rig.Autostart.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void TheSeamMethod_SetEnabled_IsApply()
    {
        using var rig = new Rig();
        BeeMemoryBank.BlindMobile.Services.Blind.IBlindAutostart seam = rig.Autostart;

        seam.SetEnabled(true);
        rig.Autostart.IsEnabled.Should().BeTrue();
        seam.SetEnabled(false);
        rig.Autostart.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void ThePlist_IsWrittenAtomically_NoTemporaryFileStays_AndItIsReadableByOthers()
    {
        using var rig = new Rig();

        rig.Autostart.Apply(true);

        Directory.GetFiles(Path.GetDirectoryName(rig.Autostart.PlistPath)!).Should().ContainSingle().Which.Should().Be(rig.Autostart.PlistPath);
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(rig.Autostart.PlistPath).Should().Be(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                "launchd refuses a plist that others can write");
    }

    [Fact]
    public void ABadLabel_IsRefusedWhenTheAutostartIsMade()
    {
        var act = () => new MacOsBlindAutostart(new MacOsBlindAutostartOptions { Label = "../evil" });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TheDefaultProgram_IsTheRunningApp_ThenMinimized()
    {
        var autostart = new MacOsBlindAutostart(new MacOsBlindAutostartOptions { Label = Label, LaunchAgentsDirectory = Path.GetTempPath() });

        autostart.ProgramArguments[^1].Should().Be("--minimized");
        autostart.ProgramArguments.Count.Should().BeInRange(2, 3);
        autostart.ProgramArguments[0].Should().Be(Environment.ProcessPath);
    }

    [Fact]
    public void TheDefaultLabelAndLocation_AreTheBlindAppsOwn()
    {
        var autostart = new MacOsBlindAutostart();

        autostart.Label.Should().Be("com.beememorybank.blind");
        autostart.PlistPath.Should().EndWith(Path.Combine("Library", "LaunchAgents", "com.beememorybank.blind.plist"));
    }
}
