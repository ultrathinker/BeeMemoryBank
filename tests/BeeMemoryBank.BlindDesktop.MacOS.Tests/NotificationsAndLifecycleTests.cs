using BeeMemoryBank.BlindDesktop.MacOS;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.MacOS.Tests;

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan duration) => _now += duration;
    public override DateTimeOffset GetUtcNow() => _now;
}

public class AppleScriptTextTests
{
    [Theory]
    [InlineData("Backup", "\"Backup\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("back\\slash", "\"back\\\\slash\"")]
    [InlineData("ends with backslash\\", "\"ends with backslash\\\\\"")]
    [InlineData("two\nlines\r\nand\ttab", "\"two lines  and tab\"")]
    public void Quote_EscapesBackslashAndQuote_AndFlattensControlCharacters(string text, string expected) =>
        AppleScriptText.Quote(text).Should().Be(expected);

    [Fact]
    public void Quote_OfNull_IsTheEmptyLiteral() => AppleScriptText.Quote(null).Should().Be("\"\"");

    [Fact]
    public void Quote_CannotBeBrokenOutOf_ByAnyHostileText()
    {
        string[] hostile =
        [
            "\" & (do shell script \"touch /tmp/x\") & \"",
            "\\\" & (do shell script \"x\") & \\\"",
            "\\",
            "\"\"\"",
            "x\"\n\"y",
            "end tell\ndo shell script \"x\"",
            "x" + (char)0x2028 + (char)0x2029 + (char)0x85 + (char)0 + "y",
        ];
        foreach (var text in hostile)
        {
            var literal = AppleScriptText.Quote(text);
            // walking the literal the way AppleScript does: the first unescaped quote after the opening one must be the last character
            var i = 1;
            while (i < literal.Length - 1)
            {
                if (literal[i] == '\\') { i += 2; continue; }
                literal[i].Should().NotBe('"', "an unescaped quote would end the literal early: " + literal);
                i++;
            }
            i.Should().Be(literal.Length - 1);
            literal[^1].Should().Be('"');
            literal.Should().NotContain("\n").And.NotContain("\r");
        }
    }

    [Fact]
    public void Quote_CapsTheLength_WithoutSplittingASurrogatePair()
    {
        var text = new string('a', 197) + char.ConvertFromUtf32(0x1F41D) + "tail";   // the bee emoji straddles the cap

        var literal = AppleScriptText.Quote(text, 198);

        literal.Should().EndWith("...\"");
        literal.Should().NotContain("tail");
        for (var k = 0; k < literal.Length; k++)
            if (char.IsHighSurrogate(literal[k])) char.IsLowSurrogate(literal[k + 1]).Should().BeTrue("a surrogate pair is kept whole");
    }

    [Fact]
    public void DisplayNotification_IsOneStatement_WithTheTitleAndTheBodyAsLiterals()
    {
        AppleScriptText.DisplayNotification("Backup", "42 %").Should().Be("display notification \"42 %\" with title \"Backup\"");
    }
}

public class NotificationsTests
{
    private static (MacOsBlindNotifications Notifier, FakeCommandRunner Runner, ManualTime Time) Make(TimeSpan? interval = null)
    {
        var runner = new FakeCommandRunner();
        var time = new ManualTime();
        return (new MacOsBlindNotifications(runner, time, interval ?? TimeSpan.FromSeconds(30), background: false), runner, time);
    }

    [Theory]
    [InlineData("Backup", 0.0, "Backup", "Working...")]
    [InlineData("Backup", -1.0, "Backup", "Working...")]
    [InlineData("Backup", 0.424, "Backup", "42 %")]
    [InlineData("Backup", 0.5, "Backup", "50 %")]
    [InlineData("Initial load", 1.0, "Initial load", "Done.")]
    [InlineData("  ", 0.1, "Bee Memory Bank", "10 %")]
    public void Compose_TitleAndProgressText(string title, double progress, string expectedTitle, string expectedBody) =>
        MacOsBlindNotifications.Compose(title, progress).Should().Be((expectedTitle, expectedBody));

    [Fact]
    public void Show_RunsOsascript_WithOneScriptArgument_NoShell()
    {
        var (notifier, runner, _) = Make();

        notifier.Show("Backup", 0.42);

        runner.Calls.Should().Equal("/usr/bin/osascript -e display notification \"42 %\" with title \"Backup\"");
    }

    [Fact]
    public void Show_WithAHostileTitle_PutsItInsideTheLiteralOnly()
    {
        var (notifier, runner, _) = Make();

        notifier.Show("\" & (do shell script \"touch /tmp/x\") & \"", 0.5);

        runner.Calls.Single().Should().Contain("with title \"\\\" & (do shell script \\\"touch /tmp/x\\\") & \\\"\"");
    }

    [Fact]
    public void Show_IsRateLimited_PerTitle_ButTheEndOfAJobAlwaysShows()
    {
        var (notifier, runner, time) = Make();

        notifier.Show("Backup", 0.0);
        notifier.Show("Backup", 0.1);
        notifier.Show("Backup", 0.2);
        runner.Calls.Should().HaveCount(1);

        notifier.Show("Initial load", 0.1);   // another job is not held back by the first
        runner.Calls.Should().HaveCount(2);

        notifier.Show("Backup", 1.0);          // done
        runner.Calls.Should().HaveCount(3);

        time.Advance(TimeSpan.FromSeconds(31));
        notifier.Show("Backup", 0.3);
        runner.Calls.Should().HaveCount(4);
    }

    [Fact]
    public void Show_NeverThrows_WhenTheToolFails_OrTheRunnerThrows()
    {
        var failing = new MacOsBlindNotifications(new FakeCommandRunner().Fail("/usr/bin/osascript", "-e x"), new ManualTime(), TimeSpan.Zero, background: false);
        var act1 = () => failing.Show("Backup", 0.5);
        act1.Should().NotThrow();

        var throwing = new MacOsBlindNotifications(new ThrowingRunner(), new ManualTime(), TimeSpan.Zero, background: false);
        var act2 = () => throwing.Show("Backup", 0.5);
        act2.Should().NotThrow();
    }

    private sealed class ThrowingRunner : ICommandRunner
    {
        public CommandResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void TheDefaultNotifier_CanBeMade_AndShowDoesNotThrow_EvenWhereOsascriptDoesNotExist()
    {
        // On a PC /usr/bin/osascript is missing: the process cannot be started and Show is a no-op. (On a Mac this test would show a
        // banner, so it only runs the call where the tool is absent.)
        if (File.Exists("/usr/bin/osascript")) return;
        var notifier = new MacOsBlindNotifications();

        var act = () => notifier.Show("Backup", 0.5);

        act.Should().NotThrow();
    }
}

public class LifecycleTests
{
    private sealed class FakeScheduler : IBlindScheduler
    {
        public int Ensured, Cancelled;
        public void EnsureScheduled() => Ensured++;
        public void Cancel() => Cancelled++;
    }

    [Fact]
    public void StopBackgroundWork_CancelsTheSchedulerAndTheRunningJobs()
    {
        var scheduler = new FakeScheduler();
        var lifecycle = new MacOsBlindLifecycle(scheduler);
        var token = lifecycle.JobToken;

        lifecycle.StopBackgroundWork();

        scheduler.Cancelled.Should().Be(1);
        token.IsCancellationRequested.Should().BeTrue();
        lifecycle.RestartNeeded.Should().BeFalse("stopping is not the end of the wipe");
    }

    [Fact]
    public void StopBackupService_CancelsTheRunningJobs()
    {
        var lifecycle = new MacOsBlindLifecycle();
        var token = lifecycle.JobToken;

        lifecycle.StopBackupService();

        token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void Stopping_CanBeRepeated_AndWorksWithoutAScheduler()
    {
        var lifecycle = new MacOsBlindLifecycle();

        var act = () => { lifecycle.StopBackgroundWork(); lifecycle.StopBackgroundWork(); lifecycle.StopBackupService(); };

        act.Should().NotThrow();
    }

    [Fact]
    public void RestartAfterWipe_ReportsRestartNeeded_RaisesTheEventOnce_AndGivesAFreshToken()
    {
        var lifecycle = new MacOsBlindLifecycle();
        var raised = 0;
        lifecycle.RestartRequested += () => raised++;
        lifecycle.StopBackgroundWork();
        lifecycle.JobToken.IsCancellationRequested.Should().BeTrue();

        lifecycle.RestartAfterWipe();

        lifecycle.RestartNeeded.Should().BeTrue();
        raised.Should().Be(1);
        lifecycle.JobToken.IsCancellationRequested.Should().BeFalse("after the return to first run, the jobs the host starts next are not born cancelled");

        lifecycle.AcknowledgeRestart();
        lifecycle.RestartNeeded.Should().BeFalse();
    }

    [Fact]
    public void TheHostCanBeLeftWithoutAHandler()
    {
        var lifecycle = new MacOsBlindLifecycle();

        var act = lifecycle.RestartAfterWipe;

        act.Should().NotThrow();
    }

    [Fact]
    public async Task ARunningJob_ObservingTheToken_IsStoppedByTheWipe()
    {
        var lifecycle = new MacOsBlindLifecycle();
        using var started = new ManualResetEventSlim();
        var job = Task.Run(async () =>
        {
            started.Set();
            try { await Task.Delay(TimeSpan.FromSeconds(30), lifecycle.JobToken); return "finished"; }
            catch (OperationCanceledException) { return "cancelled"; }
        });
        started.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        lifecycle.StopBackupService();

        (await job.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("cancelled");
    }

    [Fact]
    public void TheLifecycle_RunsInTheOrderOfTheWipe_ThroughBlindPhoneReset()
    {
        // BlindPhoneReset.Wipe stops the background work, then the backup service, ... , then WipeAndRestart reports the restart.
        var order = new List<string>();
        var scheduler = new RecordingScheduler(order);
        var lifecycle = new MacOsBlindLifecycle(scheduler);
        lifecycle.RestartRequested += () => order.Add("restart");
        lifecycle.JobToken.Register(() => order.Add("jobs-cancelled"));

        lifecycle.StopBackgroundWork();
        lifecycle.StopBackupService();
        lifecycle.RestartAfterWipe();

        order.Should().Equal("scheduler-cancelled", "jobs-cancelled", "restart");
    }

    private sealed class RecordingScheduler(List<string> order) : IBlindScheduler
    {
        public void EnsureScheduled() { }
        public void Cancel() => order.Add("scheduler-cancelled");
    }
}
