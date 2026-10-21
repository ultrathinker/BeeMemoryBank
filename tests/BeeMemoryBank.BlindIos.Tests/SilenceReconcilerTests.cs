using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Boundary;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>
/// A notification center that behaves like the phone's where it matters: a call changes what is pending only when it COMPLETES (so the order of completion is the order of effects), the test
/// decides when each call completes, and any two calls that are outstanding at the same time are written down.
/// </summary>
internal sealed class FakeNotificationCenter : ISilenceNotificationCenter
{
    private readonly object _gate = new();
    private readonly List<string> _calls = [];
    private int _outstanding;

    /// <summary>What is pending in the phone, as the calls that completed left it.</summary>
    public HashSet<string> Pending { get; } = new(StringComparer.Ordinal);

    public int MaxOutstanding { get; private set; }

    /// <summary>Called when a call begins with its description ("pending", "remove:id", "add:id", "add:id#generation"); the call completes when the task it returns does.</summary>
    public Func<string, Task>? Gate { get; set; }

    /// <summary>Makes a call fail: by description. Return an exception to throw, or null.</summary>
    public Func<string, Exception?>? Fault { get; set; }

    /// <summary>Makes an add answer "refused".</summary>
    public Func<string, bool>? Refuse { get; set; }

    public IReadOnlyList<string> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    public int Count(string prefix)
    {
        lock (_gate) return _calls.Count(c => c.StartsWith(prefix, StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<string>> PendingIdsAsync()
    {
        await Begin("pending");
        try
        {
            lock (_gate) return [.. Pending];
        }
        finally { End(); }
    }

    public async Task RemoveAsync(IReadOnlyList<string> requestIds)
    {
        await Begin("remove:" + string.Join(",", requestIds));
        try
        {
            lock (_gate) foreach (var id in requestIds) Pending.Remove(id);
        }
        finally { End(); }
    }

    public async Task<bool> AddAsync(SilenceAdd add, TimeSpan after)
    {
        await Begin("add:" + add.RequestId);
        try
        {
            if (Refuse?.Invoke(add.RequestId) == true) return false;
            lock (_gate) Pending.Add(add.RequestId);
            return true;
        }
        finally { End(); }
    }

    public void RequestPermission()
    {
        lock (_gate) _calls.Add("permission");
    }

    private async Task Begin(string call)
    {
        lock (_gate)
        {
            _calls.Add(call);
            _outstanding++;
            MaxOutstanding = Math.Max(MaxOutstanding, _outstanding);
        }
        try
        {
            if (Fault?.Invoke(call) is { } fault) throw fault;
            if (Gate?.Invoke(call) is { } gate) await gate;
        }
        catch
        {
            End();
            throw;
        }
    }

    private void End()
    {
        lock (_gate) _outstanding--;
    }
}

/// <summary>A clock the test moves; timers (the native timeouts) stay real.</summary>
internal sealed class MovableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// Fix round 3, item 1 (final review, MINOR): the whole native read / withdraw / add of a notification update is one job of one ordered queue, so that a late withdrawal cannot delete a newer add
/// and the book never believes in a warning the phone does not have. Driven with a fake notification center whose calls complete when the test says, and whose effect is the order of completion.
/// </summary>
public sealed class SilenceReconcilerTests
{
    private const string AnnaId = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";
    private static readonly string RequestId = BlindSilenceAlarm.RequestId + "." + AnnaId;

    private static async Task Within(Task task, int seconds = 30) => await task.WaitAsync(TimeSpan.FromSeconds(seconds));

    private static async Task Reached(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the state the test waits for was not reached");
            await Task.Delay(2);
        }
    }

    private static BlindAccountSnapshot Anna(DateTimeOffset now, bool held = false, int silentDays = 5, string name = "Anna") =>
        new(new BlindAccountEntry(AnnaId, BlindAccountLayouts.Dir, name, InviteId: "i1", PausedByUser: held),
            new BlindAppStatus(Guid.NewGuid(), "iPhone", true, true, now.AddDays(-silentDays), null, BlindBackupSchedule.Off, false, null, null, [], [], Endpoint: "https://a.test:5610"),
            Pause: held ? new BlindPause(BlindPauseReasons.PausedByYou) : null);

    private static BlindAccountsSnapshot Snapshot(params BlindAccountSnapshot[] accounts) => new(accounts, BlindDeviceLimits.None, null, true, 8);

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- the review's sequence: a late withdrawal must not delete a newer add -------------------------------------------------------------------

    [Fact]
    public async Task ALateWithdrawal_CannotDeleteANewerAdd_BecauseTheNativeCallsRunOneAtATime()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var now = clock.GetUtcNow();
        var center = new FakeNotificationCenter();
        var reconciler = new SilenceReconciler(center, time: clock);
        await Within(reconciler.UpdateAsync(Snapshot(Anna(now))));
        center.Pending.Should().Contain(RequestId, "the control: the first update added the warning");

        var removeGate = Gate();
        center.Gate = call => call.StartsWith("remove:", StringComparison.Ordinal) ? removeGate.Task : Task.CompletedTask;
        var held = reconciler.UpdateAsync(Snapshot(Anna(now, held: true)));      // update A: the account is held, its warning goes
        await Reached(() => center.Count("remove:") == 1);
        var active = reconciler.UpdateAsync(Snapshot(Anna(now)));                // update B, while A's withdrawal is still out: the account is active again
        await Task.Delay(100);

        center.Count("add:").Should().Be(1, "B has not touched the center: A's withdrawal has not been answered yet");
        removeGate.SetResult();
        await Within(Task.WhenAll(held, active));

        center.Pending.Should().Contain(RequestId, "the add of B came after the withdrawal of A had completed");
        reconciler.Book.ShowsAt(RequestId).Should().NotBeNull("and the book agrees with the phone");
        center.MaxOutstanding.Should().Be(1, "never two native calls at once");
        var calls = center.Calls;
        calls.Skip(calls.ToList().IndexOf("remove:" + RequestId)).Should().Equal("remove:" + RequestId, "add:" + RequestId);
    }

    [Fact]
    public async Task TheSameCallsWithoutTheQueue_LoseTheWarning_WhileTheBookStillBelievesItIsPending()
    {
        // the proof that the fake is sensitive to the review's sequence: the withdrawal of A is slow, the add of B is quick - the effects land in the opposite order
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var now = clock.GetUtcNow();
        var center = new FakeNotificationCenter { Gate = call => call.StartsWith("remove:", StringComparison.Ordinal) ? Task.Delay(150) : Task.CompletedTask };
        var book = new SilenceRequestBook();

        var heldPlan = book.Plan(Snapshot(Anna(now, held: true)), now);          // A
        var withdrawal = center.RemoveAsync([RequestId]);                         // not awaited: the old notifier did not wait either
        var activePlan = book.Plan(Snapshot(Anna(now)), now);                     // B
        var add = activePlan.Adds.Single();
        var accepted = await center.AddAsync(add, TimeSpan.FromDays(1));
        book.Answered(add.RequestId, add.Generation, accepted, now.AddDays(1));
        await Within(withdrawal);

        heldPlan.Removes.Should().Contain(RequestId);
        center.Pending.Should().NotContain(RequestId, "the late withdrawal deleted the newer add");
        book.ShowsAt(RequestId).Should().NotBeNull("and the book is still sure that the warning is pending: nothing would ever add it again");
        book.Plan(Snapshot(Anna(now)), now).IsEmpty.Should().BeTrue();
    }

    // ---- an update during an in-flight update ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnUpdateDuringAnInFlightUpdate_WaitsForIt_AndSeveralThatWaitAreOneJobOfTheNewestSnapshot()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var now = clock.GetUtcNow();
        var center = new FakeNotificationCenter();
        var reconciler = new SilenceReconciler(center, time: clock);
        await Within(reconciler.UpdateAsync(Snapshot()));
        var addGate = Gate();
        center.Gate = call => call.StartsWith("add:", StringComparison.Ordinal) ? addGate.Task : Task.CompletedTask;

        var first = reconciler.UpdateAsync(Snapshot(Anna(now, name: "Anna")));
        await Reached(() => center.Count("add:") == 1);
        var second = reconciler.UpdateAsync(Snapshot(Anna(now, name: "Anna R.")));
        var third = reconciler.UpdateAsync(Snapshot(Anna(now, name: "Anna Rose")));
        var fourth = reconciler.UpdateAsync(Snapshot(Anna(now, name: "Anna Rose")));
        await Task.Delay(100);

        second.IsCompleted.Should().BeFalse("it waits for the update in flight");
        center.Count("add:").Should().Be(1);
        addGate.SetResult();
        await Within(Task.WhenAll(first, second, third, fourth));

        center.Count("add:").Should().Be(2, "the update in flight, then ONE job for the three that waited (the newest name)");
        center.MaxOutstanding.Should().Be(1);
        reconciler.Book.ShowsAt(RequestId).Should().NotBeNull();
    }

    // ---- calls that fail, and calls that are never answered ---------------------------------------------------------------------------------

    [Fact]
    public async Task AnAddTheCenterNeverAnswers_IsGivenUp_ThePlanIsWithdrawn_AndALateAnswerChangesNothing()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var now = clock.GetUtcNow();
        var center = new FakeNotificationCenter();
        var reconciler = new SilenceReconciler(center, time: clock, nativeTimeout: TimeSpan.FromMilliseconds(150));
        var lost = Gate();
        var first = true;
        center.Gate = call =>
        {
            if (!call.StartsWith("add:", StringComparison.Ordinal) || !first) return Task.CompletedTask;
            first = false;
            return lost.Task;
        };
        var snapshot = Snapshot(Anna(now));

        await Within(reconciler.UpdateAsync(snapshot));      // the add is lost: after the timeout the job goes on without it

        reconciler.Book.ShowsAt(RequestId).Should().BeNull("an add the center did not answer is not known to be pending");
        await Within(reconciler.UpdateAsync(snapshot));       // an unchanged snapshot: the plan was withdrawn, so it is added again
        center.Count("add:").Should().Be(2);
        center.Pending.Should().Contain(RequestId);
        reconciler.Book.ShowsAt(RequestId).Should().NotBeNull();
        var shows = reconciler.Book.ShowsAt(RequestId);

        lost.SetResult();                                      // the old answer arrives at last
        await Task.Delay(50);
        reconciler.Book.ShowsAt(RequestId).Should().Be(shows, "a late answer to an add that was given up changes nothing");
    }

    [Fact]
    public async Task AFailedAdd_IsAddedAgainByTheNextUpdate_OfAnUnchangedSnapshot()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var now = clock.GetUtcNow();
        var center = new FakeNotificationCenter { Refuse = _ => true };
        var reconciler = new SilenceReconciler(center, time: clock);
        var snapshot = Snapshot(Anna(now));

        await Within(reconciler.UpdateAsync(snapshot));
        center.Pending.Should().BeEmpty();
        reconciler.Book.ShowsAt(RequestId).Should().BeNull();

        center.Refuse = null;
        await Within(reconciler.UpdateAsync(snapshot));
        center.Pending.Should().Contain(RequestId);
        center.Count("add:").Should().Be(2);
    }

    [Fact]
    public async Task AWithdrawalTheCenterDidNotConfirm_IsMadeAgainByTheNextUpdate()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var now = clock.GetUtcNow();
        var center = new FakeNotificationCenter();
        var reconciler = new SilenceReconciler(center, time: clock);
        await Within(reconciler.UpdateAsync(Snapshot(Anna(now))));
        center.Fault = call => call.StartsWith("remove:", StringComparison.Ordinal) ? new IOException("the center is not there") : null;
        var held = Snapshot(Anna(now, held: true));

        await Within(reconciler.UpdateAsync(held));
        center.Pending.Should().Contain(RequestId, "the withdrawal failed");

        center.Fault = null;
        await Within(reconciler.UpdateAsync(held));
        center.Pending.Should().NotContain(RequestId, "an unchanged snapshot withdrew it again");
    }

    [Fact]
    public async Task AnExceptionOfTheCenter_NeverEscapesAJob_AndTheNextUpdateStillWorks()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var center = new FakeNotificationCenter { Fault = _ => new InvalidOperationException("the center blew up at /private/var/mobile/canary") };
        var reconciler = new SilenceReconciler(center, time: clock);

        await Within(reconciler.UpdateAsync(Snapshot(Anna(clock.GetUtcNow()))));      // must not throw

        center.Fault = null;
        await Within(reconciler.UpdateAsync(Snapshot(Anna(clock.GetUtcNow()))));
        center.Pending.Should().Contain(RequestId);
    }

    // ---- the request that fired, the stale ones, the permission --------------------------------------------------------------------------

    [Fact]
    public async Task ARequestThatFired_IsPlannedAgainInTheSameJob_AfterTheReadAndBeforeTheAdd()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var now = clock.GetUtcNow();
        var center = new FakeNotificationCenter();
        var reconciler = new SilenceReconciler(center, time: clock);
        var snapshot = Snapshot(Anna(now));
        await Within(reconciler.UpdateAsync(snapshot));
        var first = reconciler.Book.ShowsAt(RequestId);
        center.Pending.Remove(RequestId);                                              // iOS showed it and removed it
        clock.Advance(BlindSilenceAlarm.RemindAfter + TimeSpan.FromMinutes(2));
        var before = center.Calls.Count;

        await Within(reconciler.UpdateAsync(snapshot));

        center.Calls.Skip(before).Should().Equal(["pending", "add:" + RequestId], "the read found the request gone, and the next reminder was added in the same pass");
        center.Pending.Should().Contain(RequestId);
        reconciler.Book.ShowsAt(RequestId).Should().BeAfter(first!.Value);
    }

    [Fact]
    public async Task TheFirstJob_WithdrawsWhatNoListedAccountOwns_AndNothingElse_AndTheCenterIsNotReadAgainWithinAMinute()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var now = clock.GetUtcNow();
        var center = new FakeNotificationCenter();
        center.Pending.Add(BlindSilenceAlarm.RequestId + ".dddddddddddddddddddddddddddddddd");     // an account that was removed while the app was not running
        center.Pending.Add("something-else-of-the-app");
        var reconciler = new SilenceReconciler(center, time: clock);

        await Within(reconciler.UpdateAsync(Snapshot(Anna(now))));

        center.Pending.Should().NotContain(BlindSilenceAlarm.RequestId + ".dddddddddddddddddddddddddddddddd");
        center.Pending.Should().Contain("something-else-of-the-app").And.Contain(RequestId);
        var reads = center.Count("pending");
        await Within(reconciler.UpdateAsync(Snapshot(Anna(now, name: "Anna R."))));
        center.Count("pending").Should().Be(reads, "the stale ones are withdrawn once, and the fired ones are looked for at most once a minute");
    }

    [Fact]
    public async Task ThePermission_IsAskedOnce_BeforeTheFirstAdd_AndNeverWithoutOne()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var now = clock.GetUtcNow();
        var center = new FakeNotificationCenter();
        var reconciler = new SilenceReconciler(center, time: clock);

        await Within(reconciler.UpdateAsync(Snapshot()));
        center.Count("permission").Should().Be(0, "no account, no warning, nothing to ask for");
        await Within(reconciler.UpdateAsync(Snapshot(Anna(now))));
        await Within(reconciler.UpdateAsync(Snapshot(Anna(now, name: "Anna R."))));

        center.Count("permission").Should().Be(1);
        var calls = center.Calls;
        calls.ToList().IndexOf("permission").Should().BeLessThan(calls.ToList().IndexOf("add:" + RequestId));
    }

    [Fact]
    public async Task AlertsAllowed_IsQueuedBehindTheJobInFlight_AndAnAddFollowsIt()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var center = new FakeNotificationCenter();
        var readGate = Gate();
        var refuseFirst = true;
        center.Gate = call => call == "pending" ? readGate.Task : Task.CompletedTask;
        center.Refuse = _ =>
        {
            var refuse = refuseFirst;
            refuseFirst = false;
            return refuse;
        };
        var reconciler = new SilenceReconciler(center, time: clock);

        var update = reconciler.UpdateAsync(Snapshot(Anna(clock.GetUtcNow())));
        await Reached(() => center.Count("pending") == 1);
        var allowed = reconciler.AlertsAllowedAsync();
        await Task.Delay(100);

        allowed.IsCompleted.Should().BeFalse("it does not touch the book while a job is in the middle of a reconciliation");
        center.Count("add:").Should().Be(0);
        readGate.SetResult();
        await Within(Task.WhenAll(update, allowed));

        center.Count("add:").Should().Be(2, "the add the system refused, and the add that the grant made again");
        center.Pending.Should().Contain(RequestId);
        reconciler.Book.ShowsAt(RequestId).Should().NotBeNull();
        center.MaxOutstanding.Should().Be(1);
    }

    [Fact]
    public async Task AGrantedPermission_AddsTheReminderThatWasRefusedBefore_WithoutAnyNewSnapshot()
    {
        // fix round 4 (re-review, MINOR): the system refuses the add because alerts are not allowed yet; the person then says yes. Nothing new happens to the accounts, so no update follows -
        // the grant itself has to apply the newest snapshot.
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var center = new FakeNotificationCenter { Refuse = _ => true };
        var reconciler = new SilenceReconciler(center, time: clock);
        await Within(reconciler.UpdateAsync(Snapshot(Anna(clock.GetUtcNow()))));
        center.Pending.Should().BeEmpty("the control: not allowed yet, the add was refused");
        reconciler.Book.ShowsAt(RequestId).Should().BeNull();

        center.Refuse = null;                                   // the person said yes
        await Within(reconciler.AlertsAllowedAsync());          // no new snapshot, no new update

        center.Count("add:").Should().Be(2);
        center.Pending.Should().Contain(RequestId, "the grant applied the newest snapshot");
        reconciler.Book.ShowsAt(RequestId).Should().NotBeNull();
    }

    [Fact]
    public async Task APermissionGrantedWhileTheRefusedAddIsStillOut_IsAppliedAfterIt()
    {
        // the ordering of the review: the add is still out when the permission callback comes; the system then answers the add with a refusal
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var center = new FakeNotificationCenter();
        var addGate = Gate();
        var first = true;
        center.Gate = call => call.StartsWith("add:", StringComparison.Ordinal) && first ? addGate.Task : Task.CompletedTask;
        center.Refuse = _ =>
        {
            var refuse = first;
            first = false;
            return refuse;
        };
        var reconciler = new SilenceReconciler(center, time: clock);

        var update = reconciler.UpdateAsync(Snapshot(Anna(clock.GetUtcNow())));
        await Reached(() => center.Count("add:") == 1);
        var allowed = reconciler.AlertsAllowedAsync();          // the permission callback
        addGate.SetResult();                                    // and only then the refusal of the first add
        await Within(Task.WhenAll(update, allowed));

        center.Pending.Should().Contain(RequestId);
        center.Count("add:").Should().Be(2);
        center.MaxOutstanding.Should().Be(1);
    }

    [Fact]
    public async Task AGrantedPermission_BeforeAnySnapshot_DoesNothing_AndTheFirstUpdateAddsAsUsual()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var center = new FakeNotificationCenter();
        var reconciler = new SilenceReconciler(center, time: clock);

        await Within(reconciler.AlertsAllowedAsync());

        center.Calls.Should().BeEmpty("there is no snapshot to apply");
        await Within(reconciler.UpdateAsync(Snapshot(Anna(clock.GetUtcNow()))));
        center.Pending.Should().Contain(RequestId);
    }

    // ---- the iOS class is only the adapter -----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheIosNotifier_OnlyGivesTheReconcilerTheCenter_EveryNativeCallIsInsideTheAdapter()
    {
        var path = Path.Combine(VaultBoundary.RepoRoot(), "mobile", "BeeMemoryBank.BlindIos", "Platforms", "iOS", "IosSilenceNotifier.cs");
        var code = string.Join(Environment.NewLine, File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        var adapter = code.IndexOf("private sealed class NativeCenter", StringComparison.Ordinal);
        adapter.Should().BeGreaterThan(0);
        var outside = code[..adapter];

        outside.Should().Contain("_reconciler.UpdateAsync(").And.Contain("_reconciler.AlertsAllowedAsync(");
        foreach (var native in new[] { "AddNotificationRequest", "RemovePendingNotificationRequests", "GetPendingNotificationRequests", "RequestAuthorization" })
            outside.Should().NotContain(native, "a native call outside the adapter would not be in the ordered queue: " + native);
        outside.Should().NotContain("_next").And.NotContain("new BlindSilencePlanner").And.NotContain("Task.Run");
    }
}
