using BeeMemoryBank.Api.Services.BlindBackup;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// When the daily backup is owed (plan §7): once per slot, caught up after downtime within a
/// window, never twice for the same slot, with the per-day jitter moving the slot itself.
/// </summary>
public class BlindBackupScheduleTests
{
    private static readonly Func<DateTime, TimeSpan> NoJitter = _ => TimeSpan.Zero;
    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 9, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public void TodaysSlotHasPassed_NoBackupSince_IsDue()
    {
        BlindBackupScheduleService.DueSlot("03:00", At(27, 3, 1), lastBackupStartedAt: At(26, 3, 0), NoJitter)
            .Should().Be(At(27, 3, 0));
    }

    [Fact]
    public void BackupStartedAfterTheSlot_IsNotDueAgain()
    {
        BlindBackupScheduleService.DueSlot("03:00", At(27, 9, 0), lastBackupStartedAt: At(27, 3, 0).AddSeconds(30), NoJitter)
            .Should().BeNull("the slot was consumed by the backup that started at 03:00:30");
    }

    [Fact]
    public void BeforeTodaysSlot_YesterdaysRan_IsNotDue()
    {
        BlindBackupScheduleService.DueSlot("03:00", At(27, 2, 0), lastBackupStartedAt: At(26, 3, 0), NoJitter)
            .Should().BeNull();
    }

    [Fact]
    public void MissedLateEveningSlot_IsCaughtUpAfterMidnight()
    {
        BlindBackupScheduleService.DueSlot("22:00", At(27, 2, 0), lastBackupStartedAt: At(25, 22, 0), NoJitter)
            .Should().Be(At(26, 22, 0), "the node was down at 22:00 yesterday; four hours late is within the window");
    }

    [Fact]
    public void SlotOlderThanTheCatchUpWindow_IsSkipped()
    {
        BlindBackupScheduleService.DueSlot("03:00", At(27, 16, 0), lastBackupStartedAt: null, NoJitter)
            .Should().BeNull("enabling the schedule in the afternoon must not start a backup on the spot");
    }

    [Fact]
    public void Jitter_MovesTheSlot()
    {
        Func<DateTime, TimeSpan> plusTen = _ => TimeSpan.FromMinutes(10);
        BlindBackupScheduleService.DueSlot("03:00", At(27, 3, 5), At(26, 3, 10), plusTen)
            .Should().BeNull("with +10 min of jitter today's slot is 03:10, still ahead");
        BlindBackupScheduleService.DueSlot("03:00", At(27, 3, 11), At(26, 3, 10), plusTen)
            .Should().Be(At(27, 3, 10));
    }
}

/// <summary>What the job manager keeps across a restart: history (for the schedule) and the CPU mode.</summary>
public class BlindJobManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_blind_jobs_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private BlindJobManager NewManager() => new(_dir, NullLogger<BlindJobManager>.Instance);

    [Fact]
    public async Task LastStartedAt_SurvivesARestart()
    {
        var first = NewManager();
        var job = first.TryStart("backup", (_, _) => Task.CompletedTask)!;
        for (var i = 0; i < 100 && first.IsBusy; i++) await Task.Delay(20);

        NewManager().LastStartedAt("backup").Should().Be(job.StartedAt,
            "the schedule reads this after a restart to know the slot already ran");
        NewManager().LastStartedAt("verify").Should().BeNull();
    }

    [Fact]
    public void Pause_SurvivesARestart()
    {
        NewManager().SetMode(BlindCpuMode.Pause);

        var restarted = NewManager();
        restarted.Mode.Should().Be(BlindCpuMode.Pause, "an operator's pause must not silently lift on a container restart");
        restarted.WaitForResumeAsync(CancellationToken.None).IsCompleted.Should().BeFalse(
            "a restored pause holds the node's own phases too, not only the reported mode");
    }

    /// <summary>Holds the worker at its failure log line — between "State = Failed" and the slot being cleared.</summary>
    private sealed class GateOnErrorLogger : Microsoft.Extensions.Logging.ILogger<BlindJobManager>
    {
        public readonly TaskCompletionSource AtBoundary = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (level != Microsoft.Extensions.Logging.LogLevel.Error) return;
            AtBoundary.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task CompletionBoundary_AFinishingJobStillHoldsTheSlot()
    {
        var logger = new GateOnErrorLogger();
        var m = new BlindJobManager(_dir, logger);
        var first = m.TryStart("backup", (_, _) => throw new InvalidOperationException("restic failed"))!;
        await logger.AtBoundary.Task.WaitAsync(TimeSpan.FromSeconds(10)); // State is Failed, the slot not yet cleared

        var second = m.TryStart("backup", (_, ct) => Task.Delay(Timeout.Infinite, ct));
        logger.Release.Set();
        await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        second.Should().BeNull("a job is in the slot until its finally clears it — a second one would overlap it");
        m.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Wipe_WaitsForASnapshotListingThatGotInFirst_AndNoneGetsInAfter()
    {
        var m = NewManager();
        var listing = m.TryReserveRead();
        listing.Should().NotBeNull("an idle manager lets a listing in");

        // The listing holds its reservation and is about to spawn restic; the wipe starts now.
        var wipe = m.BeginWipeAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        wipe.IsCompleted.Should().BeFalse("the wipe must not delete while a listing may still start restic");
        m.TryReserveRead().Should().BeNull("with the gate up no new listing gets in");

        listing!.Dispose();
        (await wipe).Should().BeTrue();
        m.EndWipe();
    }

    [Fact]
    public async Task Wipe_FailsWhileAJobOutlivesItsCancellation_AndReopensTheManager()
    {
        var m = NewManager();
        var stuck = new TaskCompletionSource();
        m.TryStart("backup", (_, _) => stuck.Task).Should().NotBeNull(); // ignores its token

        var drained = await m.BeginWipeAsync(TimeSpan.FromMilliseconds(300));

        drained.Should().BeFalse("the wipe must never report success while a job is alive");
        m.IsWiping.Should().BeFalse("a refused wipe leaves the node usable");
        stuck.SetResult();
    }

    [Fact]
    public async Task Wipe_ClosesTheManager_BeforeCancelling_AndReleasesAPause()
    {
        var m = NewManager();
        m.SetMode(BlindCpuMode.Pause);
        m.TryStart("backup", async (ctx, ct) =>
        {
            await ctx.WaitForResumeAsync(ct);
            await Task.Delay(Timeout.Infinite, ct);
        }).Should().NotBeNull();

        (await m.BeginWipeAsync(TimeSpan.FromSeconds(10))).Should().BeTrue("a paused job is released and cancelled");
        // The window the review found: the old job is gone, the wipe has not started deleting —
        // the schedule's next tick must not get a job in.
        m.TryStart("backup", (_, _) => Task.CompletedTask).Should().BeNull("the gate stays up until the wipe ends");
        m.SetMode(BlindCpuMode.Fast).Should().BeFalse("no mode change may resume or stop anything mid-wipe");

        m.EndWipe();
        m.TryStart("verify", (_, _) => Task.CompletedTask).Should().NotBeNull();
    }

    [Fact]
    public async Task Pause_StopsEveryLiveChild_WhenAnotherInvocationEnded()
    {
        if (OperatingSystem.IsWindows()) return; // SIGSTOP/SIGCONT are Linux mechanics (the container)
        using var backup = StartSleep();
        using var list = StartSleep();
        var m = NewManager();
        using var backupReg = m.RegisterChild(backup);
        m.RegisterChild(list).Dispose(); // a short invocation ends while the backup still runs

        m.SetMode(BlindCpuMode.Pause);
        try
        {
            await WaitForStateAsync(backup, 'T');
            ProcState(backup).Should().Be('T', "the backup's restic must stop even though another child came and went");
        }
        finally
        {
            m.SetMode(BlindCpuMode.Economy);
            backup.Kill(); list.Kill();
            await backup.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await list.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static System.Diagnostics.Process StartSleep() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sleep", "30"))!;

    // Third field of /proc/<pid>/stat: R, S, T (stopped), …
    private static char ProcState(System.Diagnostics.Process p) =>
        File.ReadAllText($"/proc/{p.Id}/stat").Split(") ")[1][0];

    private static async Task WaitForStateAsync(System.Diagnostics.Process p, char state)
    {
        for (var i = 0; i < 50 && ProcState(p) != state; i++) await Task.Delay(20);
    }
}
