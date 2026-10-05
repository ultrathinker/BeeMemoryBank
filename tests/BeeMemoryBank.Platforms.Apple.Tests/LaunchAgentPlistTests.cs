using BeeMemoryBank.Platforms.Apple.LaunchAgents;

namespace BeeMemoryBank.Platforms.Apple.Tests;

/// <summary>
/// The LaunchAgent plist text both apps write (the blind app with its label, the full app with its own). Pure text work, so it runs on
/// every OS. The exact text matters: a plist the system cannot read is a login item that silently never starts, and the blind app's
/// existing files must be read back by its next version (BlindDesktop.MacOS.Tests carries the blind app's own cases).
/// </summary>
public class LaunchAgentPlistTests
{
    private static readonly string[] FullApp = ["/Applications/Bee Memory Bank.app/Contents/MacOS/BeeMemoryBank.Desktop", "--minimized"];

    [Fact]
    public void TheExactText_OfAnAgent_IsStable()
    {
        var text = LaunchAgentPlist.Build("com.example.agent", ["/Applications/Some App.app/Contents/MacOS/app", "--minimized"]);

        text.Should().Be(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n" +
            "\t<dict>\n" +
            "\t\t<key>Label</key>\n" +
            "\t\t<string>com.example.agent</string>\n" +
            "\t\t<key>ProgramArguments</key>\n" +
            "\t\t<array>\n" +
            "\t\t\t<string>/Applications/Some App.app/Contents/MacOS/app</string>\n" +
            "\t\t\t<string>--minimized</string>\n" +
            "\t\t</array>\n" +
            "\t\t<key>RunAtLoad</key>\n" +
            "\t\t<true />\n" +
            "\t\t<key>KeepAlive</key>\n" +
            "\t\t<false />\n" +
            "\t\t<key>LimitLoadToSessionType</key>\n" +
            "\t\t<string>Aqua</string>\n" +
            "\t\t<key>ProcessType</key>\n" +
            "\t\t<string>Interactive</string>\n" +
            "\t</dict>\n" +
            "</plist>\n");
    }

    [Fact]
    public void TheFullAppsShape_PathWithSpaces_RoundTrips()
    {
        var text = LaunchAgentPlist.Build("com.beememorybank.desktop", FullApp);

        var info = LaunchAgentPlist.TryParse(text);

        info.Should().BeEquivalentTo(new LaunchAgentInfo("com.beememorybank.desktop", FullApp, RunAtLoad: true));
        text.Should().MatchRegex(@"<key>KeepAlive</key>\s*<false\s*/>").And.MatchRegex(@"<key>LimitLoadToSessionType</key>\s*<string>Aqua</string>");
    }

    [Fact]
    public void MarkupCharacters_InAPath_AreEscaped_AndComeBackUnchanged()
    {
        string[] args = ["/Users/a&b/<odd> \"name\" 'x'/app", "--arg=1&2"];

        var text = LaunchAgentPlist.Build("com.example.agent", args);

        text.Should().Contain("a&amp;b").And.Contain("&lt;odd&gt;").And.NotContain("a&b").And.NotContain("<odd>");
        LaunchAgentPlist.TryParse(text)!.ProgramArguments.Should().Equal(args);
    }

    [Fact]
    public void ANonAsciiPath_RoundTrips_AsUtf8WithoutABom()
    {
        string[] args = ["/Users/" + "é中" + "/app"];

        var text = LaunchAgentPlist.Build("com.example.agent", args);

        text.Should().NotStartWith("﻿");
        LaunchAgentPlist.TryParse(text)!.ProgramArguments.Should().Equal(args);
    }

    [Theory]
    [InlineData("com.beememorybank.blind", true)]
    [InlineData("com.beememorybank.desktop", true)]
    [InlineData("a", true)]
    [InlineData("com.beememorybank.blind.1a2b3c4d", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("../evil", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData(".hidden", false)]
    [InlineData("trailing.", false)]
    public void ALabel_IsASafeFileName(string? label, bool valid) => LaunchAgentPlist.IsValidLabel(label).Should().Be(valid);

    [Fact]
    public void Build_RefusesWhatCouldNotBeAnAgent()
    {
        ((Action)(() => LaunchAgentPlist.Build("../x", FullApp))).Should().Throw<ArgumentException>();
        ((Action)(() => LaunchAgentPlist.Build("ok.label", []))).Should().Throw<ArgumentException>();
        ((Action)(() => LaunchAgentPlist.Build("ok.label", [" "]))).Should().Throw<ArgumentException>();
        ((Action)(() => LaunchAgentPlist.Build("ok.label", ["/a\nb"]))).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<plist><dict></dict></plist>")]
    [InlineData("<plist><dict><key>Label</key><string>x</string></dict></plist>")]
    [InlineData("<plist><dict><key>Label</key><string>x</string><key>ProgramArguments</key><array/></dict></plist>")]
    public void Garbage_IsNotAnAgent(string? text) => LaunchAgentPlist.TryParse(text).Should().BeNull();

    [Fact]
    public void AnExternalEntity_IsNeitherFetchedNorExpanded()
    {
        const string text = """
            <?xml version="1.0"?>
            <!DOCTYPE plist [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <plist version="1.0"><dict><key>Label</key><string>&xxe;</string><key>ProgramArguments</key><array><string>/a</string></array></dict></plist>
            """;

        var info = LaunchAgentPlist.TryParse(text);

        (info is null || !info.Label.Contains("root:")).Should().BeTrue();
    }
}
