using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-311 fix2 (R1): a network restore answers 202 and goes on in the background. That work is TRACKED (one task the service knows), linked to the token of the
/// application's stopping so that a node that shuts down cancels it before it replaces anything, ends in a terminal state when it is cancelled, and gives the
/// lease of the route back when - and only when - the work is over (no timer).
/// </summary>
public sealed class RestoreBackgroundTrackingTests
{
    private sealed class Lease : IDisposable
    {
        public int Disposed;
        public void Dispose() => Interlocked.Increment(ref Disposed);
    }

    private static RestoreInitiatorService Service() =>
        new(null!, null!, null!, null!, null!, null!, NullLogger<RestoreInitiatorService>.Instance, Path.GetTempPath());

    [Fact]
    public async Task TheBackgroundWork_IsTracked_HoldsTheLeaseWhileItRuns_AndGivesItBackWhenItIsDone()
    {
        var service = Service();
        var release = new TaskCompletionSource();
        var lease = new Lease();

        var task = service.StartBackground(async _ => await release.Task, CancellationToken.None, lease);

        service.BackgroundTask.Should().BeSameAs(task);
        await Task.Delay(300);
        lease.Disposed.Should().Be(0, "the lease lasts as long as the work does: no timer gives it back");
        release.SetResult();
        await task.WaitAsync(TimeSpan.FromSeconds(10));
        lease.Disposed.Should().Be(1);
        service.GetProgress().CurrentStep.Should().Be(RestoreFlowStep.Idle, "a finished run of a stub changes no step: the work itself sets those");
    }

    [Fact]
    public async Task TheApplicationStopping_CancelsTheWork_WhichEndsInTheTerminalCancelledStep_AndGivesTheLeaseBackOnce()
    {
        var service = Service();
        using var stopping = new CancellationTokenSource();
        var entered = new TaskCompletionSource();
        var lease = new Lease();
        CancellationToken seen = default;

        var task = service.StartBackground(async ct =>
        {
            seen = ct;
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, stopping.Token, lease);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        seen.CanBeCanceled.Should().BeTrue("the work is handed a token that the node's stopping reaches");
        lease.Disposed.Should().Be(0);

        stopping.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(10));

        lease.Disposed.Should().Be(1, "cancelled is a way for the work to be over: the lease goes back, once");
        service.GetProgress().CurrentStep.Should().Be(RestoreFlowStep.Cancelled);
        service.GetProgress().StatusMessage.Should().Contain("Nothing was replaced");
    }

    [Fact]
    public async Task AFailureOfTheWork_EndsItToo_AndGivesTheLeaseBack_WithoutTakingTheHostDown()
    {
        var service = Service();
        var lease = new Lease();

        var task = service.StartBackground(_ => throw new InvalidOperationException("the seeder is gone"), CancellationToken.None, lease);

        await task.WaitAsync(TimeSpan.FromSeconds(10));   // the task itself does not fault: it is logged where it ended
        lease.Disposed.Should().Be(1);
        service.GetProgress().CurrentStep.Should().NotBe(RestoreFlowStep.Cancelled);
    }

    [Fact]
    public void ACancelledRestoreIsATerminalStateOfTheStatusRoute_NotInProgressForEver()
    {
        // GET /api/snapshots/restore/status buckets the step for a caller that is not authenticated: "cancelled" is its own word, not "in_progress".
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "server", "BeeMemoryBank.Api", "Endpoints", "SnapshotEndpoints.cs"));

        source.Should().Contain("RestoreFlowStep.Cancelled => \"cancelled\"");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
