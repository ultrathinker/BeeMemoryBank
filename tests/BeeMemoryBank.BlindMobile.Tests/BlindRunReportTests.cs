using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// What a background run tells the screen's log. A scheduled run has no screen: its outcome ("waiting for the
/// computer's key", "backup failed: no room") would otherwise be lost, while every hourly "nothing due" would
/// push the lines that matter out of a log that keeps 500.
/// </summary>
public sealed class BlindRunReportTests
{
    [Fact]
    public void WhatHappened_IsLogged_UnderItsKind()
    {
        var log = NewLog();

        BlindRunReport.Record(log, "backup", "Backup: waiting for the network's next integrity anchor.");

        log.Latest(10).Should().ContainSingle().Which.Should().Match<BlindPhoneLog.Entry>(
            e => e.Kind == "backup" && e.Message.Contains("integrity anchor"));
    }

    [Theory]
    [InlineData("Nothing due.")]
    [InlineData("Not paired yet.")]
    [InlineData("Already running.")]
    [InlineData("")]
    public void ARunThatDidNothing_LeavesNoLine(string message)
    {
        var log = NewLog();

        BlindRunReport.Record(log, "backup", message);

        log.Latest(10).Should().BeEmpty();
    }

    [Fact]
    public void TheSameMessageTwiceInARow_IsOneLine_ButItIsLoggedAgainAfterAnotherLine()
    {
        var log = NewLog();

        BlindRunReport.Record(log, "backup", "Backup: waiting for the charger.");
        BlindRunReport.Record(log, "backup", "Backup: waiting for the charger.");
        log.Latest(10).Should().HaveCount(1, "an hourly retry of the same wait is not news");
        log.Add("load", "First load finished.");
        BlindRunReport.Record(log, "backup", "Backup: waiting for the charger.");

        log.Latest(10).Should().HaveCount(3);
    }

    private static BlindPhoneLog NewLog() =>
        new(Path.Combine(Path.GetTempPath(), "bmb-s4-runlog-" + Guid.NewGuid().ToString("N") + ".jsonl"), TimeProvider.System);
}
