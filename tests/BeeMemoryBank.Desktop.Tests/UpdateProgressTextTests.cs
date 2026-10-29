using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>BMB-32: while the update library unpacks what it downloaded its percent stands still; the window then says it is still working.</summary>
public sealed class UpdateProgressTextTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(35, 2)]
    [InlineData(70, 7)]
    public void WhileThePercentMoves_TheWindowShowsTheDownload(int percent, int secondsSinceChange)
    {
        var view = UpdateProgressText.For("2.7.4", percent, TimeSpan.FromSeconds(secondsSinceChange));

        view.Title.Should().Be("Downloading version 2.7.4");
        view.Body.Should().StartWith($"{percent}%");
        view.Progress.Should().Be(percent);
    }

    [Fact]
    public void WhenThePercentStandsStillMidWay_TheWindowSaysItIsStillWorking_WithABarThatMovesByItself()
    {
        var view = UpdateProgressText.For("2.7.4", 70, UpdateProgressText.StalledAfter);

        view.Progress.Should().Be(-1);
        view.Title.Should().Be("Installing version 2.7.4");
        view.Body.Should().Contain("Still working").And.Contain("minute or two");
        view.Body.Should().NotContain("stop the download", "the unpacking is not something to close the window over");
    }

    [Theory]
    [InlineData(0)]    // nothing has arrived yet: that is waiting for the server, not unpacking
    [InlineData(100)]  // done: the check returns and the window moves on by itself
    public void AtTheEnds_AStandStillIsNotUnpacking(int percent)
    {
        var view = UpdateProgressText.For("2.7.4", percent, TimeSpan.FromMinutes(2));

        view.Title.Should().StartWith("Downloading");
        view.Progress.Should().Be(percent);
    }
}
