using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Node;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Node.Tests;

/// <summary>
/// A stop that arrives while the front is still starting must never run <c>StopAsync</c> on the host
/// under its own <c>StartAsync</c> (Kestrel's heartbeat thread then throws on a foreign thread and the
/// whole process aborts, exit 134). These tests pin the rules of <see cref="FrontStartStopGate{TApp}"/>
/// with a fake front whose start takes a while.
/// </summary>
public class FrontStartStopGateTests
{
    private static readonly TimeSpan LongBound = TimeSpan.FromSeconds(30);

    private sealed class FakeFront : IAsyncDisposable
    {
        private readonly ConcurrentQueue<string>? _events;
        public int Disposed;

        public FakeFront(ConcurrentQueue<string>? events = null) => _events = events;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposed);
            _events?.Enqueue("dispose");
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task StopRequestedDuringStart_WaitsForTheStart_ThenStopsOnce_NeverOverlapping()
    {
        var events = new ConcurrentQueue<string>();
        var startRunning = 0;
        var overlaps = 0;
        var stops = 0;

        var gate = new FrontStartStopGate<FakeFront>(
            async _ =>
            {
                events.Enqueue("stop-begin");
                Interlocked.Increment(ref stops);
                if (Volatile.Read(ref startRunning) != 0) Interlocked.Increment(ref overlaps);
                await Task.Delay(30);
                events.Enqueue("stop-end");
            },
            LongBound);

        var startTask = gate.StartAsync(
            () => { events.Enqueue("build"); return new FakeFront(events); },
            async _ =>
            {
                Volatile.Write(ref startRunning, 1);
                events.Enqueue("start-begin");
                await Task.Delay(300);
                events.Enqueue("start-end");
                Volatile.Write(ref startRunning, 0);
            });

        await Task.Delay(50);                         // the start is well under way
        var stopTask = gate.StopAsync();
        await Task.WhenAll(startTask, stopTask);

        (await startTask).Should().NotBeNull("the start was let finish");
        events.Should().Equal("build", "start-begin", "start-end", "stop-begin", "stop-end");
        stops.Should().Be(1);
        overlaps.Should().Be(0);
    }

    [Fact]
    public async Task StopRequestedBeforeAnyStart_PreventsTheStart()
    {
        var stops = 0;
        var builds = 0;
        var starts = 0;
        var gate = new FrontStartStopGate<FakeFront>(_ => { Interlocked.Increment(ref stops); return Task.CompletedTask; }, LongBound);

        await gate.StopAsync();                       // nothing built yet: there is nothing to stop
        gate.StopRequested.Should().BeTrue();

        var started = await gate.StartAsync(
            () => { builds++; return new FakeFront(); },
            _ => { starts++; return Task.CompletedTask; });

        started.Should().BeNull("a start that begins after the stop was requested must not run");
        builds.Should().Be(0);
        starts.Should().Be(0);
        stops.Should().Be(0);
    }

    [Fact]
    public async Task StartThatThrows_IsSwallowedByTheStop_AndTheFailedFrontIsNotStopped()
    {
        var stops = 0;
        var failed = new FakeFront();
        var gate = new FrontStartStopGate<FakeFront>(_ => { Interlocked.Increment(ref stops); return Task.CompletedTask; }, LongBound);

        var startTask = gate.StartAsync(
            () => failed,
            async _ =>
            {
                await Task.Delay(150);
                throw new IOException("port taken");
            });

        await Task.Delay(20);
        var stopTask = gate.StopAsync();

        await stopTask;                               // must not throw: the start's exception is the start path's business
        failed.Disposed.Should().Be(1, "the gate disposes a front whose start failed before the stop goes on");
        stops.Should().Be(0, "a front that never started is not stopped");

        var act = async () => await startTask;
        await act.Should().ThrowAsync<IOException>("the start path still sees its own exception");
    }

    [Fact]
    public async Task StartRetryAfterAFailedStart_IsSkippedOnceTheStopWasRequested()
    {
        var builds = 0;
        var gate = new FrontStartStopGate<FakeFront>(_ => Task.CompletedTask, LongBound);

        var first = gate.StartAsync(
            () => { builds++; return new FakeFront(); },
            async _ =>
            {
                await Task.Delay(100);
                throw new IOException("port taken");
            });

        await Task.Delay(20);
        var stopTask = gate.StopAsync();
        var act = async () => await first;
        await act.Should().ThrowAsync<IOException>();
        await stopTask;

        var retry = await gate.StartAsync(() => { builds++; return new FakeFront(); }, _ => Task.CompletedTask);

        retry.Should().BeNull("the port-retry of the start path must not start a front after the stop was requested");
        builds.Should().Be(1);
    }

    [Fact]
    public async Task StartThatNeverFinishes_IsAbandonedAfterTheBound_WithoutStoppingTheFrontUnderIt()
    {
        var stops = 0;
        var logs = new ConcurrentQueue<string>();
        var neverEnds = new TaskCompletionSource();
        var gate = new FrontStartStopGate<FakeFront>(
            _ => { Interlocked.Increment(ref stops); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(250),
            logs.Enqueue);

        var startTask = gate.StartAsync(() => new FakeFront(), _ => neverEnds.Task);
        await Task.Delay(30);

        var sw = Stopwatch.StartNew();
        await gate.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));
        sw.Stop();

        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(200), "the stop waits for the start up to the bound");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "and then gives up");
        stops.Should().Be(0, "the front is not stopped while its start is still running");
        logs.Should().ContainSingle().Which.Should().Contain("still starting");

        (await startTask.WaitAsync(TimeSpan.FromSeconds(20))).Should().BeNull("the abandoned start call returns so the caller's run wait can complete");

        neverEnds.SetResult();                        // let the abandoned start end; nothing stops the front afterwards
        await Task.Delay(50);
        stops.Should().Be(0);
    }

    [Fact]
    public async Task StopAfterTheStartFinished_StopsTheFrontExactlyOnce_EvenWhenAskedTwice()
    {
        var stops = 0;
        var gate = new FrontStartStopGate<FakeFront>(_ => { Interlocked.Increment(ref stops); return Task.CompletedTask; }, LongBound);

        var started = await gate.StartAsync(() => new FakeFront(), _ => Task.CompletedTask);
        started.Should().NotBeNull();

        await Task.WhenAll(
            Task.Run(() => gate.StopAsync()),
            Task.Run(() => gate.StopAsync()));

        stops.Should().Be(1);
    }

    [Fact]
    public async Task ThroughTheCoordinator_TheFrontStopWaitsForTheStart_ThenTheOrchestratorStops()
    {
        var events = new ConcurrentQueue<string>();
        var completion = new TaskCompletionSource<int>();
        var gate = new FrontStartStopGate<FakeFront>(
            _ => { events.Enqueue("front-stop"); return Task.CompletedTask; },
            LongBound);
        var coordinator = new NodeStopCoordinator(
            () => gate.StopAsync(),
            () => { events.Enqueue("orchestrator-stop"); return Task.CompletedTask; },
            completion);

        var startTask = gate.StartAsync(
            () => new FakeFront(),
            async _ =>
            {
                events.Enqueue("start-begin");
                await Task.Delay(300);
                events.Enqueue("start-end");
            });

        await Task.Delay(50);
        await coordinator.RequestStopAsync(2);        // e.g. a critical failure while the front starts
        await startTask;

        events.Should().Equal("start-begin", "start-end", "front-stop", "orchestrator-stop");
        (await completion.Task).Should().Be(2, "the exit code of the first stop request is kept");
    }
}
