using System;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// The rules for BMB_MAC_TEST_ROOT, the one folder the real launchd / osascript / pmset / symlink tests work in. Pure text, so they run on
/// every OS: a wrong or missing value must make those tests SKIP with a reason, never pass or fail by accident of the machine's TMPDIR.
/// </summary>
public sealed class MacTestRootTests
{
    private const string Home = "/Users/someone";
    private const string Temp = "/var/folders/ab/cdef/T/";

    [Theory]
    [InlineData("/Users/someone/bmb-src/run-1/root")]
    [InlineData("/Users/someone/bmb-src/run-1/root/")]
    [InlineData("/Users/someone/mac tests with spaces")]
    [InlineData("/Users/someone/x")]
    public void AFolderInsideTheHome_ThatIsNotTemporary_IsUsable(string value)
    {
        MacTestRoot.Problem(value, Home, Temp).Should().BeNull();
    }

    [Theory]
    [InlineData(null, "set BMB_MAC_TEST_ROOT")]
    [InlineData("", "set BMB_MAC_TEST_ROOT")]
    [InlineData("   ", "set BMB_MAC_TEST_ROOT")]
    [InlineData("relative/folder", "absolute")]
    [InlineData("/Users/someone", "inside the home folder")]
    [InlineData("/Users/someone/", "inside the home folder")]
    [InlineData("/", "inside the home folder")]
    [InlineData("/Users/other/bmb-src", "inside the home folder")]
    [InlineData("/Users/someonelse/x", "inside the home folder")]
    [InlineData("/tmp/bmb", "inside the home folder")]
    [InlineData("/Users/someone/../other/x", "dot-dot")]
    [InlineData("/Users/someone/./x", "dot-dot")]
    public void AMissingRelativeOutsideOrUnnormalizedValue_IsNotUsable_AndSaysWhy(string? value, string reasonContains)
    {
        MacTestRoot.Problem(value, Home, Temp).Should().NotBeNull().And.Subject.Should().Contain(reasonContains);
    }

    [Theory]
    [InlineData("/Users/someone/tmp-scratch/T")]   // the machine's own temp folder is configured to live inside the home folder
    public void ATempFolderInsideTheHome_IsNotUsable(string tempInsideHome)
    {
        MacTestRoot.Problem(tempInsideHome + "/run", Home, tempInsideHome).Should().NotBeNull().And.Subject.Should().Contain("temporary");
    }

    [Theory]
    [InlineData("/Users/someone/src/repo/tests/bin/Debug/net10.0/root")]
    [InlineData("/Users/someone/src/repo/tests/obj/Release/root")]
    public void ABuildFolder_IsNotUsable(string value)
    {
        MacTestRoot.Problem(value, Home, Temp).Should().NotBeNull().And.Subject.Should().Contain("build folder");
    }

    [Fact]
    public void TheDefaultTempFolderOfAMac_IsNotUsable_EvenIfTheHomeWereToContainIt()
    {
        MacTestRoot.Problem("/var/folders/ab/cdef/T/bmb", "/var", Temp).Should().NotBeNull().And.Subject.Should().Contain("temporary");
    }
}
