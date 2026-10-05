using BeeMemoryBank.BlindDesktop.Platform;
using BeeMemoryBank.BlindMobile.Services.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// The start-up path of the app (<see cref="Program.Run"/>: everything <c>Main</c> does except the process-wide handlers and the UI): a
/// data folder that cannot be used ends the start with exit code 1 and a line in the error log, not with an unhandled exception in the
/// middle of the start; a second start hands over to the running copy; the single-instance guard is held for the life of the UI.
/// </summary>
[Collection("ErrorLog")]
public sealed class ProgramStartupTests
{
    private sealed class Outcome
    {
        public int Exit;
        public string Out = "";
        public string Err = "";
        public int UiStarts;
        public string Log = "";
    }

    /// <summary>Runs the start-up with the error log redirected to a file of this test; the previous log path comes back afterwards.</summary>
    private static Outcome Start(string[] args, Func<string?, IBlindDesktopPlatform> createPlatform, Func<string[], int>? ui = null)
    {
        var outcome = new Outcome();
        var original = ErrorLog.PathOfLog;
        var log = Path.Combine(TestFolders.New("startup-log"), "error.log");
        try
        {
            ErrorLog.UsePath(log);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            outcome.Exit = Program.Run(args, stdout, stderr, createPlatform, a =>
            {
                outcome.UiStarts++;
                return ui?.Invoke(a) ?? 0;
            });
            outcome.Out = stdout.ToString();
            outcome.Err = stderr.ToString();
        }
        finally
        {
            ErrorLog.UsePath(original);
        }
        outcome.Log = File.Exists(log) ? File.ReadAllText(log) : "";
        return outcome;
    }

    [Fact]
    public void ADataFolderThatIsAFile_EndsTheStartWithExit1_AndALineInTheErrorLog_NotAnUnhandledException()
    {
        var file = Path.Combine(TestFolders.New("startup"), "not-a-folder");
        File.WriteAllText(file, "I am a file");

        var outcome = Start(["--data-dir", file], PlatformSelector.Create);

        outcome.Exit.Should().Be(1);
        outcome.UiStarts.Should().Be(0, "the UI is never started over a folder that cannot be used");
        outcome.Log.Should().Contain("The data folder cannot be used.").And.Match("*Exception*");
        File.ReadAllText(file).Should().Be("I am a file", "the file is left as it was");
    }

    [Fact]
    public void ADataFolderBelowAFile_CannotBeMade_SameAnswer()
    {
        var file = Path.Combine(TestFolders.New("startup"), "not-a-folder");
        File.WriteAllText(file, "I am a file");

        var outcome = Start(["--data-dir", Path.Combine(file, "BeeMemoryBankBlind")], PlatformSelector.Create);

        outcome.Exit.Should().Be(1);
        outcome.UiStarts.Should().Be(0);
        outcome.Log.Should().Contain("The data folder cannot be used.");
    }

    [Fact]
    public void APlatformThatCannotBeCreated_EndsTheStartWithExit1_AndALogLine()
    {
        var io = Start([], _ => throw new IOException("the folder is read-only"));
        var other = Start([], _ => throw new PlatformNotSupportedException("no adapters for this system"));

        io.Exit.Should().Be(1);
        io.Log.Should().Contain("The data folder cannot be used.").And.Contain("the folder is read-only");
        other.Exit.Should().Be(1);
        other.Log.Should().Contain("The app cannot run on this computer.").And.Contain("no adapters");
        (io.UiStarts + other.UiStarts).Should().Be(0);
    }

    [Fact]
    public void AGuardThatThrows_EndsTheStartWithExit1_AndALogLine()
    {
        var folder = TestFolders.New("startup");

        var outcome = Start(["--data-dir", folder], f => new ThrowingGuardPlatform(PlatformSelector.Create(f)));

        outcome.Exit.Should().Be(1);
        outcome.UiStarts.Should().Be(0);
        outcome.Log.Should().Contain("The data folder cannot be used.").And.Contain("UnauthorizedAccessException");
    }

    [Fact]
    public void AGoodStart_HoldsTheOneCopyGuardWhileTheUiRuns_AndReleasesItAfterwards()
    {
        var folder = TestFolders.New("startup");
        bool secondRefusedDuringUi = false;

        var outcome = Start(["--data-dir", folder], PlatformSelector.Create, _ =>
        {
            using var second = PlatformSelector.Create(folder).TryAcquireInstance();
            secondRefusedDuringUi = second is null;
            Program.Startup.Should().NotBeNull();
            Program.Startup!.Instance.Should().NotBeNull();
            Program.Startup.Options.DataDirectory.Should().Be(folder);
            return 7;
        });

        outcome.Exit.Should().Be(7, "the exit code of the UI is the exit code of the app");
        outcome.UiStarts.Should().Be(1);
        secondRefusedDuringUi.Should().BeTrue();
        using var after = PlatformSelector.Create(folder).TryAcquireInstance();
        after.Should().NotBeNull("the guard went with the end of the UI");
        outcome.Log.Should().BeEmpty("a good start writes nothing to the error log");
    }

    [Fact]
    public void ASecondStart_HandsOverToTheRunningCopy_AndEndsWithExit0_WithoutStartingTheUi()
    {
        var folder = TestFolders.New("startup");
        using var running = PlatformSelector.Create(folder).TryAcquireInstance()!;
        using var shown = new ManualResetEventSlim();
        running.Listen(shown.Set);

        var outcome = Start(["--data-dir", folder], PlatformSelector.Create);

        outcome.Exit.Should().Be(0);
        outcome.UiStarts.Should().Be(0);
        shown.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the running copy was asked to show its window");
    }

    [Fact]
    public void AUiThatThrows_IsALogLine_AndExit1()
    {
        var folder = TestFolders.New("startup");

        var outcome = Start(["--data-dir", folder], PlatformSelector.Create, _ => throw new InvalidOperationException("the window system is gone"));

        outcome.Exit.Should().Be(1);
        outcome.Log.Should().Contain("The app stopped unexpectedly.").And.Contain("the window system is gone");
    }

    [Fact]
    public void TheGuardIsReleased_EvenWhenTheUiThrows()
    {
        var folder = TestFolders.New("startup");

        Start(["--data-dir", folder], PlatformSelector.Create, _ => throw new InvalidOperationException("boom"));

        using var after = PlatformSelector.Create(folder).TryAcquireInstance();
        after.Should().NotBeNull();
    }

    /// <summary>The real platform, except that taking the one-copy guard fails the way a folder without permission does.</summary>
    private sealed class ThrowingGuardPlatform(IBlindDesktopPlatform inner) : IBlindDesktopPlatform
    {
        public string Name => inner.Name;
        public IBlindPaths Paths => inner.Paths;
        public void AddSeams(IServiceCollection services) => inner.AddSeams(services);
        public IInstanceGuard? TryAcquireInstance() => throw new UnauthorizedAccessException("Access to the path is denied.");
        public bool SignalRunningInstance() => false;
    }
}
