using BeeMemoryBank.BlindDesktop.Services;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>The line the tray tooltip shows while a long job runs (the notification seam the desktop app really registers).</summary>
public sealed class TrayNotificationsTests
{
    [Fact]
    public void AJobInProgress_ShowsItsPercent_AndClearGoesBackToNothing()
    {
        var tray = new TrayNotifications();
        tray.Line.Should().BeNull();

        tray.Show("First load", 0.42);
        tray.Line.Should().StartWith("First load: 42", "the percent sign is the culture's own");
        tray.Clear();

        tray.Line.Should().BeNull();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    public void ANoticeWithoutProgress_ShowsOnlyItsTitle(double progress)
    {
        var tray = new TrayNotifications();

        tray.Show("Backup finished", progress);

        tray.Line.Should().Be("Backup finished");
    }
}
