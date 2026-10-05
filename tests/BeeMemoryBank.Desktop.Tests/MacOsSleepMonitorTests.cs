using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.MacOS;
using BeeMemoryBank.Desktop.MacOS.Interop;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// A stand-in for the IOKit notification port: <see cref="Send"/> delivers a power message the way the real run loop does - on the
/// monitor's own thread, from inside <c>Pump</c> - and returns when the monitor has dealt with it. Records every
/// <c>IOAllowPowerChange</c> and every open and close of the registration, with the order of events.
/// </summary>
internal sealed class FakePowerPort : IPowerNotificationSource
{
    private readonly ConcurrentQueue<(uint Type, IntPtr Argument, TaskCompletionSource Done)> _inbox = new();
    private readonly SemaphoreSlim _signal = new(0);
    private Action<uint, IntPtr>? _onMessage;

    public List<string> Events { get; } = [];
    public List<IntPtr> Allowed { get; } = [];
    public int Opened { get; private set; }
    public int Closed { get; private set; }
    public Exception? OpenFails { get; set; }
    public Exception? AllowFails { get; set; }
    public int? PumpThreadId { get; private set; }

    public IPowerNotificationSession Open(Action<uint, IntPtr> onMessage)
    {
        if (OpenFails != null) throw OpenFails;
        _onMessage = onMessage;
        lock (Events) { Opened++; Events.Add("open"); }
        return new Session(this);
    }

    /// <summary>Delivers a message and waits until it was handled.</summary>
    public void Send(uint type, nint argument = 7)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inbox.Enqueue((type, argument, done));
        _signal.Release();
        done.Task.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the monitor must handle a message");
    }

    private sealed class Session(FakePowerPort port) : IPowerNotificationSession
    {
        public void AllowPowerChange(IntPtr id)
        {
            lock (port.Events) { port.Allowed.Add(id); port.Events.Add("allow"); }
            if (port.AllowFails != null) throw port.AllowFails;
        }

        public void Pump(Func<bool> stopRequested)
        {
            port.PumpThreadId = Environment.CurrentManagedThreadId;
            while (!stopRequested())
            {
                if (!port._signal.Wait(50)) continue;
                if (port._inbox.TryDequeue(out var message))
                {
                    try { port._onMessage!(message.Type, message.Argument); }
                    finally { message.Done.TrySetResult(); }
                }
            }
        }

        public void Wake() { }

        public void Dispose()
        {
            lock (port.Events) { port.Closed++; port.Events.Add("close"); }
        }
    }
}

internal sealed class RecordingNotifier : IUserNotifier
{
    public ConcurrentQueue<(string Title, string Message)> Notices { get; } = new();
    public Exception? Fails { get; set; }

    public void Notify(string title, string message)
    {
        Notices.Enqueue((title, message));
        if (Fails != null) throw Fails;
    }
}

public sealed class MacOsSleepMonitorTests
{
    private const uint WillSleep = 0xE0000280;
    private const uint CanSleep = 0xE0000270;
    private const uint WillNotSleep = 0xE0000290;
    private const uint PoweredOn = 0xE0000300;

    private readonly FakePowerPort _port = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly ConcurrentQueue<string> _log = new();

    private MacOsSleepMonitor Make(SleepLockRequest lockNode, TimeSpan? timeout = null) =>
        new(lockNode, _notifier, _port, timeout ?? TimeSpan.FromSeconds(5), _log.Enqueue);

    private static SleepLockRequest Locks(Action? onCall = null) => _ =>
    {
        onCall?.Invoke();
        return Task.FromResult(new SleepLockResult(true));
    };

    [Fact]
    public void TheIOKitMessageNumbers_AreTheOnesOfIOMessageH()
    {
        // iokit_common_msg(x) = 0xE0000000 | x; if one of these drifted the monitor would answer the wrong question
        IOKitPower.MessageCanSystemSleep.Should().Be(0xE0000270u);
        IOKitPower.MessageSystemWillSleep.Should().Be(0xE0000280u);
        IOKitPower.MessageSystemWillNotSleep.Should().Be(0xE0000290u);
        IOKitPower.MessageSystemHasPoweredOn.Should().Be(0xE0000300u);
    }

    [Fact]
    public void WillSleep_LocksTheNode_ThenAllowsThePowerChangeExactlyOnce_WithTheSameNotificationId()
    {
        var calls = 0;
        using var monitor = Make(Locks(() =>
        {
            lock (_port.Events) _port.Events.Add("lock");
            calls++;
        }));
        monitor.Start();

        _port.Send(WillSleep, argument: 12345);

        calls.Should().Be(1);
        _port.Allowed.Should().Equal(new IntPtr(12345));
        _port.Events.Should().ContainInOrder("open", "lock", "allow");
        _port.Events.Count(e => e == "allow").Should().Be(1);
    }

    [Fact]
    public void WillSleep_EachMessageGetsItsOwnAnswer()
    {
        using var monitor = Make(Locks());
        monitor.Start();

        _port.Send(WillSleep, 1);
        _port.Send(WillSleep, 2);

        _port.Allowed.Should().Equal(new IntPtr(1), new IntPtr(2));
    }

    [Fact]
    public void AHandlerThatThrows_StillGetsTheAnswer_AndAWarningIsShown()
    {
        using var monitor = Make(_ => throw new InvalidOperationException("the node is gone"));
        monitor.Start();

        _port.Send(WillSleep, 9);

        _port.Allowed.Should().Equal(new IntPtr(9));
        var notice = _notifier.Notices.Should().ContainSingle().Subject;
        notice.Title.Should().Contain("warning");
        notice.Message.Should().Contain("could not be locked").And.Contain("the node is gone");
        _log.Should().Contain(l => l.Contains("the node is gone"));
    }

    [Fact]
    public void AHandlerThatThrowsAsynchronously_IsTheSame()
    {
        using var monitor = Make(async _ =>
        {
            await Task.Delay(10);
            throw new TimeoutException("too slow");
        });
        monitor.Start();

        _port.Send(WillSleep, 5);

        _port.Allowed.Should().Equal(new IntPtr(5));
        _notifier.Notices.Should().ContainSingle().Which.Message.Should().Contain("too slow");
    }

    [Fact]
    public void AHandlerThatNeverFinishes_IsGivenUpOnAfterTheTimeout_AndSleepIsAllowed()
    {
        CancellationToken seen = default;
        var gate = new TaskCompletionSource<SleepLockResult>();
        using var monitor = Make(ct =>
        {
            seen = ct;
            return gate.Task;   // never completes by itself
        }, timeout: TimeSpan.FromMilliseconds(300));
        monitor.Start();

        var started = DateTime.UtcNow;
        _port.Send(WillSleep, 3);
        var took = DateTime.UtcNow - started;

        _port.Allowed.Should().Equal(new IntPtr(3));
        took.Should().BeLessThan(TimeSpan.FromSeconds(5), "a hanging node must never hold the Mac awake beyond the timeout");
        seen.IsCancellationRequested.Should().BeTrue("the request is cancelled when it is given up on");
        _notifier.Notices.Should().ContainSingle().Which.Message.Should().Contain("did not answer");
        gate.SetResult(new SleepLockResult(true));
    }

    [Fact]
    public void ANodeThatSaysNo_GivesAWarningWithItsReason_AndSleepIsStillAllowed()
    {
        using var monitor = Make(_ => Task.FromResult(new SleepLockResult(false, "The node answered 500 to the lock request.", 500)));
        monitor.Start();

        _port.Send(WillSleep);

        _port.Allowed.Should().HaveCount(1);
        _notifier.Notices.Should().ContainSingle().Which.Message.Should().Contain("500");
    }

    // ── a node that does not offer lock-on-sleep yet (HTTP 501) ────────────────

    [Fact]
    public void A501_MeansTheNodeDoesNotOfferLockOnSleepYet_OneLogLine_NoNotice_SleepStillAllowedOnce()
    {
        using var monitor = Make(_ => Task.FromResult(new SleepLockResult(false, "The node answered 501 to the lock request.", 501)));
        monitor.Start();

        _port.Send(WillSleep, 21);

        _notifier.Notices.Should().BeEmpty("a banner at every sleep about something the person cannot act on");
        _log.Should().ContainSingle().Which.Should().Contain("501").And.Contain("does not offer lock-on-sleep");
        _port.Allowed.Should().Equal(new IntPtr(21));
        _port.Events.Count(e => e == "allow").Should().Be(1);
    }

    // ── a node this app did not start (no key to lock it with): logged, never shown ────────────────

    [Fact]
    public void ANodeTheAppHoldsNoKeyFor_OneLogLine_NoNotice_SleepStillAllowedOnce()
    {
        using var monitor = Make(_ => Task.FromResult(new SleepLockResult(false, "This app did not start the open node, so it has no key to lock it with.", LogOnly: true)));
        monitor.Start();

        _port.Send(WillSleep, 31);

        _notifier.Notices.Should().BeEmpty("the person cannot act on it at the moment of sleep");
        _log.Should().ContainSingle().Which.Should().Contain("not locked").And.Contain("no key");
        _port.Allowed.Should().Equal(new IntPtr(31));
    }

    [Fact]
    public void WhenTheVaultWasLocked_TheNoticeSaysSo()
    {
        using var monitor = Make(Locks());
        monitor.Start();

        _port.Send(WillSleep, 1);

        var notice = _notifier.Notices.Should().ContainSingle().Subject;
        notice.Title.Should().NotContain("warning");
        notice.Message.Should().Contain("vault was locked");
    }

    [Fact]
    public void WhenThereWasNothingToLock_TheNoticeDoesNotClaimTheVaultWasLocked()
    {
        using var monitor = Make(_ => Task.FromResult(new SleepLockResult(true, "No node is open, so there is nothing to lock.")));
        monitor.Start();

        _port.Send(WillSleep, 2);

        _notifier.Notices.Should().ContainSingle().Which.Message.Should().NotContain("was locked");
    }

    [Fact]
    public void A501_EverySleep_StaysQuiet_AndTheMonitorKeepsWorking()
    {
        var answers = new Queue<SleepLockResult>([
            new SleepLockResult(false, "501", 501),
            new SleepLockResult(false, "501", 501),
            new SleepLockResult(true),
        ]);
        using var monitor = Make(_ => Task.FromResult(answers.Dequeue()));
        monitor.Start();

        _port.Send(WillSleep, 1);
        _port.Send(WillSleep, 2);
        _notifier.Notices.Should().BeEmpty();
        _port.Send(WillSleep, 3);

        _port.Allowed.Should().Equal(new IntPtr(1), new IntPtr(2), new IntPtr(3));
        _notifier.Notices.Should().ContainSingle("only the sleep where the lock worked says anything").Which.Title.Should().NotContain("warning");
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(405)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void EveryOtherHttpFailure_StillGivesAWarning(int status)
    {
        using var monitor = Make(_ => Task.FromResult(new SleepLockResult(false, $"The node answered {status} to the lock request.", status)));
        monitor.Start();

        _port.Send(WillSleep, 5);

        var notice = _notifier.Notices.Should().ContainSingle().Subject;
        notice.Title.Should().Contain("warning");
        notice.Message.Should().Contain(status.ToString());
        _port.Allowed.Should().Equal(new IntPtr(5));
    }

    [Fact]
    public void A501InTheTextOnly_WithoutTheStatus_IsNotMistakenForTheStub()
    {
        // the decision is made on the status the shell reports, not on words in a message
        using var monitor = Make(_ => Task.FromResult(new SleepLockResult(false, "Something said 501 somewhere.")));
        monitor.Start();

        _port.Send(WillSleep);

        _notifier.Notices.Should().ContainSingle().Which.Title.Should().Contain("warning");
    }

    [Fact]
    public void AConnectionRefused_AndATimeout_StillGiveAWarning()
    {
        using var refused = Make(_ => throw new System.Net.Http.HttpRequestException("Connection refused"));
        refused.Start();
        _port.Send(WillSleep, 1);
        _notifier.Notices.Should().ContainSingle().Which.Message.Should().Contain("Connection refused");

        var gate = new TaskCompletionSource<SleepLockResult>();
        var timedOutNotifier = new RecordingNotifier();
        var otherPort = new FakePowerPort();
        using var slow = new MacOsSleepMonitor(_ => gate.Task, timedOutNotifier, otherPort, TimeSpan.FromMilliseconds(200), _log.Enqueue);
        slow.Start();
        otherPort.Send(WillSleep, 2);
        timedOutNotifier.Notices.Should().ContainSingle().Which.Message.Should().Contain("did not answer");
        otherPort.Allowed.Should().Equal(new IntPtr(2));
        gate.SetResult(new SleepLockResult(true));
    }

    [Fact]
    public void WhenAllWentWell_TheNoticeIsThePlainOne_NotAWarning()
    {
        using var monitor = Make(Locks());
        monitor.Start();

        _port.Send(WillSleep);

        var notice = _notifier.Notices.Should().ContainSingle().Subject;
        notice.Title.Should().NotContain("warning");
        notice.Message.Should().Contain("about to sleep");
    }

    [Fact]
    public void NothingToLock_IsNotAFailure()
    {
        using var monitor = Make(_ => Task.FromResult(new SleepLockResult(true, "No node is open, so there is nothing to lock.")));
        monitor.Start();

        _port.Send(WillSleep);

        _notifier.Notices.Should().ContainSingle().Which.Title.Should().NotContain("warning");
    }

    [Fact]
    public void ANotifierThatThrows_OrAllowThatFails_NeverEscapesTheCallback()
    {
        _notifier.Fails = new InvalidOperationException("no banner");
        _port.AllowFails = new InvalidOperationException("IOAllowPowerChange failed");
        using var monitor = Make(Locks());
        monitor.Start();

        var send = () => _port.Send(WillSleep);

        send.Should().NotThrow("an exception out of a native callback would end the process");
        _port.Allowed.Should().HaveCount(1);
        monitor.IsRunning.Should().BeTrue("and the monitor goes on");
        _log.Should().Contain(l => l.Contains("no banner")).And.Contain(l => l.Contains("IOAllowPowerChange failed"));
    }

    [Fact]
    public void CanSystemSleep_IsAlwaysAnswered_Yes_WithoutLocking()
    {
        var locks = 0;
        using var monitor = Make(Locks(() => locks++));
        monitor.Start();

        _port.Send(CanSleep, 77);

        locks.Should().Be(0, "an idle-sleep query is not a sleep");
        _port.Allowed.Should().Equal(new IntPtr(77));
        _notifier.Notices.Should().BeEmpty();
    }

    [Theory]
    [InlineData(WillNotSleep)]
    [InlineData(PoweredOn)]
    [InlineData(0xE0000320u)]   // WillPowerOn
    [InlineData(0u)]
    public void OtherMessages_AreNotAnswered_AndLockNothing(uint message)
    {
        var locks = 0;
        using var monitor = Make(Locks(() => locks++));
        monitor.Start();

        _port.Send(message, 1);

        locks.Should().Be(0);
        _port.Allowed.Should().BeEmpty("only will-sleep and can-sleep expect an answer");
    }

    [Fact]
    public void TheMessagesAreHandledOnTheMonitorsOwnThread_NotTheCallers()
    {
        using var monitor = Make(Locks());
        monitor.Start();

        _port.Send(WillSleep);

        _port.PumpThreadId.Should().NotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void Start_RegistersOnce_AndDisposeDeregistersOnce_AndEndsTheThread()
    {
        var monitor = Make(Locks());

        monitor.Start();
        monitor.Start();
        monitor.IsRunning.Should().BeTrue();
        _port.Opened.Should().Be(1);

        monitor.Dispose();
        monitor.Dispose();

        monitor.IsRunning.Should().BeFalse();
        monitor.ThreadHasExited.Should().BeTrue("no thread is left behind");
        _port.Closed.Should().Be(1);
        _port.Events.Should().Equal("open", "close");
    }

    [Fact]
    public void Dispose_WithoutStart_IsHarmless_AndAStartedMonitorCannotBeRestartedAfterDispose()
    {
        var never = Make(Locks());
        never.Dispose();
        never.ThreadHasExited.Should().BeTrue();

        var monitor = Make(Locks());
        monitor.Start();
        monitor.Dispose();
        monitor.Start();

        _port.Opened.Should().Be(1, "a disposed monitor stays stopped");
    }

    [Fact]
    public void WhenTheRegistrationFails_ItIsLogged_AndShownAsAWarning_AndStartDoesNotThrow()
    {
        _port.OpenFails = new InvalidOperationException("IORegisterForSystemPower did not return a connection to the power manager.");
        using var monitor = Make(Locks());

        var start = () => monitor.Start();

        start.Should().NotThrow();
        monitor.IsRunning.Should().BeFalse();
        _notifier.Notices.Should().ContainSingle().Which.Message.Should().Contain("will NOT be locked");
        _log.Should().Contain(l => l.Contains("Could not watch for sleep"));
        monitor.ThreadHasExited.Should().BeTrue();
    }

    [Fact]
    public void ManyStartStopCycles_LeaveNoThreadAndNoRegistration()
    {
        for (var i = 0; i < 20; i++)
        {
            var monitor = Make(Locks());
            monitor.Start();
            monitor.Dispose();
            monitor.ThreadHasExited.Should().BeTrue();
        }

        _port.Opened.Should().Be(20);
        _port.Closed.Should().Be(20);
    }

    [Fact]
    public void ARequiredArgument_IsChecked()
    {
        var noLock = () => new MacOsSleepMonitor(null!, _notifier, _port, TimeSpan.FromSeconds(1), _ => { });
        var noNotifier = () => new MacOsSleepMonitor(Locks(), null!, _port, TimeSpan.FromSeconds(1), _ => { });

        noLock.Should().Throw<ArgumentNullException>();
        noNotifier.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void OffMacOS_TheRealPortRefusesClearly_InsteadOfLoadingIOKit()
    {
        if (OperatingSystem.IsMacOS()) return;   // on a Mac this is the real port, covered by the Mac-only tests

        var log = new List<string>();
        using var monitor = new MacOsSleepMonitor(Locks(), _notifier, new IoKitPowerNotificationSource(), TimeSpan.FromSeconds(1), log.Add);

        monitor.Start();

        monitor.IsRunning.Should().BeFalse();
        log.Should().Contain(l => l.Contains("runs only on macOS"));
    }
}
