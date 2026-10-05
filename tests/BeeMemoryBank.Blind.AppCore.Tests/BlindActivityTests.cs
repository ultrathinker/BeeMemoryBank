using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>F-03: the list of what the blind copy is doing, which "Disconnect and wipe" stops and waits for.</summary>
public sealed class BlindActivityTests
{
    [Fact]
    public async Task WaitIdle_IsTrueAtOnce_WhenNothingRuns_AndOnlyAfterTheLastOperationEnds()
    {
        var activity = new BlindActivity();
        (await activity.WaitIdleAsync(TimeSpan.Zero)).Should().BeTrue();

        var sync = activity.TryBegin(BlindActivity.Sync)!;
        var heavy = activity.TryBegin(BlindActivity.Heavy)!;
        activity.ActiveCount.Should().Be(2);
        activity.IsActive(BlindActivity.Sync).Should().BeTrue();

        var wait = activity.WaitIdleAsync(TimeSpan.FromSeconds(10));
        sync.Dispose();
        await Task.Delay(100);
        wait.IsCompleted.Should().BeFalse("one operation is still running");
        heavy.Dispose();
        (await wait).Should().BeTrue();
        activity.ActiveCount.Should().Be(0);
    }

    [Fact]
    public async Task WaitIdle_GivesUpAfterTheBound_WhileAnOperationRuns()
    {
        var activity = new BlindActivity();
        using var op = activity.TryBegin(BlindActivity.Sync)!;

        (await activity.WaitIdleAsync(TimeSpan.FromMilliseconds(100))).Should().BeFalse();
        activity.ActiveKinds().Should().Equal(BlindActivity.Sync);
    }

    [Fact]
    public void CancelAll_FiresEveryOperationsToken_AndTheCallersOwnTokenToo()
    {
        var activity = new BlindActivity();
        using var own = new CancellationTokenSource();
        using var a = activity.TryBegin(BlindActivity.Sync)!;
        using var b = activity.TryBegin(BlindActivity.Heavy, own.Token)!;

        own.Cancel();
        a.Token.IsCancellationRequested.Should().BeFalse();
        b.Token.IsCancellationRequested.Should().BeTrue("the caller's own token still works");

        activity.CancelAll();
        a.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void WhileClosed_NoNewOperationStarts_AndReopenAcceptsThemAgainWithAFreshToken()
    {
        var activity = new BlindActivity();
        activity.Close();
        activity.CancelAll();

        activity.TryBegin(BlindActivity.Sync).Should().BeNull("a worker that starts during the wipe must not touch the files");
        activity.IsClosed.Should().BeTrue();

        activity.Reopen();
        using var op = activity.TryBegin(BlindActivity.Sync);
        op.Should().NotBeNull();
        op!.Token.IsCancellationRequested.Should().BeFalse("the cancellation of the abandoned wipe is not inherited");
    }

    [Fact]
    public void DisposingAnOperationTwice_CountsItOnce()
    {
        var activity = new BlindActivity();
        var op = activity.TryBegin(BlindActivity.Sync)!;
        using var other = activity.TryBegin(BlindActivity.Heavy)!;

        op.Dispose();
        op.Dispose();

        activity.ActiveCount.Should().Be(1);
    }
}
