using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Crypto.Tests;

/// <summary>
/// The queue is process-wide, so every test that touches it lives in this one class (xUnit runs a
/// class's tests one after another).
/// </summary>
public class HeavyDerivationQueueTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task CancelMidDerivation_ReleasesTheCallerAtOnce_TheDerivationFinishesInTheBackground()
    {
        using var started = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var job = HeavyDerivationQueue.RunAsync(() =>
        {
            started.Set();
            return RecoveryBoxCrypto.Wrap(MasterKeyManager.GenerateMasterDek(), "pw", RecoveryBoxKdf.Strong1024);
        }, cts.Token);
        started.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();
        var next = HeavyDerivationQueue.RunAsync(() => clock.Elapsed);

        cts.Cancel();
        var releasedAfter = System.Diagnostics.Stopwatch.StartNew();
        var act = () => job;
        await act.Should().ThrowAsync<OperationCanceledException>();
        releasedAfter.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500), "the caller must not wait for Argon2");

        var derivationTook = await next; // runs once the abandoned derivation has finished
        output.WriteLine($"s1024t4 derivation (abandoned, finished in background): {derivationTook.TotalSeconds:F1} s");
    }

    [Fact]
    public async Task HeavyPreset_OnTheQueue_RoundTrips()
    {
        var dek = MasterKeyManager.GenerateMasterDek();

        var seal = await HeavyDerivationQueue.RunAsync(() => RecoveryBoxCrypto.Wrap(dek, "strong pw", RecoveryBoxKdf.Strong512));
        var opened = await HeavyDerivationQueue.RunAsync(() =>
            RecoveryBoxCrypto.Unwrap("strong pw", seal.KdfPreset, seal.Salt, seal.Wrapped, seal.Iv));
        var wrong = await HeavyDerivationQueue.RunAsync(() =>
            RecoveryBoxCrypto.TryUnwrap("other pw", seal.KdfPreset, seal.Salt, seal.Wrapped, seal.Iv));

        opened.Should().Equal(dek);
        wrong.Should().BeNull();
    }

    [Fact]
    public async Task Jobs_RunOneAtATime_InOrder()
    {
        var running = 0;
        var maxRunning = 0;
        var order = new List<int>();

        var jobs = Enumerable.Range(0, 4).Select(i => HeavyDerivationQueue.RunAsync(() =>
        {
            var now = Interlocked.Increment(ref running);
            lock (order) { maxRunning = Math.Max(maxRunning, now); order.Add(i); }
            Thread.Sleep(30);
            Interlocked.Decrement(ref running);
            return i;
        })).ToArray();
        await Task.WhenAll(jobs);

        maxRunning.Should().Be(1);
        order.Should().Equal(0, 1, 2, 3);
    }

    [Fact]
    public async Task FullQueue_RefusesInsteadOfPilingUp()
    {
        using var gate = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var blocker = HeavyDerivationQueue.RunAsync(() => { started.Set(); gate.Wait(); return 0; });
        started.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        var queued = Enumerable.Range(0, 4).Select(_ => HeavyDerivationQueue.RunAsync(() => 1)).ToList();
        Action act = () => HeavyDerivationQueue.RunAsync(() => 2);

        act.Should().Throw<KdfBusyException>();
        gate.Set();
        await blocker;
        await Task.WhenAll(queued);
    }

    [Fact]
    public async Task Work_RunsOffTheCallersThread()
    {
        var caller = Environment.CurrentManagedThreadId;

        var worker = await HeavyDerivationQueue.RunAsync(() => Environment.CurrentManagedThreadId);

        worker.Should().NotBe(caller);
    }
}
