using BeeMemoryBank.BlindIos.Services;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>
/// A loop and an account set that behave like the real ones where it matters: a stop keeps the loop "running" until it has really ended, a start while the loop is still ending is ignored
/// (the scheduler never starts a second loop beside one that is draining), and any two operations of the loop, or an operation and a background round, that overlap are written down.
/// </summary>
internal sealed class FakeLoop : IIosLoop
{
    private readonly object _gate = new();
    private readonly List<string> _events = [];
    private int _active;

    public bool Running { get; private set; }

    /// <summary>Two operations (an accounts start, a loop start, a loop stop, a background round) were inside the loop at the same moment.</summary>
    public bool Overlapped { get; private set; }

    /// <summary>The loop was started, or was found running, while a background round was on.</summary>
    public bool LoopBesideARound { get; private set; }

    public bool RoundActive { get; private set; }
    public Func<Task>? OnStartAccounts { get; set; }
    public Func<Task>? OnStop { get; set; }

    public IReadOnlyList<string> Events
    {
        get { lock (_gate) return [.. _events]; }
    }

    public int Count(string name)
    {
        lock (_gate) return _events.Count(e => e == name);
    }

    public bool LoopRunning => Running;

    private static async Task YieldAsync() => await Task.Yield();

    public async Task StartAccountsAsync(CancellationToken ct)
    {
        Begin("accounts-begin");
        try { await (OnStartAccounts?.Invoke() ?? YieldAsync()); }
        finally { End("accounts-end"); }
    }

    public void EnsureLoop()
    {
        Begin("start");
        if (Running) Note("start-ignored");
        else Running = true;
        if (RoundActive) LoopBesideARound = true;
        End(null);
    }

    public async Task StopLoopAsync()
    {
        Begin("stop-begin");
        try
        {
            await (OnStop?.Invoke() ?? YieldAsync());
            Running = false;
        }
        finally { End("stop-end"); }
    }

    /// <summary>The round the lifecycle runs: it writes its begin and its end, holds until <paramref name="body"/> ends (or the lifecycle cancels it) and says so in its outcome.</summary>
    public Func<CancellationToken, Task<IosRoundOutcome>> Round(Func<CancellationToken, Task>? body = null) => async token =>
    {
        Begin("round-begin");
        lock (_gate)
        {
            RoundActive = true;
            if (Running) LoopBesideARound = true;
        }
        try
        {
            await (body?.Invoke(token) ?? YieldAsync());
        }
        catch (OperationCanceledException)
        {
            // the lifecycle asked the round to stop
        }
        finally
        {
            lock (_gate) RoundActive = false;
            End("round-end");
        }
        return new IosRoundOutcome([], Note: null, EndedByIos: token.IsCancellationRequested, NotReached: 0);
    };

    private void Begin(string name)
    {
        lock (_gate)
        {
            if (_active > 0 || RoundActive) Overlapped = true;
            _active++;
            _events.Add(name);
        }
    }

    private void End(string? name)
    {
        lock (_gate)
        {
            _active--;
            if (name is not null) _events.Add(name);
        }
    }

    private void Note(string name)
    {
        lock (_gate) _events.Add(name);
    }
}

/// <summary>
/// Fix round 2, item 1 (review revfixios, MAJOR F1): the foreground transition, the stop, the background round and the disposal are ONE ordered queue. The sleep/resume callbacks of the app
/// are one-line calls into it, so the orderings that went wrong - a stale stop finishing after a newer start, a start asked for while a round holds the turn, a disposal in the middle of a
/// transition - are driven here with a loop and a round that wait for the test.
/// </summary>
public sealed class IosLifecycleTests
{
    private static async Task<T> Within<T>(Task<T> task, int seconds = 30) => await task.WaitAsync(TimeSpan.FromSeconds(seconds));

    private static Task Within(Task task, int seconds = 30) => task.WaitAsync(TimeSpan.FromSeconds(seconds));

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Reached(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the state the test waits for was not reached");
            await Task.Delay(2);
        }
    }

    // ---- both orderings of sleep and resume ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SleepThenARapidResume_EndsWithTheLoopRunning_AndTheStaleStopDoesNotStopTheNewLoop()
    {
        var loop = new FakeLoop();
        await using var lifecycle = new IosLifecycle(loop);
        await Within(lifecycle.EnterForegroundAsync());
        loop.Running.Should().BeTrue();
        var stopGate = Gate();
        loop.OnStop = () => stopGate.Task;

        var sleep = lifecycle.EnterBackgroundAsync();
        await Reached(() => loop.Count("stop-begin") == 1);
        var resume = lifecycle.EnterForegroundAsync();

        lifecycle.InForeground.Should().BeTrue("the flag follows the order the callbacks came in, at once");
        await Task.Delay(100);
        loop.Count("start").Should().Be(1, "the newer start waits for the older stop; it has not touched the loop yet");
        loop.Count("accounts-begin").Should().Be(1, "not even the accounts are started beside the stop that is still on");
        stopGate.SetResult();
        await Within(Task.WhenAll(sleep, resume));

        loop.Running.Should().BeTrue("foreground = the loop runs");
        lifecycle.InForeground.Should().BeTrue();
        loop.Overlapped.Should().BeFalse();
        loop.Events.Should().Equal("accounts-begin", "accounts-end", "start", "stop-begin", "stop-end", "accounts-begin", "accounts-end", "start");
    }

    [Fact]
    public async Task ResumeThenARapidSleep_EndsWithTheLoopStopped_AndTheLateStartDoesNotStartIt()
    {
        var loop = new FakeLoop();
        await using var lifecycle = new IosLifecycle(loop);
        var accountsGate = Gate();
        loop.OnStartAccounts = () => accountsGate.Task;

        var resume = lifecycle.EnterForegroundAsync();
        await Reached(() => loop.Count("accounts-begin") == 1);
        var sleep = lifecycle.EnterBackgroundAsync();

        lifecycle.InForeground.Should().BeFalse("the person has gone to the background, whatever the start is still doing");
        await Task.Delay(100);
        loop.Count("stop-begin").Should().Be(0, "the stop waits for the start that is in flight");
        accountsGate.SetResult();
        await Within(Task.WhenAll(resume, sleep));

        loop.Running.Should().BeFalse("background = the loop is stopped");
        loop.Count("start").Should().Be(0, "the start that was overtaken saw that the app had left and did not start a loop");
        lifecycle.InForeground.Should().BeFalse();
        loop.Overlapped.Should().BeFalse();
    }

    [Fact]
    public async Task EveryOrderOfSleepResumeAndAWake_AtAnySpeed_EndsInTheLastWish_WithNoTwoOperationsInsideTheLoopAtOnce()
    {
        // 'R' = OnResume, 'S' = OnSleep, 'B' = a background wake; every sequence of one to five of them, each step taking a different number of turns of the scheduler.
        var sequences = new List<string> { "" };
        for (var length = 1; length <= 5; length++)
            sequences.AddRange(sequences.Where(s => s.Length == length - 1).SelectMany(s => "RSB".Select(c => s + c)).ToList());

        foreach (var sequence in sequences.Where(s => s.Length > 0))
        {
            var rng = new Random(sequence.Aggregate(17, (h, c) => h * 31 + c));
            int Next(int below)
            {
                lock (rng) return rng.Next(0, below);
            }

            var loop = new FakeLoop
            {
                OnStartAccounts = () => Turns(Next(4)),
                OnStop = () => Turns(Next(4)),
            };
            await using var lifecycle = new IosLifecycle(loop);
            var pending = new List<Task>();
            foreach (var step in sequence)
            {
                pending.Add(step switch
                {
                    'R' => lifecycle.EnterForegroundAsync(),
                    'S' => lifecycle.EnterBackgroundAsync(),
                    _ => lifecycle.RunBackgroundAsync(loop.Round(_ => Turns(Next(4))), CancellationToken.None),
                });
                await Turns(Next(3));
            }

            await Within(Task.WhenAll(pending));

            var wished = sequence.LastOrDefault(c => c != 'B') == 'R';
            lifecycle.InForeground.Should().Be(wished, "sequence " + sequence);
            loop.Running.Should().Be(wished, "sequence " + sequence + ": foreground = the loop runs, background = it is stopped");
            loop.Overlapped.Should().BeFalse("sequence " + sequence + ": " + string.Join(" ", loop.Events));
            loop.LoopBesideARound.Should().BeFalse("sequence " + sequence);
        }
    }

    private static async Task Turns(int count)
    {
        for (var i = 0; i < count; i++) await Task.Yield();
    }

    // ---- a callback that already holds the background turn --------------------------------------------------------------------------------------

    [Fact]
    public async Task ARoundThatIsOn_IsAskedToStopByResume_AndTheLoopStartsOnlyAfterTheRoundHasEnded()
    {
        var loop = new FakeLoop();
        await using var lifecycle = new IosLifecycle(loop);
        var begun = Gate();
        var wake = lifecycle.RunBackgroundAsync(loop.Round(async token => { begun.SetResult(); await Task.Delay(Timeout.Infinite, token); }), CancellationToken.None);
        await Within(begun.Task);

        var resume = lifecycle.EnterForegroundAsync();
        await Within(resume);

        (await Within(wake)).EndedByIos.Should().BeTrue("the round was asked to stop at once");
        loop.Events.Should().Equal("round-begin", "round-end", "accounts-begin", "accounts-end", "start");
        loop.LoopBesideARound.Should().BeFalse();
        loop.Running.Should().BeTrue();
        (await Within(lifecycle.RunBackgroundAsync(loop.Round(), CancellationToken.None))).Skipped.Should().Contain("app is open");
    }

    [Fact]
    public async Task ARoundThatIgnoresTheStopForAWhile_KeepsTheLoopWaiting_AndNeverRunsBesideIt()
    {
        var loop = new FakeLoop();
        await using var lifecycle = new IosLifecycle(loop);
        var begun = Gate();
        var letGo = Gate();
        var wake = lifecycle.RunBackgroundAsync(loop.Round(async _ => { begun.SetResult(); await letGo.Task; }), CancellationToken.None);
        await Within(begun.Task);

        var resume = lifecycle.EnterForegroundAsync();
        await Task.Delay(100);

        resume.IsCompleted.Should().BeFalse("the loop waits for the round");
        loop.Count("start").Should().Be(0);
        lifecycle.InForeground.Should().BeTrue();
        letGo.SetResult();
        await Within(Task.WhenAll(wake, resume));
        loop.Running.Should().BeTrue();
        loop.LoopBesideARound.Should().BeFalse();
        loop.Overlapped.Should().BeFalse();
    }

    [Fact]
    public async Task AWakeQueuedBehindAStop_AndOvertakenByAResume_DoesNothing()
    {
        var loop = new FakeLoop();
        await using var lifecycle = new IosLifecycle(loop);
        await Within(lifecycle.EnterForegroundAsync());
        var stopGate = Gate();
        loop.OnStop = () => stopGate.Task;
        var sleep = lifecycle.EnterBackgroundAsync();
        await Reached(() => loop.Count("stop-begin") == 1);
        var wake = lifecycle.RunBackgroundAsync(loop.Round(), CancellationToken.None);
        var resume = lifecycle.EnterForegroundAsync();

        stopGate.SetResult();
        await Within(Task.WhenAll(sleep, wake, resume));

        wake.Result.Skipped.Should().Contain("app is open");
        loop.Count("round-begin").Should().Be(0, "the round never began: the app was in front again before its turn");
        loop.Running.Should().BeTrue();
    }

    [Fact]
    public async Task AWakeWhileAnotherIsQueuedOrOn_IsSkipped_AndALaterOneRunsOnceTheFirstHasEnded()
    {
        var loop = new FakeLoop();
        await using var lifecycle = new IosLifecycle(loop);
        var begun = Gate();
        var letGo = Gate();
        var first = lifecycle.RunBackgroundAsync(loop.Round(async _ => { begun.SetResult(); await letGo.Task; }), CancellationToken.None);
        await Within(begun.Task);

        var second = await Within(lifecycle.RunBackgroundAsync(loop.Round(), CancellationToken.None), 5);
        var third = await Within(lifecycle.RunBackgroundAsync(loop.Round(), CancellationToken.None), 5);

        second.Skipped.Should().Contain("Another background round");
        third.Skipped.Should().Contain("Another background round");
        letGo.SetResult();
        (await Within(first)).Skipped.Should().BeNull();
        (await Within(lifecycle.RunBackgroundAsync(loop.Round(), CancellationToken.None))).Skipped.Should().BeNull();
        loop.Count("round-begin").Should().Be(2);
        loop.Overlapped.Should().BeFalse();
    }

    [Fact]
    public async Task AWakeThatIosEndedBeforeItBegan_IsACancellation_NotASkipOrARound()
    {
        var loop = new FakeLoop();
        await using var lifecycle = new IosLifecycle(loop);
        await Within(lifecycle.EnterForegroundAsync());
        var stopGate = Gate();
        loop.OnStop = () => stopGate.Task;
        var sleep = lifecycle.EnterBackgroundAsync();
        await Reached(() => loop.Count("stop-begin") == 1);
        using var expired = new CancellationTokenSource();
        var wake = lifecycle.RunBackgroundAsync(loop.Round(), expired.Token);

        expired.Cancel();
        stopGate.SetResult();
        await Within(sleep);

        await FluentActions.Awaiting(() => wake).Should().ThrowAsync<OperationCanceledException>();
        loop.Count("round-begin").Should().Be(0);
        (await Within(lifecycle.RunBackgroundAsync(loop.Round(), CancellationToken.None))).Skipped.Should().BeNull("the next wake may run: the failed one left no turn behind");
    }

    // ---- disposal ------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DisposalInTheMiddleOfATransition_WaitsForIt_StopsTheLoop_AndNothingStartsAfterwards()
    {
        var loop = new FakeLoop();
        var lifecycle = new IosLifecycle(loop);
        await Within(lifecycle.EnterForegroundAsync());
        var stopGate = Gate();
        loop.OnStop = () => stopGate.Task;
        var sleep = lifecycle.EnterBackgroundAsync();
        await Reached(() => loop.Count("stop-begin") == 1);
        var resume = lifecycle.EnterForegroundAsync();

        var disposing = lifecycle.DisposeAsync().AsTask();
        await Task.Delay(50);
        disposing.IsCompleted.Should().BeFalse("it waits for the transition in flight");
        stopGate.SetResult();
        await Within(Task.WhenAll(sleep, resume, disposing));

        loop.Running.Should().BeFalse("the process is ending: the loop is stopped, not restarted by the resume that was queued before");
        loop.Count("start").Should().Be(1, "only the first start");
        loop.Overlapped.Should().BeFalse();
        var eventsAtEnd = loop.Events.Count;

        await Within(lifecycle.EnterForegroundAsync());
        (await Within(lifecycle.RunBackgroundAsync(loop.Round(), CancellationToken.None))).Skipped.Should().Contain("closing");
        await Within(lifecycle.DisposeAsync().AsTask());
        loop.Events.Should().HaveCount(eventsAtEnd, "after disposal nothing is asked of the loop any more");
        lifecycle.InForeground.Should().BeFalse();
    }

    [Fact]
    public async Task DisposalWhileARoundIsOn_CancelsIt_WaitsForItsEnd_AndStopsTheLoopLast()
    {
        var loop = new FakeLoop();
        var lifecycle = new IosLifecycle(loop);
        var begun = Gate();
        var wake = lifecycle.RunBackgroundAsync(loop.Round(async token => { begun.SetResult(); await Task.Delay(Timeout.Infinite, token); }), CancellationToken.None);
        await Within(begun.Task);

        await Within(lifecycle.DisposeAsync().AsTask());

        (await Within(wake)).EndedByIos.Should().BeTrue();
        loop.Events.Should().Equal("round-begin", "round-end", "stop-begin", "stop-end");
        loop.Running.Should().BeFalse();
    }

    // ---- failures and waiting ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATransitionThatFails_TellsItsOwnCaller_AndTheNextOneStillRuns()
    {
        var loop = new FakeLoop();
        await using var lifecycle = new IosLifecycle(loop);
        var calls = 0;
        loop.OnStartAccounts = () => ++calls == 1 ? Task.FromException(new IOException("the folder is not there yet")) : Task.CompletedTask;

        await FluentActions.Awaiting(() => lifecycle.EnterForegroundAsync()).Should().ThrowAsync<IOException>();
        lifecycle.InForeground.Should().BeTrue("the app is in front; the next resume starts the loop");
        loop.Running.Should().BeFalse();

        await Within(lifecycle.EnterForegroundAsync());
        loop.Running.Should().BeTrue();
        await Within(lifecycle.EnterBackgroundAsync());
        loop.Running.Should().BeFalse();
    }

    [Fact]
    public async Task ACallerThatStopsWaiting_LeavesTheTransitionToFinish_SoTheStateIsNeverHalfApplied()
    {
        var loop = new FakeLoop();
        await using var lifecycle = new IosLifecycle(loop);
        var accountsGate = Gate();
        loop.OnStartAccounts = () => accountsGate.Task;
        using var impatient = new CancellationTokenSource();

        var resume = lifecycle.EnterForegroundAsync(impatient.Token);
        await Reached(() => loop.Count("accounts-begin") == 1);
        impatient.Cancel();
        await FluentActions.Awaiting(() => resume).Should().ThrowAsync<OperationCanceledException>();
        accountsGate.SetResult();
        await Within(lifecycle.EnterBackgroundAsync());
        await Within(lifecycle.EnterForegroundAsync());

        loop.Running.Should().BeTrue();
        loop.Overlapped.Should().BeFalse();
    }
}
