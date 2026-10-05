using System;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Node;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Node.Tests;

public class NodeStopCoordinatorTests
{
    [Fact]
    public async Task RequestStopAsync_ConcurrentTriggers_StopExactlyOnceAndCompleteTheWait()
    {
        var frontStops = 0;
        var orchestratorStops = 0;
        var completion = new TaskCompletionSource<int>();
        var coordinator = new NodeStopCoordinator(
            () =>
            {
                Interlocked.Increment(ref frontStops);
                return Task.CompletedTask;
            },
            () =>
            {
                Interlocked.Increment(ref orchestratorStops);
                return Task.CompletedTask;
            },
            completion);

        await Task.WhenAll(
            Task.Run(() => coordinator.RequestStopAsync()),
            Task.Run(() => coordinator.RequestStopAsync()));

        frontStops.Should().Be(1);
        orchestratorStops.Should().Be(1);
        (await completion.Task).Should().Be(0);
    }
}
