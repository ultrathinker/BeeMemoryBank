using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Busy;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>Fix round 1 of the Android head, the parts that run on any OS: the input gate (paste, scanner, link), the backup reservation, and the words of the notification.</summary>
public sealed class BlindInviteInputTests
{
    private static string Invitation(int length) => BlindInviteLinks.Scheme + new string('a', length - BlindInviteLinks.Scheme.Length);

    private sealed class FakeScanner(string? text, Exception? failure = null) : IBlindQrScanner
    {
        public Task<string?> ScanAsync(CancellationToken ct = default) =>
            failure is null ? Task.FromResult(text) : Task.FromException<string?>(failure);
    }

    [Fact]
    public void TheBoxLimit_IsTheLongestInvitationThereCanBe_AndTheXamlSaysTheSameNumber()
    {
        BlindInviteInput.MaxLength.Should().Be(BlindInvitation.MaxTextLength);
        var xaml = File.ReadAllText(Path.Combine(BeeMemoryBank.Boundary.VaultBoundary.RepoRoot(), "mobile", "BeeMemoryBank.BlindMobile", "Pages", "BlindAddPage.xaml"));

        xaml.Should().Contain($"MaxLength=\"{BlindInvitation.MaxTextLength}\"", "the Entry itself refuses what the gate would refuse");
    }

    [Fact]
    public void ATextAtTheLimit_Passes_OneCharacterOver_IsRefused()
    {
        BlindInviteInput.Accept(Invitation(BlindInviteInput.MaxLength)).Text.Should().HaveLength(BlindInviteInput.MaxLength);

        var over = BlindInviteInput.Accept(Invitation(BlindInviteInput.MaxLength + 1));

        over.Text.Should().BeNull("an over-limit text is not given to the box");
        over.Refusal.Should().Be(BlindInviteInput.TooLong);
    }

    [Fact]
    public void WhiteSpaceAroundAnInvitationThatFits_IsTrimmed_LikeAClipboardThatEndsInALineBreak()
    {
        var taken = BlindInviteInput.Accept("\r\n  " + Invitation(100) + "  \r\n");

        taken.Text.Should().Be(Invitation(100));
        taken.Refusal.Should().BeNull();
    }

    [Fact]
    public void AnInvitationThatIsTooLongOnceTheWhiteSpaceIsGone_IsRefusedToo()
    {
        var taken = BlindInviteInput.Accept("  " + Invitation(BlindInviteInput.MaxLength + 5) + "  ");

        taken.Text.Should().BeNull();
        taken.Refusal.Should().Be(BlindInviteInput.TooLong);
    }

    [Fact]
    public void AHugeText_IsRefusedWithoutBeingTrimmedOrCopied_AndTheSentenceDoesNotRepeatAnyOfIt()
    {
        // 40 million characters of white space and a secret-looking tail: trimming would allocate a new string of the tail; the gate must not look at it at all.
        var huge = new string(' ', 40_000_000) + "bmb-invite:?SECRET-TAIL";

        var taken = BlindInviteInput.Accept(huge);

        taken.Text.Should().BeNull();
        taken.Refusal.Should().Be(BlindInviteInput.TooLong).And.NotContain("SECRET-TAIL").And.NotContain("40000000");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void NothingOrBlank_IsNothing_NotARefusal(string? text) => BlindInviteInput.Accept(text).Should().Be(BlindInviteInputResult.Nothing);

    [Fact]
    public async Task ThePastePath_RefusesAnOverLimitClipboard_AndPassesAnOrdinaryOne()
    {
        var hostile = await BlindInviteInput.FromClipboardAsync(() => Task.FromResult<string?>(new string('x', 10_000_000)));
        var ordinary = await BlindInviteInput.FromClipboardAsync(() => Task.FromResult<string?>("  " + Invitation(80) + "\n"));
        var empty = await BlindInviteInput.FromClipboardAsync(() => Task.FromResult<string?>(null));

        hostile.Should().Be(new BlindInviteInputResult(null, BlindInviteInput.TooLong));
        ordinary.Should().Be(new BlindInviteInputResult(Invitation(80), null));
        empty.Should().Be(BlindInviteInputResult.Nothing);
    }

    [Fact]
    public async Task TheScannerPath_RefusesAnOverLimitCode_AndPassesAnOrdinaryOne_AndACancelledScanIsNothing()
    {
        (await BlindInviteInput.FromScannerAsync(new FakeScanner(new string('9', BlindInviteInput.MaxLength + 1))))
            .Should().Be(new BlindInviteInputResult(null, BlindInviteInput.TooLong));
        (await BlindInviteInput.FromScannerAsync(new FakeScanner(Invitation(120))))
            .Should().Be(new BlindInviteInputResult(Invitation(120), null));
        (await BlindInviteInput.FromScannerAsync(new FakeScanner(null))).Should().Be(BlindInviteInputResult.Nothing);
    }

    [Fact]
    public async Task AScannerThatCannotRun_ThrowsToThePage_WhichSaysItsOwnFixedSentence()
    {
        var scan = () => BlindInviteInput.FromScannerAsync(new FakeScanner(null, new InvalidOperationException("com.google.android.gms: module /data/user/0/x missing")));

        await scan.Should().ThrowAsync<InvalidOperationException>("the gate does not turn a failure into text: the page's catch says BlindPageSentences.ScannerUnavailable");
    }

    [Fact]
    public void TheLinkInbox_AndTheGate_AgreeOnTheLimit()
    {
        BlindInviteLinks.IsLink(Invitation(BlindInviteInput.MaxLength)).Should().BeTrue();
        BlindInviteLinks.IsLink(Invitation(BlindInviteInput.MaxLength + 1)).Should().BeFalse();
        BlindInviteInput.Accept(Invitation(BlindInviteInput.MaxLength)).Text.Should().NotBeNull();
        BlindInviteInput.Accept(Invitation(BlindInviteInput.MaxLength + 1)).Text.Should().BeNull();
    }
}

public sealed class BlindManualBackupTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ---- the state machine alone ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ASecondAccountsRequest_WhileTheFirstIsPending_IsRefusedAtOnce_WithTheFirstAccountsId()
    {
        var machine = new BlindManualBackup();

        machine.Reserve("A").Accepted.Should().BeTrue();
        var second = machine.Reserve("B");

        second.Accepted.Should().BeFalse("it would have been merged into A's run, and B's page would have waited for a backup that never came");
        second.Holder.Should().Be("A");
        second.Stopping.Should().BeFalse();
        machine.Holder.Should().Be("A");
    }

    [Fact]
    public void TheSameAccountAsked_TwiceInARow_JoinsItsOwnReservation_AndItsRun()
    {
        var machine = new BlindManualBackup();
        var owner = new object();

        var first = machine.Reserve("A");
        var second = machine.Reserve("A");
        machine.Deliver("A", owner).Kind.Should().Be(BlindDeliveryKind.Started);
        var third = machine.Reserve("A");

        second.Accepted.Should().BeTrue();
        second.Generation.Should().Be(first.Generation, "a join is the same reservation");
        third.Accepted.Should().BeTrue("the run that is going takes the join");
        third.Generation.Should().Be(first.Generation);
    }

    [Fact]
    public void ARunsEnd_ReleasesOnlyItsOwnGenerationAndOwner()
    {
        var machine = new BlindManualBackup();
        var owner = new object();
        var other = new object();
        var generation = machine.Reserve("A").Generation;
        machine.Deliver("A", owner);

        machine.End("A", generation, other).Should().BeFalse("another owner's end");
        machine.End("A", generation + 1, owner).Should().BeFalse("another generation's end");
        machine.End("B", generation, owner).Should().BeFalse("another account's end");
        machine.Holder.Should().Be("A");
        machine.End("A", generation, owner).Should().BeTrue();

        machine.Holder.Should().BeNull();
        machine.End("A", generation, owner).Should().BeFalse("a second end of the same run is nothing");
    }

    [Fact]
    public void APendingReservation_LapsesOnlyWhenAnotherAccountAsksAfterItsDeadline_ARunNever()
    {
        var clock = new Clock();
        var machine = new BlindManualBackup(clock);
        machine.Reserve("A");
        clock.Now += BlindManualBackup.PendingTimeout - TimeSpan.FromSeconds(1);
        machine.Reserve("B").Accepted.Should().BeFalse("the start may still be on its way");

        clock.Now += TimeSpan.FromSeconds(2);
        machine.Holder.Should().BeNull("a reservation past its deadline holds nobody out");

        machine.Reserve("B").Accepted.Should().BeTrue("one lost start must not lock every other account out");
        var owner = new object();
        machine.Deliver("B", owner).Kind.Should().Be(BlindDeliveryKind.Started);
        clock.Now += TimeSpan.FromHours(3);
        var third = machine.Reserve("A");

        third.Accepted.Should().BeFalse("a run that is working does not lapse");
        third.Holder.Should().Be("B");
    }

    [Fact]
    public void ALateStart_NobodyHasOvertaken_StillRuns_ButOneThatWasOvertakenFindsStaleAndTouchesNothing()
    {
        var clock = new Clock();
        var machine = new BlindManualBackup(clock);
        machine.Reserve("A");
        clock.Now += BlindManualBackup.PendingTimeout + TimeSpan.FromSeconds(5);
        var lateOwner = new object();
        var late = machine.Deliver("A", lateOwner);
        late.Kind.Should().Be(BlindDeliveryKind.Started, "nobody asked after A: it is still the person's request");
        machine.End("A", late.Generation, lateOwner).Should().BeTrue();

        var second = new BlindManualBackup(clock);
        second.Reserve("A");
        clock.Now += BlindManualBackup.PendingTimeout + TimeSpan.FromSeconds(5);
        var b = second.Reserve("B");
        b.Accepted.Should().BeTrue("A lapsed");

        var stale = second.Deliver("A", new object());

        stale.Kind.Should().Be(BlindDeliveryKind.Stale);
        stale.Holder.Should().Be("B", "the notification may name whose backup holds the phone");
        second.Holder.Should().Be("B", "the stale delivery changed nothing");
        second.Deliver("B", new object()).Kind.Should().Be(BlindDeliveryKind.Started, "B's own start is still the current pending one");
    }

    [Fact]
    public void AStartTheSystemRefused_TakesBackItsOwnRequest_UnlessAnotherRequestOfTheAccountStillWaits_OrTheRunIsGoing()
    {
        var machine = new BlindManualBackup();
        var only = machine.Reserve("A");
        machine.Withdraw("A", only.Generation);
        machine.Holder.Should().BeNull("the only request failed");

        var first = machine.Reserve("A");
        machine.Reserve("A"); // a second tap on the same account joins
        machine.Withdraw("A", first.Generation);
        machine.Holder.Should().Be("A", "the first tap's start is still on its way");

        var owner = new object();
        machine.Deliver("A", owner);
        machine.Withdraw("A", first.Generation);
        machine.Holder.Should().Be("A", "a run is never taken back by a failed request");
    }

    [Fact]
    public void ADeliveryThatIsNotTheCurrentPendingOnes_DoesNothingAndSaysSo()
    {
        var machine = new BlindManualBackup();
        var owner = new object();

        machine.Deliver("A", owner).Kind.Should().Be(BlindDeliveryKind.Stale, "nobody reserved A");
        machine.Deliver(null, owner).Kind.Should().Be(BlindDeliveryKind.Stale);
        machine.Reserve("A");
        machine.Deliver("A", owner).Kind.Should().Be(BlindDeliveryKind.Started);
        machine.Deliver("A", owner).Kind.Should().Be(BlindDeliveryKind.Joined, "a duplicate delivery to the same owner joins its run");
        machine.Deliver("A", new object()).Kind.Should().Be(BlindDeliveryKind.Busy, "an older instance's run has not ended");
        machine.Deliver("B", owner).Kind.Should().Be(BlindDeliveryKind.Busy);
        machine.Holder.Should().Be("A", "none of those touched the run");
    }

    [Fact]
    public void WhenTheOwnerIsDestroyed_EveryRequestIsRefusedAsStopping_UntilItsRunEnds()
    {
        var machine = new BlindManualBackup();
        var owner = new object();
        var generation = machine.Reserve("A").Generation;
        machine.Deliver("A", owner);

        machine.Stopping(owner);

        var same = machine.Reserve("A");
        same.Accepted.Should().BeFalse("its run is on its way out: a join would be dropped");
        same.Stopping.Should().BeTrue();
        machine.Reserve("B").Stopping.Should().BeTrue();
        machine.Deliver("A", owner).Kind.Should().Be(BlindDeliveryKind.Busy);
        machine.End("A", generation, owner).Should().BeTrue();
        machine.Reserve("B").Accepted.Should().BeTrue();
    }

    [Fact]
    public void TheRefusal_NamesTheAccountThatHoldsThePhone_OrSaysAnotherCopy_AndNeverCarriesALineBreak()
    {
        BlindManualBackup.RefusalText("Alpha").Should().Contain("The backup of Alpha is running").And.Contain("one backup at a time");
        BlindManualBackup.RefusalText("Alpha", stopping: true).Should().Contain("The backup of Alpha is stopping");
        BlindManualBackup.RefusalText(null).Should().Contain("another copy");
        BlindManualBackup.RefusalText("  ").Should().Contain("another copy");
        BlindManualBackup.RefusalText("Line\r\nBreak").Should().NotContain("\n");
    }

    // ---- one service instance over the process-wide state, interleaved with fakes -------------------------------------------------

    private sealed class FakeHost : IBlindBackupServiceHost
    {
        private readonly object _gate = new();
        public readonly List<string> Events = [];
        public readonly List<string> Ran = [];
        public readonly Dictionary<string, TaskCompletionSource> Hold = new(StringComparer.Ordinal);
        public Func<string?, bool> ThrowOnForeground = _ => false;
        public ManualResetEventSlim? ForegroundBlock;
        public bool HonourCancel;
        public int Running;
        public int MaxRunningOfOneAccount;
        private readonly Dictionary<string, int> _runningOf = new(StringComparer.Ordinal);

        public TaskCompletionSource HoldFor(string account) => Hold[account] = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ShowForeground(string? accountId)
        {
            ForegroundBlock?.Wait(TimeSpan.FromSeconds(10));
            lock (_gate) Events.Add("foreground:" + (accountId ?? "-"));
            if (ThrowOnForeground(accountId)) throw new InvalidOperationException("ForegroundServiceStartNotAllowedException: /data/user/0/secret");
        }

        public void HideForeground() { lock (_gate) Events.Add("hide"); }

        public void StopAfter(int startId) { lock (_gate) Events.Add("stop:" + startId); }

        public async Task RunAsync(string accountId, CancellationToken ct)
        {
            lock (_gate)
            {
                Ran.Add(accountId);
                _runningOf[accountId] = _runningOf.GetValueOrDefault(accountId) + 1;
                MaxRunningOfOneAccount = Math.Max(MaxRunningOfOneAccount, _runningOf[accountId]);
            }
            try
            {
                var gate = Hold.TryGetValue(accountId, out var held) ? held.Task : Task.CompletedTask;
                if (HonourCancel) await Task.WhenAny(gate, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
                else await gate.ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) _runningOf[accountId]--;
            }
        }

        public string[] Snapshot() { lock (_gate) return [.. Events]; }
    }

    private static async Task Settled(BlindManualBackupService core)
    {
        if (core.RunTask is { } task) await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AnOrdinaryRun_ShowsTheForeground_RunsOnce_ReleasesTheReservation_AndStopsTheServiceForTheLatestStartId()
    {
        var machine = new BlindManualBackup();
        var host = new FakeHost();
        var core = new BlindManualBackupService(machine, host);

        machine.Reserve("A").Accepted.Should().BeTrue();
        core.OnStart("A", 1);
        await Settled(core);

        host.Ran.Should().Equal("A");
        host.Snapshot().Should().Equal("foreground:A", "hide", "stop:1");
        machine.Holder.Should().BeNull();
    }

    [Fact]
    public async Task AFirstRunHeldPastThePendingWindow_IsStillTheHolder_TheSecondAccountIsRefused_OnlyTheFirstRuns_AndItsEndReleases()
    {
        var clock = new Clock();
        var machine = new BlindManualBackup(clock);
        var host = new FakeHost();
        var release = host.HoldFor("A");
        var core = new BlindManualBackupService(machine, host);
        machine.Reserve("A");
        core.OnStart("A", 1);
        machine.IsRunning.Should().BeTrue("A's task is in flight");

        clock.Now += BlindManualBackup.PendingTimeout + TimeSpan.FromMinutes(5);

        machine.Holder.Should().Be("A", "a run does not expire");
        var refused = machine.Reserve("B");
        refused.Accepted.Should().BeFalse("with an expiry that reached a run, B was granted the phone here and then silently merged into A's run");
        refused.Holder.Should().Be("A");
        release.SetResult();
        await Settled(core);

        host.Ran.Should().Equal("A");
        machine.Holder.Should().BeNull("A's end released its own reservation");
        machine.Reserve("B").Accepted.Should().BeTrue();
        core.OnStart("B", 2);
        await Settled(core);
        host.Ran.Should().Equal("A", "B");
    }

    [Fact]
    public async Task WhenTheForegroundStepThrowsAfterTheRunWasAccepted_TheRunIsGivenBack_AndAnotherAccountCanReserveAndRun()
    {
        var machine = new BlindManualBackup();
        var host = new FakeHost { ThrowOnForeground = account => account == "A" };
        var core = new BlindManualBackupService(machine, host);
        machine.Reserve("A").Accepted.Should().BeTrue();

        var start = () => core.OnStart("A", 1);

        start.Should().NotThrow("the platform's refusal must not take the process down");
        host.Ran.Should().BeEmpty("A's task was never started");
        machine.Holder.Should().BeNull("the claim was rolled back with exactly its generation: nothing leaks");
        host.Snapshot().Should().Contain("stop:1", "the service lets go of itself");
        machine.Reserve("B").Accepted.Should().BeTrue();
        core.OnStart("B", 2);
        await Settled(core);
        host.Ran.Should().Equal("B");
        machine.Holder.Should().BeNull("B's generation was released by B's end");
    }

    [Fact]
    public async Task AStaleStartOfA_WhileBIsPending_RunsNothing_TakesNoSlot_AndBsLaterStartRunsAndIsReleased()
    {
        var clock = new Clock();
        var machine = new BlindManualBackup(clock);
        var host = new FakeHost();
        var core = new BlindManualBackupService(machine, host);
        machine.Reserve("A");
        clock.Now += BlindManualBackup.PendingTimeout + TimeSpan.FromSeconds(1);
        var b = machine.Reserve("B");
        b.Accepted.Should().BeTrue("A's start never came within its window");

        core.OnStart("A", 1); // A's start finally arrives, stale
        core.RunTask.Should().BeNull("a stale delivery starts no task");
        host.Ran.Should().BeEmpty();
        host.Snapshot().Should().Equal("foreground:B", "hide", "stop:1");
        machine.Holder.Should().Be("B", "B's reservation is untouched");

        core.OnStart("B", 2);
        await Settled(core);

        host.Ran.Should().Equal(new[] { "B" }, "the slot was never occupied by the stale start: B really runs");
        machine.Holder.Should().BeNull("and B's generation is released");
        machine.End("B", b.Generation, core.Owner).Should().BeFalse("it already was");
    }

    [Fact]
    public async Task AStaleStartThatIsStillInsideItsForegroundAcknowledgement_DoesNotLoseTheNewerAccountsStart()
    {
        var clock = new Clock();
        var machine = new BlindManualBackup(clock);
        var host = new FakeHost { ForegroundBlock = new ManualResetEventSlim(false) };
        var core = new BlindManualBackupService(machine, host);
        machine.Reserve("A");
        clock.Now += BlindManualBackup.PendingTimeout + TimeSpan.FromSeconds(1);
        machine.Reserve("B").Accepted.Should().BeTrue();

        var stale = Task.Run(() => core.OnStart("A", 1)); // held in the foreground acknowledgement
        await Task.Delay(100);
        var newer = Task.Run(() => core.OnStart("B", 2)); // B's intent arrives meanwhile
        await Task.Delay(100);
        host.ForegroundBlock.Set();
        await Task.WhenAll(stale, newer).WaitAsync(TimeSpan.FromSeconds(10));
        await Settled(core);

        host.Ran.Should().Equal(new[] { "B" }, "B's work is bound to a task, whichever order the two starts were processed in");
        machine.Holder.Should().BeNull();
    }

    [Fact]
    public async Task TwoServiceInstances_TheFirstDestroyedWithADelayedCancellation_NeverRunOneAccountTwice_AndAnOldEndNeverReleasesTheNewOwnersGeneration()
    {
        var machine = new BlindManualBackup();
        var host1 = new FakeHost();
        var host2 = new FakeHost();
        var releaseA = host1.HoldFor("A");
        var s1 = new BlindManualBackupService(machine, host1);
        var g1 = machine.Reserve("A").Generation;
        s1.OnStart("A", 1);

        // Android destroys S1; its backup ignores the cancellation for now.
        s1.OnDestroy();
        machine.Reserve("A").Stopping.Should().BeTrue("the person is told the backup is stopping instead of a second run being started beside it");
        machine.Reserve("B").Stopping.Should().BeTrue();

        // The race path: a tap that was accepted BEFORE the destruction is delivered to the new instance.
        var s2 = new BlindManualBackupService(machine, host2);
        s2.OnStart("A", 2);
        host2.Ran.Should().BeEmpty("S2 must not start a second run of A while S1's is still there");
        host2.Snapshot().Should().Contain("stop:2", "S2 has no run of its own and lets go of itself");
        machine.Holder.Should().Be("A");

        releaseA.SetResult(); // S1's run finally ends
        await Settled(s1);

        host1.MaxRunningOfOneAccount.Should().Be(1);
        host1.Snapshot().Should().NotContain(e => e.StartsWith("stop:"), "a destroyed instance does not stop anything after its end");
        machine.Holder.Should().BeNull("S1's own end released S1's own generation");

        // B takes the phone through S2; then a LATE, duplicate end of S1's old run arrives.
        machine.Reserve("B").Accepted.Should().BeTrue();
        host2.HoldFor("B");
        s2.OnStart("B", 3);
        machine.End("A", g1, s1.Owner).Should().BeFalse("S1's old generation and owner are not B's");
        machine.Holder.Should().Be("B", "the old end did not release the new owner");
        machine.Reserve("A").Accepted.Should().BeFalse("B still holds the phone: no run of A beside B's");
        host2.Hold["B"].SetResult();
        await Settled(s2);
        machine.Holder.Should().BeNull();
    }

    [Fact]
    public async Task ADuplicateStartOfTheSameAccountToTheSameInstance_JoinsItsRun_AndTheServiceStopsForTheLatestStartIdWhenItEnds()
    {
        var machine = new BlindManualBackup();
        var host = new FakeHost();
        var release = host.HoldFor("A");
        var core = new BlindManualBackupService(machine, host);
        machine.Reserve("A");
        core.OnStart("A", 1);
        machine.Reserve("A").Accepted.Should().BeTrue();

        core.OnStart("A", 2); // the second tap's start
        host.Snapshot().Should().Equal("foreground:A", "foreground:A");
        release.SetResult();
        await Settled(core);

        host.Ran.Should().Equal(["A"], "one run");
        host.Snapshot().Should().EndWith(new[] { "hide", "stop:2" });
        machine.Holder.Should().BeNull();
    }

    [Fact]
    public async Task AStartThatFindsAnotherAccountsRunInThisInstance_DoesNotStopTheServiceUnderIt()
    {
        var machine = new BlindManualBackup();
        var host = new FakeHost();
        var release = host.HoldFor("A");
        var core = new BlindManualBackupService(machine, host);
        machine.Reserve("A");
        core.OnStart("A", 1);

        core.OnStart("B", 2); // not reserved, not A's
        host.Snapshot().Should().NotContain("stop:2", "stopSelf(2) would stop the service under A's run (2 is the latest start id)");
        release.SetResult();
        await Settled(core);

        host.Snapshot().Should().EndWith(new[] { "hide", "stop:2" });
        host.Ran.Should().Equal("A");
    }
}

public sealed class BlindNotificationTextTests
{
    [Fact]
    public void AnAccountIsNamed_ItsJobAndItsProgressFollow()
    {
        BlindNotificationText.Body("Alpha", "First load", 0.45).Should().Be($"Alpha: First load: {0.45:P0}");
        BlindNotificationText.Body("Alpha", "Backup", 0).Should().Be("Alpha: Backup");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoAccountSelected_IsTheNeutralStatus_NeverANameThatIsNotThatJobs(string? account)
    {
        BlindNotificationText.Body(account, "Backup", 0.5).Should().Be("Blind copies");
    }

    [Fact]
    public void ThePublicVersion_HoldsNoName_NoTitle_AndNoFigure()
    {
        BlindNotificationText.Public.Should().Be("Blind copies");
        BlindNotificationText.AppName.Should().NotContain("Alpha");
    }

    [Fact]
    public void ALongOrMultiLineName_IsOneShortLine()
    {
        var name = BlindNotificationText.NameOf("First line\r\nsecond line " + new string('x', 200));

        name.Should().NotContain("\n").And.NotContain("\r");
        name.Length.Should().BeLessThanOrEqualTo(63);
    }
}

public sealed class BlindPageSentencesTests
{
    private const string Hostile = @"C:\Users\x\bmb\accounts\a1\keys\bmb_blind_seed.bin: alias bmb_blind_seed_v1.a1 could not be opened (Javax.Crypto.AEADBadTagException)";

    [Fact]
    public void AFailedJobsOwnText_IsNeverSaid_ThePageSaysItsFixedSentenceAndPointsToTheLog()
    {
        var said = BlindPageSentences.JobEnd(new BlindJobOutcome(BlindJobPhase.Failed, Hostile), BlindJobWait.Backup,
            finished: "done", failed: BlindPageSentences.BackupNotFinished, ended: "ended");

        said.Should().Be(BlindPageSentences.BackupNotFinished);
        said.Should().NotContain("C:\\").And.NotContain("bmb_blind").And.NotContain("Javax").And.NotContain("alias");
    }

    [Theory]
    [InlineData(BlindJobPhase.Finished, "done")]
    [InlineData(BlindJobPhase.Failed, "failed")]
    [InlineData(BlindJobPhase.Running, "ended")]
    [InlineData(BlindJobPhase.Queued, "ended")]
    public void EveryPhase_HasItsOwnFixedSentence_WhateverTheOutcomeCarries(BlindJobPhase phase, string expected)
    {
        BlindPageSentences.JobEnd(new BlindJobOutcome(phase, Hostile), BlindJobWait.Sync, finished: "done", failed: "failed", ended: "ended").Should().Be(expected);
    }

    [Fact]
    public void AnUnreportedJob_IsToldBySharedFixedSentence_NotByTheOutcomesMessage()
    {
        var said = BlindPageSentences.JobEnd(new BlindJobOutcome(BlindJobPhase.Unreported, Hostile), BlindJobWait.FirstLoad, "done", "failed", "ended");

        said.Should().Be(BlindJobWait.UnreportedText(BlindJobWait.FirstLoad));
        said.Should().NotContain("C:\\");
    }

    [Fact]
    public void TheFixedSentences_HoldNoPlaceholdersAndNoLineBreaks()
    {
        foreach (var sentence in new[]
        {
            BlindPageSentences.StatusUnreadable, BlindPageSentences.DeviceUnreadable, BlindPageSentences.PreviewFailed, BlindPageSentences.QrNotMade, BlindPageSentences.CodeNotCopied,
            BlindPageSentences.RePairNotStarted, BlindPageSentences.ClipboardUnreadable, BlindPageSentences.ScannerUnavailable, BlindPageSentences.BackgroundProblem,
            BlindPageSentences.FirstLoadNotFinished, BlindPageSentences.SyncNotFinished, BlindPageSentences.BackupNotFinished,
        })
            sentence.Should().NotContain("{").And.NotContain("\n");
    }
}

/// <summary>
/// The REAL shared busy helper (<c>BusyAction.cs</c>, compiled into this project as it is) driven through its catch / display route with hostile exceptions, after the same one line the app runs at start
/// (<see cref="BlindFailureSetup.Apply"/>). Whatever the helper hands to the screen - a dialog's body or a result label's text - must be the closed sentence: none of the canaries, none of the words of a path, an alias, an
/// address or a library. The helper keeps its in-flight set in a static, so these tests share one class and run one after the other.
/// </summary>
[Collection("BusyActionRoute")]
public sealed class BlindBusyRouteCanaryTests
{
    private const string Canary = "canary-123";
    private const string SecretPath = @"C:\secret\bmb_blind_seed_v1";

    private static readonly string[] Needles = [Canary, "bmb_blind_", "canary-host", "token=", @"C:\", "Access to the path", "being used by another process", "https://", "Javax"];

    public static TheoryData<string> Names => new(
        "io-path", "io-inner-path", "http-uri-token", "aggregate", "crypto", "unauthorized", "invalid-operation-with-path", "invalid-operation-uri-token", "argument-with-path",
        "unknown", "wrapper-with-inner", "type-initialization", "http-wrapped-in-io", "invalid-data", "aggregate-of-invalid-operations", "target-invocation");

    private static Exception Make(string name) => name switch
    {
        "io-path" => new IOException($"The process cannot access the file '{SecretPath}' because it is being used by another process. {Canary}"),
        "io-inner-path" => new IOException("The file could not be written.", new IOException($"{SecretPath} {Canary}")),
        "http-uri-token" => new HttpRequestException($"An error occurred while sending the request to https://canary-host.invalid/api?token={Canary}", new IOException(SecretPath)),
        "aggregate" => new AggregateException($"One or more errors occurred. {Canary}", new IOException($"{SecretPath} {Canary}"), new FormatException($"bad {Canary}")),
        "crypto" => new System.Security.Cryptography.CryptographicException($"The key {SecretPath} could not be loaded. {Canary}"),
        "unauthorized" => new UnauthorizedAccessException($"Access to the path '{SecretPath}' is denied. {Canary}"),
        "invalid-operation-with-path" => new InvalidOperationException($"Could not open {SecretPath} {Canary}"),
        "invalid-operation-uri-token" => new InvalidOperationException($"https://canary-host.invalid/?token={Canary} {SecretPath}"),
        "argument-with-path" => new ArgumentException($"The path '{SecretPath}' is not valid. {Canary} (Parameter 'path')"),
        "unknown" => new NotSupportedException($"{Canary} {SecretPath} Javax.Crypto.AEADBadTagException"),
        "wrapper-with-inner" => new InvalidOperationException("The step failed.", new IOException($"{SecretPath} {Canary}")),
        "type-initialization" => new TypeInitializationException("Some.Type", new IOException($"{SecretPath} {Canary}")),
        "http-wrapped-in-io" => new IOException("The step failed.", new HttpRequestException($"https://canary-host.invalid/x?token={Canary}")),
        "invalid-data" => new InvalidDataException($"The package {SecretPath} is damaged. {Canary}"),
        "aggregate-of-invalid-operations" => new AggregateException("many", new InvalidOperationException($"https://canary-host.invalid/?token={Canary} {SecretPath}"), new ArgumentException($"account 8f3a-{Canary}")),
        "target-invocation" => new System.Reflection.TargetInvocationException(new ArgumentException($"https://canary-host.invalid/?token={Canary} {SecretPath}")),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>What the helper put on the screen for a failed action: through its real RunAsync, with a result callback in place of a label or a dialog (nothing else of the route is replaced).</summary>
    private static async Task<(bool Ran, bool? Ok, string? Text)> ShownFor(Exception failure)
    {
        bool? ok = null;
        string? text = null;
        var ran = await BusyAction.RunAsync(null, () => Task.FromException(failure), new BusyOptions { Result = (success, said) => { ok = success; text = said; } });
        return (ran, ok, text);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task EveryHostileFailure_OfAnAction_IsToldByTheClosedSentenceOnly_ThroughTheRealBusyRoute(string name)
    {
        BlindFailureSetup.Apply();

        var (ran, ok, text) = await ShownFor(Make(name));

        ran.Should().BeFalse("the action failed");
        ok.Should().BeFalse();
        text.Should().NotBeNullOrWhiteSpace();
        foreach (var needle in Needles) text.Should().NotContain(needle, $"the closed rule says a failure by its kind: {name}");
        text.Should().Be(BlindFailureText.Say(Make(name)), "what the screen got is exactly the blind core's closed text");
    }

    [Fact]
    public async Task ARefusalSentenceThePageThrewForThePerson_IsStillSaid_BecauseItIsMarkedAsTheCodesOwn()
    {
        BlindFailureSetup.Apply();

        var (_, _, text) = await ShownFor(BlindPageSentences.Refusal("Give the copy a name of 1 to 40 characters."));

        text.Should().Be("Give the copy a name of 1 to 40 characters.", "without the mark the person would read 'an unexpected problem' and not know what to change");
    }

    [Fact]
    public async Task AnUnmarkedInvalidOperation_ThatCarriesAPlace_IsNeverSaidByItsWords()
    {
        BlindFailureSetup.Apply();

        var (_, _, text) = await ShownFor(new InvalidOperationException($"Could not open {SecretPath} {Canary}"));

        text.Should().NotContain(Canary).And.NotContain("bmb_blind_");
    }

    [Fact]
    public async Task ASuccessAndACancelledAction_ShowNothingOfAnException()
    {
        BlindFailureSetup.Apply();
        string? shown = null;
        var cancelled = await BusyAction.RunAsync(null, () => Task.FromCanceled(new CancellationToken(true)), new BusyOptions { Result = (_, said) => shown = said });
        var ok = await BusyAction.RunAsync(null, () => Task.CompletedTask, new BusyOptions { Result = (_, said) => shown = said });

        cancelled.Should().BeFalse("a stopped action is not reported");
        ok.Should().BeTrue();
        shown.Should().BeNull();
    }

    [Fact]
    public async Task WithoutTheSetup_TheSharedHelpersDefaultWouldShowTheWords_WhichIsWhyTheAppSetsItFirstThing()
    {
        // The proof that the canary above is sensitive: the same route with no Describer (what an app that forgot the setup gets) says the local failure's own text.
        var before = BusyAction.Describer;
        BusyAction.Describer = null;
        try
        {
            var (_, _, text) = await ShownFor(new IOException($"cannot read {SecretPath} {Canary}"));

            text.Should().Contain(Canary, "the default story of the helper is the text of a local failure");
        }
        finally
        {
            BusyAction.Describer = before;
        }
    }
}

[CollectionDefinition("BusyActionRoute", DisableParallelization = true)]
public sealed class BusyActionRouteCollection;
