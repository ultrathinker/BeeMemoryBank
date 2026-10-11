using BeeMemoryBank.Vault.Metadata;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The barrier between the operations that author sealed content and the DEK commit of a rotation (docs/design/step-a-spec.md 4.4):
/// authors share it, a rotation holds it alone, and an author that is already inside is never blocked by a rotation that is waiting.
/// Every author runs in a task of its own, started from a context that holds no scope: a request is a flow of its own too.
/// </summary>
public class AuthoringBarrierTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Authors_ShareTheBarrier()
    {
        var barrier = new AuthoringBarrier();
        var first = Author.Start(barrier);
        await first.Inside.WaitAsync(Generous);

        var second = Author.Start(barrier);
        await second.Inside.WaitAsync(Generous); // not held back by the first

        first.Leave();
        second.Leave();
        await Task.WhenAll(first.Done, second.Done).WaitAsync(Generous);
    }

    [Fact]
    public async Task ARotation_WaitsForTheAuthorsInFlight_AndHoldsNewOnesBackUntilItIsDone()
    {
        var barrier = new AuthoringBarrier();
        var inFlight = Author.Start(barrier);
        await inFlight.Inside.WaitAsync(Generous);

        var rotation = barrier.EnterExclusiveAsync();
        await Task.Delay(Settle);
        rotation.IsCompleted.Should().BeFalse("an author is still in flight: the DEK swap must not happen under it");

        var late = Author.Start(barrier);
        await Task.Delay(Settle);
        late.Inside.IsCompleted.Should().BeFalse("an author that starts while the rotation waits queues behind it");

        inFlight.Leave();
        var exclusive = await rotation.WaitAsync(Generous);
        await Task.Delay(Settle);
        late.Inside.IsCompleted.Should().BeFalse("and stays out for as long as the rotation holds the barrier");

        exclusive.Dispose();
        await late.Inside.WaitAsync(Generous);
        late.Leave();
        await late.Done.WaitAsync(Generous);
    }

    [Fact]
    public async Task AnAuthorThatIsInside_IsNotBlockedByAWaitingRotation_WhenItEntersAgain()
    {
        var barrier = new AuthoringBarrier();
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var goOn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A service that calls another service: the second entry happens while a rotation is already waiting for the first.
        var flow = Task.Run(async () =>
        {
            using var outer = await barrier.EnterShared();
            inside.SetResult();
            await goOn.Task;
            using var nested = await barrier.EnterShared();
        });
        await inside.Task.WaitAsync(Generous);
        var rotation = barrier.EnterExclusiveAsync();
        await Task.Delay(Settle);
        rotation.IsCompleted.Should().BeFalse();

        goOn.SetResult();
        await flow.WaitAsync(Generous); // a barrier that did not recognise the flow would wait here for the rotation, which waits for the flow

        (await rotation.WaitAsync(Generous)).Dispose();
    }

    [Fact]
    public async Task ARotation_GivesUpWhenTheWritesInFlightDoNotEnd_AndTheBarrierIsFreeAgain()
    {
        var barrier = new AuthoringBarrier();
        var stuck = Author.Start(barrier);
        await stuck.Inside.WaitAsync(Generous);

        var act = () => barrier.EnterExclusiveAsync(TimeSpan.FromMilliseconds(200));

        await act.Should().ThrowAsync<TimeoutException>();
        var another = Author.Start(barrier);
        await another.Inside.WaitAsync(Generous); // not held back by the rotation that gave up

        another.Leave();
        stuck.Leave();
        (await barrier.EnterExclusiveAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task ACancelledEntry_LeavesNoScopeBehind()
    {
        var barrier = new AuthoringBarrier();
        var exclusive = await barrier.EnterExclusiveAsync();
        using var cancel = new CancellationTokenSource();

        var entering = Task.Run(async () => (await barrier.EnterShared(cancel.Token)).Dispose());
        await Task.Delay(Settle);
        cancel.Cancel();
        var act = () => entering;
        await act.Should().ThrowAsync<OperationCanceledException>();
        exclusive.Dispose();

        // Nothing is counted as inside: a rotation does not wait for the entry that never happened.
        (await barrier.EnterExclusiveAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task ADisposedScope_CanBeDisposedAgain_WithoutReleasingTheHoldOfTheSameFlow()
    {
        var barrier = new AuthoringBarrier();
        var flowIsInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flow = Task.Run(async () =>
        {
            var one = await barrier.EnterShared();
            using var two = await barrier.EnterShared(); // nested: the same flow
            one.Dispose();
            one.Dispose(); // twice must count once
            flowIsInside.SetResult();
            await leave.Task;
        });
        await flowIsInside.Task.WaitAsync(Generous);

        var rotation = barrier.EnterExclusiveAsync();
        await Task.Delay(Settle);
        rotation.IsCompleted.Should().BeFalse("the nested scope is still open");

        leave.SetResult();
        await flow.WaitAsync(Generous);
        (await rotation.WaitAsync(Generous)).Dispose();
    }

    /// <summary>An author in a task of its own: inside the barrier until told to leave.</summary>
    private sealed class Author
    {
        private readonly TaskCompletionSource _inside = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _leave = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Inside => _inside.Task;
        public Task Done { get; private set; } = Task.CompletedTask;

        public void Leave() => _leave.TrySetResult();

        public static Author Start(AuthoringBarrier barrier)
        {
            var author = new Author();
            author.Done = Task.Run(async () =>
            {
                using var scope = await barrier.EnterShared();
                author._inside.SetResult();
                await author._leave.Task;
            });
            return author;
        }
    }
}
