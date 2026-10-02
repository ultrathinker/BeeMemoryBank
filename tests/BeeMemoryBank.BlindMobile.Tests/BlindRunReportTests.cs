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

    /// <summary>
    /// A Release build on a phone has no debugger and no logcat of caught exceptions: the first real backup stopped with
    /// "NullReferenceException: Object reference not set" and nothing said where. The log line names the calling methods.
    /// </summary>
    [Fact]
    public void AnUnexpectedError_NamesTheMethodsItWasThrownFrom()
    {
        var log = NewLog();
        Exception error;
        try { ThrowFromTheDeepestMethod(); throw new InvalidOperationException("unreachable"); }
        catch (NullReferenceException e) { error = e; }

        BlindRunReport.RecordFailure(log, "backup", error);

        var message = log.Latest(10).Should().ContainSingle().Which.Message;
        message.Should().Contain(nameof(ThrowFromTheDeepestMethod), "the line must say where it happened");
        message.Should().Contain(nameof(AnUnexpectedError_NamesTheMethodsItWasThrownFrom), "and who called it");
        message.Should().NotContain(@"D:\", "no build-machine paths in a user's log");
    }

    private static void ThrowFromTheDeepestMethod() => throw new NullReferenceException();

    [Fact]
    public void AnUnexpectedError_IsLoggedWithItsTypeAndMessage_OnceWhileItRepeats()
    {
        var log = NewLog();

        BlindRunReport.RecordFailure(log, "run", new InvalidOperationException("The blind phone identity has not been recorded."));
        BlindRunReport.RecordFailure(log, "run", new InvalidOperationException("The blind phone identity has not been recorded."));

        var line = log.Latest(10).Should().ContainSingle().Which;
        line.Kind.Should().Be("run");
        line.Message.Should().Contain("InvalidOperationException").And.Contain("identity has not been recorded");
    }

    private static BlindPhoneLog NewLog() =>
        new(Path.Combine(Path.GetTempPath(), "bmb-s4-runlog-" + Guid.NewGuid().ToString("N") + ".jsonl"), TimeProvider.System);
}
