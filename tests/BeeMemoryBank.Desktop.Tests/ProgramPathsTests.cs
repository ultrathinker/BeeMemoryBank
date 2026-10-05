using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BeeMemoryBank.Desktop.MacOS;
using BeeMemoryBank.Platforms.Apple.LaunchAgents;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>A file system that is only a table of symbolic links (path -> target text as stored).</summary>
internal sealed class FakeLinks(Dictionary<string, string>? links = null) : ILinkReader
{
    public Dictionary<string, string> Links { get; } = links ?? [];

    public string? ReadLink(string absolutePath) => Links.TryGetValue(absolutePath, out var target) ? target : null;
}

/// <summary>The two meanings of a path (as written with dots taken out, and as the system reaches it through links), as pure text.</summary>
public sealed class ProgramPathsTests
{
    [Theory]
    [InlineData("/Applications/Bee Memory Bank.app/Contents/MacOS/x", "/Applications/Bee Memory Bank.app/Contents/MacOS/x")]
    [InlineData("/a/b/../c", "/a/c")]
    [InlineData("/a/./b//c/", "/a/b/c")]
    [InlineData("/a/../../..", "/")]
    [InlineData("/../tmp/x", "/tmp/x")]
    [InlineData("/", "/")]
    public void Lexical_TakesDotsAndEmptySegmentsOut_WithoutLookingAtTheDisk(string path, string expected)
    {
        ProgramPaths.Lexical(path).Should().Be(expected);
    }

    [Fact]
    public void Physical_WithoutLinks_IsTheLexicalPath()
    {
        ProgramPaths.Physical("/a/b/../c/./d", new FakeLinks()).Should().Be("/a/c/d");
    }

    [Fact]
    public void Physical_FollowsAnAbsoluteLinkInAFolder()
    {
        var links = new FakeLinks { Links = { ["/Users/x/Apps/Bundle"] = "/Users/x/src/bin/Debug/net10.0" } };

        ProgramPaths.Physical("/Users/x/Apps/Bundle/BeeMemoryBank.Desktop", links).Should().Be("/Users/x/src/bin/Debug/net10.0/BeeMemoryBank.Desktop");
    }

    [Fact]
    public void Physical_FollowsARelativeLink_FromTheFolderTheLinkIsIn()
    {
        var links = new FakeLinks { Links = { ["/Users/x/Apps/Bee"] = "../src/obj/Debug/out" } };

        ProgramPaths.Physical("/Users/x/Apps/Bee/app", links).Should().Be("/Users/x/src/obj/Debug/out/app");
    }

    [Fact]
    public void Physical_FollowsALinkOnTheFileItself_AndAChainOfLinks()
    {
        var links = new FakeLinks
        {
            Links =
            {
                ["/Users/x/bin/bmb"] = "/Users/x/other/bmb2",
                ["/Users/x/other/bmb2"] = "/Users/x/src/bin/Release/app",
            },
        };

        ProgramPaths.Physical("/Users/x/bin/bmb", links).Should().Be("/Users/x/src/bin/Release/app");
    }

    [Fact]
    public void Physical_DotDotAfterALink_GoesUpFromWhereTheLinkReallyLeads_NotFromWhereItWasWritten()
    {
        var links = new FakeLinks { Links = { ["/Users/x/links/up"] = "/Users/x/src/bin/Debug/net10.0/sub" } };

        ProgramPaths.Lexical("/Users/x/links/up/../app").Should().Be("/Users/x/links/app", "written, it looks harmless");
        ProgramPaths.Physical("/Users/x/links/up/../app", links).Should().Be("/Users/x/src/bin/Debug/net10.0/app", "really, it is in the build folder");
    }

    [Fact]
    public void Physical_ALoop_IsNull()
    {
        var links = new FakeLinks { Links = { ["/a/one"] = "/a/two", ["/a/two"] = "/a/one" } };

        ProgramPaths.Physical("/a/one/x", links).Should().BeNull();
    }

    [Fact]
    public void Physical_ASelfReference_IsNull()
    {
        ProgramPaths.Physical("/a/self", new FakeLinks { Links = { ["/a/self"] = "self" } }).Should().BeNull();
    }
}

/// <summary>The build/temp refusal applied to the path as given, as written without dots, and as the system reaches it.</summary>
public sealed class ProgramLocationPolicyLinksTests
{
    private const string Temp = "/var/folders/ab/cdef/T/";

    private static string? Refusal(string program, FakeLinks? links = null) => ProgramLocationPolicy.Refusal(program, Temp, links ?? new FakeLinks());

    [Fact]
    public void AnInstalledLookingPath_IsAccepted()
    {
        Refusal("/Users/x/bmb-src/run/Bee Memory Bank.app/Contents/MacOS/BeeMemoryBank.Desktop").Should().BeNull();
        Refusal("/Applications/Bee Memory Bank.app/Contents/MacOS/BeeMemoryBank.Desktop").Should().BeNull();
    }

    [Fact]
    public void DotsThatStayInsideAGoodFolder_AreAccepted()
    {
        Refusal("/Applications/Other/../Bee Memory Bank.app/Contents/./MacOS/BeeMemoryBank.Desktop").Should().BeNull();
    }

    [Fact]
    public void ALinkInANormalFolder_ToAnInstalledBundle_IsAccepted()
    {
        var links = new FakeLinks { Links = { ["/Users/x/Apps/BMB"] = "/Applications/Bee Memory Bank.app" } };

        Refusal("/Users/x/Apps/BMB/Contents/MacOS/BeeMemoryBank.Desktop", links).Should().BeNull();
    }

    [Fact]
    public void ALinkInANormalFolder_IntoABinDebugFolder_IsRefused_AndTheReasonSaysWhere()
    {
        var links = new FakeLinks { Links = { ["/Users/x/Apps/Bundle"] = "/Users/x/src/desktop/bin/Debug/net10.0" } };

        var reason = Refusal("/Users/x/Apps/Bundle/BeeMemoryBank.Desktop", links);

        reason.Should().Contain("build folder").And.Contain("/Users/x/Apps/Bundle/BeeMemoryBank.Desktop").And.Contain("/Users/x/src/desktop/bin/Debug/net10.0/BeeMemoryBank.Desktop");
    }

    [Fact]
    public void ALinkOnTheFileItself_IntoABuildFolder_IsRefused()
    {
        var links = new FakeLinks { Links = { ["/Users/x/bin/bmb"] = "/Users/x/src/obj/Release/app" } };

        Refusal("/Users/x/bin/bmb", links).Should().Contain("build folder");
    }

    [Theory]
    [InlineData("/tmp")]
    [InlineData("/private/tmp")]
    [InlineData("/var/folders/ab/cdef/T")]
    [InlineData("/private/var/folders/ab/cdef/T")]
    public void ALink_IntoATempFolder_IsRefused(string tempTarget)
    {
        var links = new FakeLinks { Links = { ["/Users/x/Apps/Tmp"] = tempTarget } };

        Refusal("/Users/x/Apps/Tmp/BeeMemoryBank.Desktop", links).Should().Contain("temporary");
    }

    [Fact]
    public void ARelativeLink_IntoABuildFolder_IsRefused()
    {
        var links = new FakeLinks { Links = { ["/Users/x/Apps/Bee"] = "../src/obj/Debug/out" } };

        Refusal("/Users/x/Apps/Bee/app", links).Should().Contain("build folder");
    }

    [Fact]
    public void DotDotThatLandsInTmp_IsRefused_EvenWhenNothingIsThere()
    {
        var reason = Refusal("/Users/x/bmb-src/run/../../../../tmp/app/BeeMemoryBank.Desktop");

        reason.Should().Contain("temporary").And.Contain("means '/tmp/app/BeeMemoryBank.Desktop'");
    }

    [Fact]
    public void DotDotThatLandsInABuildFolder_IsRefused()
    {
        // written, "bin/x/../Debug" does not look like bin/Debug; with the dots taken out it is exactly that
        Refusal("/Users/x/src/bin/x/../Debug/net10.0/app").Should().Contain("build folder").And.Contain("means '/Users/x/src/bin/Debug/net10.0/app'");
    }

    [Fact]
    public void DotDotAfterALink_IsJudgedWhereItReallyLeads()
    {
        var links = new FakeLinks { Links = { ["/Users/x/links/up"] = "/Users/x/src/bin/Debug/net10.0/sub" } };

        var reason = Refusal("/Users/x/links/up/../BeeMemoryBank.Desktop", links);

        reason.Should().Contain("build folder").And.Contain("leads to '/Users/x/src/bin/Debug/net10.0/BeeMemoryBank.Desktop'");
    }

    [Fact]
    public void ALinkToTheDotnetHost_IsRefused()
    {
        var links = new FakeLinks { Links = { ["/Users/x/bin/bmb"] = "/usr/local/share/dotnet/dotnet" } };

        Refusal("/Users/x/bin/bmb", links).Should().Contain("dotnet host");
    }

    [Fact]
    public void ALinkLoop_IsRefused_WithAReason_NotAHang()
    {
        var links = new FakeLinks { Links = { ["/Users/x/a"] = "/Users/x/b", ["/Users/x/b"] = "/Users/x/a" } };

        Refusal("/Users/x/a/BeeMemoryBank.Desktop", links).Should().Contain("symbolic links");
    }

    [Fact]
    public void TheRulesOnTheRawText_StillHold_WithoutAnyLinks()
    {
        Refusal("/Users/x/src/bin/Debug/net10.0/app").Should().Contain("build folder");
        Refusal("/tmp/app").Should().Contain("temporary");
        Refusal("relative").Should().Contain("absolute");
        Refusal("").Should().Contain("not known");
    }

    [Fact]
    public void TheAutostart_UsesTheSameRule_ForAProgramReachedThroughALink()
    {
        var agents = TestScratch.New("agents-links");
        var links = new FakeLinks { Links = { ["/Users/x/Apps/Bundle"] = "/Users/x/src/bin/Debug/net10.0" } };
        var autostart = new MacOsAutostart(
            new MacOsAutostartOptions { LaunchAgentsDirectory = agents, ProgramArguments = ["/Users/x/Apps/Bundle/BeeMemoryBank.Desktop", "--minimized"] },
            new FakeCommandRunner(), () => 501, _ => null, () => null, _ => true, Temp, links);

        var enable = () => autostart.Enable();

        enable.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("build folder");
        Directory.GetFileSystemEntries(agents).Should().BeEmpty("a refused login item is not written");
    }

    [Fact]
    public void TheAutostart_AcceptsALinkIntoAGoodBundle_AndWritesTheProgramAsGiven()
    {
        var agents = TestScratch.New("agents-links-ok");
        var links = new FakeLinks { Links = { ["/Users/x/Apps/BMB"] = "/Applications/Bee Memory Bank.app" } };
        var program = "/Users/x/Apps/BMB/Contents/MacOS/BeeMemoryBank.Desktop";
        var autostart = new MacOsAutostart(
            new MacOsAutostartOptions { LaunchAgentsDirectory = agents, ProgramArguments = [program, "--minimized"] },
            new FakeCommandRunner(), () => 501, _ => null, () => null, _ => true, Temp, links);

        autostart.Enable();

        LaunchAgentPlist.TryParse(File.ReadAllText(autostart.PlistPath))!.ProgramArguments.Should().Equal(program, "--minimized");
        autostart.IsEnabled.Should().BeTrue();
    }
}
