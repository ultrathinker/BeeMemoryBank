using System;
using System.Threading;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>A second start of the full app asks the running copy to show its window (it used to end without a trace).</summary>
public sealed class ActivationSignalTests
{
    private static string NewName() => "BeeMemoryBank.Desktop.Activate.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public void WithNoRunningCopy_ThereIsNobodyToAsk()
    {
        ActivationSignal.SignalRunningInstance(NewName()).Should().BeFalse();
    }

    [Fact]
    public void ASecondStart_AsksTheRunningCopyToShowItsWindow_EachTime()
    {
        if (!OperatingSystem.IsWindows()) return; // named events exist only on Windows; the Mac uses the reopen request

        var name = NewName();
        using var running = ActivationSignal.Create(name);
        using var shown = new SemaphoreSlim(0);
        running.Listen(() => shown.Release());

        ActivationSignal.SignalRunningInstance(name).Should().BeTrue();
        shown.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the running copy was asked to show its window");

        ActivationSignal.SignalRunningInstance(name).Should().BeTrue();
        shown.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("and again at the next start");
    }

    [Fact]
    public void AHandlerThatThrows_DoesNotEndTheListening_AndDisposeIsHarmless()
    {
        if (!OperatingSystem.IsWindows()) return;

        var name = NewName();
        var running = ActivationSignal.Create(name);
        using var second = new SemaphoreSlim(0);
        var calls = 0;
        running.Listen(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("no window yet");
            second.Release();
        });

        ActivationSignal.SignalRunningInstance(name).Should().BeTrue();
        SpinWait.SpinUntil(() => Volatile.Read(ref calls) >= 1, TimeSpan.FromSeconds(5)).Should().BeTrue();
        ActivationSignal.SignalRunningInstance(name).Should().BeTrue();
        second.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("one failing request does not stop the next");

        running.Dispose();
        FluentActions.Invoking(running.Dispose).Should().NotThrow();
        ActivationSignal.SignalRunningInstance(name).Should().BeFalse("the copy has gone");
    }
}
