using BeeMemoryBank.Api.Services;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The restore DB-file swap has to survive the transient Windows lock a pooled background reader
/// holds on the live database: Microsoft.Data.Sqlite keeps physical handles open in the pool after
/// Dispose, and File.Move over an open file throws UnauthorizedAccessException. The production fix
/// is SnapshotService.SwapDbFileWithRetryAsync — clear-pool-then-swap as one gesture, retried with
/// backoff. Timing-based reproduction is inherently flaky, so these tests pin the retry LOGIC
/// through its seams instead: fake swap/clearPools/delay, no real files, no real clocks.
/// </summary>
public class SnapshotFileSwapRetryTests
{
    private static Func<int, Task> NoDelay => _ => Task.CompletedTask;

    [Fact]
    public async Task ASwapBlockedAFewTimes_Succeeds_AndClearsThePoolBeforeEveryAttempt()
    {
        var swapCalls = 0;
        var clearCalls = 0;

        await SnapshotService.SwapDbFileWithRetryAsync(
            swap: () => { if (++swapCalls < 4) throw new UnauthorizedAccessException("Access to the path is denied."); },
            clearPools: () => clearCalls++,
            delay: NoDelay);

        swapCalls.Should().Be(4, "three blocked attempts, then the freed window");
        // The pool must be cleared immediately before EVERY attempt — clearing once and waiting
        // (the old code) hands the file back to whoever reopens a connection during the wait.
        clearCalls.Should().Be(4);
    }

    [Fact]
    public async Task IOExceptionIsRetriedToo()
    {
        var swapCalls = 0;

        await SnapshotService.SwapDbFileWithRetryAsync(
            swap: () => { if (++swapCalls < 2) throw new IOException("being used by another process"); },
            clearPools: () => { },
            delay: NoDelay);

        swapCalls.Should().Be(2);
    }

    [Fact]
    public async Task APermanentlyLockedFile_StillFails_AfterMaxAttempts_WithTheOriginalException()
    {
        var swapCalls = 0;

        var act = () => SnapshotService.SwapDbFileWithRetryAsync(
            swap: () => { swapCalls++; throw new UnauthorizedAccessException("Access to the path is denied."); },
            clearPools: () => { },
            maxAttempts: 5,
            delay: NoDelay);

        // The retry is a bounded tolerance for a momentary lock, not an infinite loop hiding a
        // genuinely wedged file — the caller's 500-with-log path still exists for that.
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        swapCalls.Should().Be(5);
    }

    [Fact]
    public async Task AnUnexpectedExceptionType_IsNotRetried()
    {
        var swapCalls = 0;

        var act = () => SnapshotService.SwapDbFileWithRetryAsync(
            swap: () => { swapCalls++; throw new InvalidOperationException("not a file lock"); },
            clearPools: () => { },
            delay: NoDelay);

        // Only the two transient file-lock shapes are retried; anything else is a real error and
        // must surface on the first attempt, not after ten blind retries against a broken restore.
        await act.Should().ThrowAsync<InvalidOperationException>();
        swapCalls.Should().Be(1);
    }
}
