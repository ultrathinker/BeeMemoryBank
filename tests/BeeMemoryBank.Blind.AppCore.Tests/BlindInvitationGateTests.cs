using System.Collections.Concurrent;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Fix round 1, MAJOR-3 to MAJOR-6. Every parse of an invitation text has an owner that wipes the secret on every way out; a removal of an account ends what the account asks of the hub and
/// lets go of its secret BEFORE anything of it is cleared (a pending 503 retry, a redeem in flight, an add that is still setting it up); the whole add and every ask of the hub are operations
/// of the host, so that a disposal waits for them (and cancels them) before it closes anything under them; and the account is never made again behind its removal.
/// </summary>
public sealed class BlindInvitationGateTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static async Task<(InviteRig Rig, FakeInviteHub Hub, BlindAccountHost Host)> StartedAsync(IBlindCrashPoints? crash = null, int maxAccounts = BlindAccountHost.DefaultMaxAccounts)
    {
        var rig = new InviteRig();
        var hub = rig.Hub();
        var host = rig.Host(crash: crash, maxAccounts: maxAccounts);
        await host.StartAsync();
        return (rig, hub, host);
    }

    /// <summary>
    /// A second host over the same folder cannot take it: the host that is being disposed has not given the root back. The attempt waits for the root as long as a start does (the rig's lock wait), so a
    /// disposal that did not wait for the work it must wait for - and so gave the root back at once - is caught here, whatever the thread pool does.
    /// </summary>
    private static async Task TheRootIsStillHeldAsync(InviteRig rig, string because)
    {
        await using var other = rig.Host();
        await other.StartAsync();
        other.GetSnapshot().RegistryProblem.Should().NotBeNull(because);
    }

    private static void NothingOfTheAccountIsLeft(InviteRig rig, BlindAccountHost host, BlindAccountEntry entry)
    {
        host.GetSnapshot().Accounts.Should().BeEmpty("the account was removed and is not listed again");
        Directory.Exists(BlindAccountNames.DataDirectory(rig.Accounts.Root, entry)).Should().BeFalse("its folder was deleted and not made again");
        rig.Accounts.States.HasState(entry).Should().BeFalse("its state was erased and not written again");
        foreach (var role in Enum.GetValues<BlindAccountNames.Role>())
            rig.Accounts.Secrets.ReadBlob(entry, role).Should().BeNull($"its {role} key was cleared and not made again");
        host.HeldRedemptions.Should().Be(0, "no secret is held for an account that is gone");
    }

    // ---- every parse is wiped on every way out (MAJOR-3) --------------------------------------------------------------------------------------

    private static void AllWiped(ConcurrentBag<byte[]> seen, int expectedAtLeast)
    {
        seen.Count.Should().BeGreaterThanOrEqualTo(expectedAtLeast, "the parse was observed");
        foreach (var secret in seen) secret.Should().OnlyContain(b => b == 0, "a parsed copy of the secret does not outlive the call that parsed it");
    }

    [Fact]
    public async Task APreviewOfAnInvitationThatCanBeAdded_WipesItsParsedSecret()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var seen = new ConcurrentBag<byte[]>();
        host.ParsedSecretObserver = seen.Add;

        host.PreviewInvitation(hub.IssueText()).CanAdd.Should().BeTrue();

        AllWiped(seen, 1);
    }

    [Fact]
    public async Task APreviewThatRefusesAnExpiredInvitation_WipesItsParsedSecret()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var seen = new ConcurrentBag<byte[]>();
        host.ParsedSecretObserver = seen.Add;
        var text = hub.IssueText(lifetime: TimeSpan.FromSeconds(5));
        rig.Clock.Advance(TimeSpan.FromSeconds(10));

        host.PreviewInvitation(text).RefusalKind.Should().Be(BlindInvitationRefusals.Expired);

        AllWiped(seen, 1);
    }

    [Fact]
    public async Task APreviewThatRefusesBecauseTheDeviceIsFull_OrTheHubIsAlreadyHere_WipesItsParsedSecret()
    {
        var (rig, hub, host) = await StartedAsync(maxAccounts: 1);
        using var _ = rig;
        await using var __ = host;
        var seen = new ConcurrentBag<byte[]>();
        host.ParsedSecretObserver = seen.Add;
        (await host.AddFromInvitationAsync(hub.IssueText())).Ok.Should().BeTrue();
        seen.Clear();

        host.PreviewInvitation(hub.IssueText()).RefusalKind.Should().BeOneOf(BlindInvitationRefusals.TooMany, BlindInvitationRefusals.AlreadyHere);
        host.PreviewInvitation(rig.Hub("hub-b.test").IssueText()).RefusalKind.Should().Be(BlindInvitationRefusals.TooMany);

        AllWiped(seen, 2);
    }

    [Fact]
    public async Task APreviewBeforeTheHostHasStarted_WipesItsParsedSecret()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        var seen = new ConcurrentBag<byte[]>();
        host.ParsedSecretObserver = seen.Add;

        host.PreviewInvitation(hub.IssueText()).RefusalKind.Should().Be(BlindInvitationRefusals.Blocked);

        AllWiped(seen, 1);
    }

    [Fact]
    public async Task AnAddThatSucceeds_WipesBothParsesOfItsSecret_TheOneItKeepsAndTheOneInsideTheGate()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var seen = new ConcurrentBag<byte[]>();
        host.ParsedSecretObserver = seen.Add;

        (await host.AddFromInvitationAsync(hub.IssueText())).Ok.Should().BeTrue();

        AllWiped(seen, 2);
        host.HeldRedemptions.Should().Be(0);
    }

    [Fact]
    public async Task AnAddThatIsRefused_AtEachPlaceItCanBe_WipesItsParsedSecret()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var seen = new ConcurrentBag<byte[]>();
        host.ParsedSecretObserver = seen.Add;

        // refused by the preview part: this hub is already here
        (await host.AddFromInvitationAsync(hub.IssueText())).Ok.Should().BeTrue();
        (await host.AddFromInvitationAsync(hub.IssueText())).Ok.Should().BeFalse();
        // refused after it: a negative limit
        var other = rig.Hub("hub-b.test");
        (await host.AddFromInvitationAsync(other.IssueText(), new BlindAccountSettings(new BlindAccountLimits(MaxStoredBytes: -1), false, false))).Ok.Should().BeFalse();
        // refused by the hub: it answers as another computer, so the account is taken away again
        var impostor = rig.Hub("hub-c.test");
        impostor.IdentityNodeId = Guid.NewGuid();
        (await host.AddFromInvitationAsync(impostor.IssueText())).Ok.Should().BeFalse();
        // not an invitation at all
        (await host.AddFromInvitationAsync("bmb-invite:?v=1&nonsense")).Ok.Should().BeFalse();

        AllWiped(seen, 4);
        host.HeldRedemptions.Should().Be(0);
    }

    // ---- a removal ends the account's invitation work first (MAJOR-4) -------------------------------------------------------------------------

    [Fact]
    public async Task RemovingAPendingAccount_WhileAFiveHundredAndThreeRetryIsPending_LetsGoOfTheSecret_AndNothingIsAskedAgain()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        hub.Enqueue("redeem", new HubFault.Status(503, RetryAfterSeconds: 3600, Error: "locked"));
        var added = await host.AddFromInvitationAsync(hub.IssueText());
        added.Ok.Should().BeTrue(added.Error);
        var entry = host.CardOf(added.AccountId!).Entry;
        entry.Phase.Should().Be(BlindAccountPhases.Pending);
        host.HeldRedemptions.Should().Be(1, "the secret waits in memory for the retry");

        await host.RemoveAsync(entry.Id);

        NothingOfTheAccountIsLeft(rig, host, entry);
        rig.Clock.Advance(TimeSpan.FromHours(2));
        await host.PollInvitationsAsync(force: true);
        hub.Count("redeem").Should().Be(1, "no retry came after the removal");
    }

    [Fact]
    public async Task RemovingAnAccountWhileItsRedeemIsInFlight_CancelsTheCall_AndTheAccountIsNotMadeAgain()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var hold = hub.HoldNext("redeem");
        var addTask = Task.Run(() => host.AddFromInvitationAsync(hub.IssueText()));
        await hold.Entered.WaitAsync(Patience);
        var entry = host.GetSnapshot().Accounts.Single().Entry;
        host.HeldRedemptions.Should().Be(1);

        await host.RemoveAsync(entry.Id).WaitAsync(Patience);
        var added = await addTask.WaitAsync(Patience);

        added.Ok.Should().BeFalse();
        NothingOfTheAccountIsLeft(rig, host, entry);
    }

    [Fact]
    public async Task RemovingAnAccountWhileAStatusRequestIsInFlight_CancelsTheCall_AndNothingIsWrittenAfterwards()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue();
        var added = await host.AddFromInvitationAsync(invitation.ToWireText());
        added.Ok.Should().BeTrue(added.Error);
        var entry = host.CardOf(added.AccountId!).Entry;
        hub.Confirm(invitation);   // the owner confirmed: the next status answers "confirmed", and applying it would write a call code
        var hold = hub.HoldNext("status");
        var poll = Task.Run(() => host.PollInvitationsAsync(force: true));
        await hold.Entered.WaitAsync(Patience);

        await host.RemoveAsync(entry.Id).WaitAsync(Patience);
        hold.Release();
        await poll.WaitAsync(Patience);

        NothingOfTheAccountIsLeft(rig, host, entry);
    }

    // ---- an add and a removal of the same account (MAJOR-5) ---------------------------------------------------------------------------------

    [Fact]
    public async Task ARemovalThatComesWhileTheAddIsPausedAfterTheRegistryWrite_WaitsForTheAdd_AndLeavesNothingBehind()
    {
        var pause = new PauseAtStep("invite:context-saved");
        var probe = new StepProbe();
        var (rig, hub, host) = await StartedAsync(crash: new AllOfCrashPoints(pause, probe));
        using var _ = rig;
        await using var __ = host;
        var addTask = Task.Run(() => host.AddFromInvitationAsync(hub.IssueText("Alex")));
        await pause.Reached.WaitAsync(Patience);
        var entry = host.GetSnapshot().Accounts.Single().Entry;
        entry.Phase.Should().Be(BlindAccountPhases.Pending, "the account is listed and the add is paused");

        var removal = host.RemoveAsync(entry.Id);
        await probe.Reached("remove:marked").WaitAsync(Patience);   // a deterministic point: the removal has marked the account and goes on to wait for the add, which is still paused
        await probe.AssertNotReachedAsync("remove:drained", TimeSpan.FromMilliseconds(300), "the removal waits for the add: nothing the add does may come after the cleanup");
        removal.IsCompleted.Should().BeFalse();
        probe.Note("add released");
        pause.Release();
        var added = await addTask.WaitAsync(Patience);
        await removal.WaitAsync(Patience);
        probe.Steps.Should().ContainInOrder("remove:marked", "add released", "remove:drained");

        added.Ok.Should().BeFalse();
        added.Error.Should().Contain("removed");
        NothingOfTheAccountIsLeft(rig, host, entry);
        hub.Calls.Should().BeEmpty("the add did not go on to ask the hub for an account that is gone");
    }

    [Fact]
    public async Task ARemovalThatComesWhileTheAddIsPausedAfterTheRuntimeStarted_IsWaitedFor_AndTheRuntimeIsClosed()
    {
        var pause = new PauseAtStep("invite:started");
        var (rig, hub, host) = await StartedAsync(crash: pause);
        using var _ = rig;
        await using var __ = host;
        var addTask = Task.Run(() => host.AddFromInvitationAsync(hub.IssueText("Alex")));
        await pause.Reached.WaitAsync(Patience);
        var entry = host.GetSnapshot().Accounts.Single().Entry;

        var removal = host.RemoveAsync(entry.Id);
        pause.Release();
        await Task.WhenAll(addTask, removal).WaitAsync(Patience);

        NothingOfTheAccountIsLeft(rig, host, entry);
        hub.Count("redeem").Should().Be(0, "the redemption was never sent for an account that was being removed");
    }

    [Fact]
    public async Task ARemovalThatIsCalledButHasNotMarkedTheAccountYet_StillStopsAnAddThatGoesOn_FromRedeeming()
    {
        // The window between the call of RemoveAsync and the mark it writes: the removal is held at its first step, so that the add - released now - finds the account still listed as it was.
        // The call alone must be enough: nothing may leave for the hub of an account whose removal has been asked for.
        var addPause = new PauseAtStep("invite:started");
        var removalPause = new PauseAtStep("remove:start");
        var (rig, hub, host) = await StartedAsync(crash: new AllOfCrashPoints(addPause, removalPause));
        using var _ = rig;
        await using var __ = host;
        var addTask = Task.Run(() => host.AddFromInvitationAsync(hub.IssueText("Alex")));
        await addPause.Reached.WaitAsync(Patience);
        var entry = host.GetSnapshot().Accounts.Single().Entry;

        var removal = host.RemoveAsync(entry.Id);
        await removalPause.Reached.WaitAsync(Patience);
        host.GetSnapshot().Accounts.Single().Entry.Phase.Should().Be(BlindAccountPhases.Pending, "the removal has been called and has marked nothing yet");
        addPause.Release();
        var added = await addTask.WaitAsync(Patience);

        hub.Count("redeem").Should().Be(0, "the redemption was not sent after the removal was asked for, marked or not");
        added.Ok.Should().BeFalse();
        removalPause.Release();
        await removal.WaitAsync(Patience);
        NothingOfTheAccountIsLeft(rig, host, entry);
    }

    // ---- a removal whose first registry write fails (review D2) ------------------------------------------------------------------------------

    [Fact]
    public async Task ARemovalWhoseMarkFailsOnce_IsTriedAgain_AndTheAddItStoppedLeavesNothingBehind()
    {
        var pause = new PauseAtStep("invite:context-saved");
        var failing = new FailRegistryWrites();
        var (rig, hub, host) = await StartedAsync(crash: new AllOfCrashPoints(pause, failing));
        using var _ = rig;
        await using var __ = host;
        var addTask = Task.Run(() => host.AddFromInvitationAsync(hub.IssueText("Alex")));
        await pause.Reached.WaitAsync(Patience);
        var entry = host.GetSnapshot().Accounts.Single().Entry;

        failing.FailNext(1);                 // the first registry write of the removal - the mark - fails, the second goes through
        var removal = host.RemoveAsync(entry.Id);
        await failing.Failed.WaitAsync(Patience);
        pause.Release();                     // the add has been cancelled by the removal's call; it ends with "removed before it was set up"
        var added = await addTask.WaitAsync(Patience);
        await removal.WaitAsync(Patience);   // before the fix: IOException, and the account stayed listed as pending with nothing to serve it

        added.Ok.Should().BeFalse();
        added.Error.Should().Contain("removed");
        NothingOfTheAccountIsLeft(rig, host, entry);
        hub.Count("redeem").Should().Be(0);
    }

    [Fact]
    public async Task ARemovalWhoseMarkFailsOnce_IsTriedAgain_ForTheOldWaysAddToo()
    {
        var pause = new PauseAtStep("add:registered");
        var failing = new FailRegistryWrites();
        var (rig, _, host) = await StartedAsync(crash: new AllOfCrashPoints(pause, failing));
        using var _ = rig;
        await using var __ = host;
        var addTask = Task.Run(() => host.AddAsync("Old way"));
        await pause.Reached.WaitAsync(Patience);
        var entry = host.GetSnapshot().Accounts.Single().Entry;

        failing.FailNext(1);
        var removal = host.RemoveAsync(entry.Id);
        await failing.Failed.WaitAsync(Patience);
        pause.Release();
        var added = await addTask.WaitAsync(Patience);
        await removal.WaitAsync(Patience);

        added.Error.Should().Contain("removed");
        NothingOfTheAccountIsLeft(rig, host, entry);
    }

    [Fact]
    public async Task ARemovalWhoseMarkCannotBeWritten_FinishesTheLastStepsOfTheAddItStopped_WhenItsRuntimeWasUp()
    {
        var pause = new PauseAtStep("invite:started");
        var failing = new FailRegistryWrites();
        var (rig, hub, host) = await StartedAsync(crash: new AllOfCrashPoints(pause, failing));
        using var _ = rig;
        await using var __ = host;
        var addTask = Task.Run(() => host.AddFromInvitationAsync(hub.IssueText("Alex")));
        await pause.Reached.WaitAsync(Patience);
        var entry = host.GetSnapshot().Accounts.Single().Entry;

        failing.FailNext(BlindAccountHost.MarkAttempts);      // the registry stays unwritable for every try the removal makes
        var removal = host.RemoveAsync(entry.Id);
        await failing.Exhausted.WaitAsync(Patience);
        pause.Release();                                      // the add sees the cancellation after its runtime came up and ends without the last steps
        (await addTask.WaitAsync(Patience)).Ok.Should().BeFalse();
        var failure = await Assert.ThrowsAsync<IOException>(() => removal.WaitAsync(Patience));

        failure.Message.Should().Contain("registry file is held");
        host.CardOf(entry.Id).Entry.Phase.Should().Be(BlindAccountPhases.Pending, "nothing was marked: the account is what it was");
        host.CardOf(entry.Id).Problem.Should().BeNull("the account goes on: the removal did the add's last steps");
        (await host.PollInvitationsAsync(force: true)).Should().Be(1);
        hub.Count("redeem").Should().Be(1, "the account is asked about like any other: it is not an orphan that nothing serves");
        host.EntryOf(entry.Id).Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);

        failing.Stop();
        await host.RemoveAsync(entry.Id).WaitAsync(Patience);
        NothingOfTheAccountIsLeft(rig, host, entry);
    }

    [Fact]
    public async Task ARemovalWhoseMarkCannotBeWritten_SaysSoOnTheCard_WhenTheAddIsStoppedBeforeItsRuntimeIsUp()
    {
        var pause = new PauseAtStep("invite:context-saved");
        var failing = new FailRegistryWrites();
        var (rig, hub, host) = await StartedAsync(crash: new AllOfCrashPoints(pause, failing));
        using var _ = rig;
        await using var __ = host;
        var addTask = Task.Run(() => host.AddFromInvitationAsync(hub.IssueText("Alex")));
        await pause.Reached.WaitAsync(Patience);
        var entry = host.GetSnapshot().Accounts.Single().Entry;

        failing.FailNext(BlindAccountHost.MarkAttempts);
        var removal = host.RemoveAsync(entry.Id);
        await failing.Exhausted.WaitAsync(Patience);
        pause.Release();
        await addTask.WaitAsync(Patience);
        await Assert.ThrowsAsync<IOException>(() => removal.WaitAsync(Patience));

        var card = host.CardOf(entry.Id);
        card.Entry.Phase.Should().Be(BlindAccountPhases.Pending, "nothing was marked: the account is what it was");
        card.Problem.Should().NotBeNull("the add ended without a runtime: the card must not go on saying that it connects");
        BlindAccountCard.Build(card, TimeZoneInfo.Utc, rig.Clock.GetUtcNow()).Kind.Should().Be(BlindCardKind.Problem);

        failing.Stop();
        await host.RemoveAsync(entry.Id).WaitAsync(Patience);
        NothingOfTheAccountIsLeft(rig, host, entry);
        hub.Count("redeem").Should().Be(0);
    }

    [Fact]
    public async Task AFailedMark_LeavesAnAddThatHadAlreadyFinished_AsItWas_WithNoProblemOnTheCard()
    {
        var failing = new FailRegistryWrites();
        var (rig, hub, host) = await StartedAsync(crash: failing);
        using var _ = rig;
        await using var __ = host;
        var added = await host.AddFromInvitationAsync(hub.IssueText());
        added.Ok.Should().BeTrue(added.Error);
        var id = added.AccountId!;

        failing.FailNext(int.MaxValue);
        await Assert.ThrowsAsync<IOException>(() => host.RemoveAsync(id).WaitAsync(Patience));

        host.CardOf(id).Problem.Should().BeNull("an add that finished its setup has nothing cut short: the removal simply did not happen");
        host.CardOf(id).Entry.Phase.Should().NotBe(BlindAccountPhases.Deleting);
        failing.Stop();
        await host.RemoveAsync(id).WaitAsync(Patience);
        host.GetSnapshot().Accounts.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAddThatFindsItsAccountRemovedBeforeItsRuntimeIsComposed_ComposesNothing()
    {
        // The window the review described: the removal has run to its end, and then the add goes on to compose a runtime from the entry it still holds.
        var pause = new PauseAtStep("invite:context-saved");
        var (rig, hub, host) = await StartedAsync(crash: pause);
        using var _ = rig;
        await using var __ = host;
        var addTask = Task.Run(() => host.AddFromInvitationAsync(hub.IssueText("Alex")));
        await pause.Reached.WaitAsync(Patience);
        var entry = host.GetSnapshot().Accounts.Single().Entry;
        var removal = host.RemoveAsync(entry.Id);
        pause.Release();

        await Task.WhenAll(addTask, removal).WaitAsync(Patience);

        NothingOfTheAccountIsLeft(rig, host, entry);
    }

    [Fact]
    public async Task AddsWhileTheSchedulerRuns_EachAccountRedeemsOnce_AndNoAskCollidesWithAnAdd()
    {
        using var rig = new InviteRig();
        var hubs = new[] { rig.Hub("hub-a.test"), rig.Hub("hub-b.test"), rig.Hub("hub-c.test") };
        await using var host = rig.Host();
        await host.StartAsync();
        using var stop = new CancellationTokenSource();
        var scheduler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await host.PollInvitationsAsync(force: true);
                await Task.Delay(1);
            }
        });

        var results = new List<BlindAddResult>();
        foreach (var hub in hubs) results.Add(await host.AddFromInvitationAsync(hub.IssueText()));
        stop.Cancel();
        await scheduler.WaitAsync(Patience);

        results.Should().OnlyContain(r => r.Ok, string.Join("; ", results.Select(r => r.Error)));
        host.GetSnapshot().Accounts.Should().HaveCount(3).And.OnlyContain(a => a.Entry.Phase == BlindAccountPhases.AwaitingConfirmation);
        hubs.Should().OnlyContain(h => h.Count("redeem") == 1, "an add and the scheduler never redeem the same account twice");
        host.HeldRedemptions.Should().Be(0);
    }

    // ---- the disposal waits for the invitation work, and cancels it (MAJOR-6) -----------------------------------------------------------------

    [Theory]
    [InlineData("identity")]
    [InlineData("redeem")]
    public async Task DisposingTheHost_WhileAnAddAwaitsTheHub_CancelsTheAsk_AndEndsTheAddBeforeItReturns(string route)
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        var hold = hub.HoldNext(route);
        var addTask = Task.Run(() => host.AddFromInvitationAsync(hub.IssueText()));
        await hold.Entered.WaitAsync(Patience);

        await host.DisposeAsync().AsTask().WaitAsync(Patience);

        addTask.IsCompleted.Should().BeTrue("the disposal waited for the add: it is an operation of the host");
        var added = await addTask;   // no exception: nothing was closed under the call that was running
        added.Ok.Should().BeFalse();
        host.ShutdownIncomplete.Should().BeFalse();
        host.HeldRedemptions.Should().Be(0, "the secrets were let go only when the work was over");
        await using var next = rig.Host();
        await next.StartAsync();
        next.GetSnapshot().RegistryProblem.Should().BeNull("the root was given back: another host takes it");
    }

    [Fact]
    public async Task DisposingTheHost_WhileAStatusRequestAwaitsTheHub_CancelsTheAsk_AndEndsThePollBeforeItReturns()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        var added = await host.AddFromInvitationAsync(hub.IssueText());
        added.Ok.Should().BeTrue(added.Error);
        var hold = hub.HoldNext("status");
        var poll = Task.Run(() => host.PollInvitationsAsync(force: true));
        await hold.Entered.WaitAsync(Patience);

        await host.DisposeAsync().AsTask().WaitAsync(Patience);

        poll.IsCompleted.Should().BeTrue("the disposal waited for the ask");
        await poll;   // no exception
        host.ShutdownIncomplete.Should().BeFalse();
        await using var next = rig.Host();
        await next.StartAsync();
        next.GetSnapshot().Accounts.Should().ContainSingle("the account is still listed: a disposal is not a removal");
    }

    [Fact]
    public async Task DisposingTheHost_WaitsForAnAddThatIsInTheMiddleOfAStepItCannotBeCancelledAt_AndClosesNothingUnderIt()
    {
        var pause = new PauseAtStep("invite:started");
        var (rig, hub, host) = await StartedAsync(crash: pause);
        using var _ = rig;
        var addTask = Task.Run(() => host.AddFromInvitationAsync(hub.IssueText()));
        await pause.Reached.WaitAsync(Patience);   // the runtime is up; the add holds it and goes on to ask the hub

        var disposal = host.DisposeAsync().AsTask();

        await TheRootIsStillHeldAsync(rig, "the add is an operation of the host: the disposal waits for it before it closes the runtime it is using and gives the root back");
        disposal.IsCompleted.Should().BeFalse();
        pause.Release();
        await disposal.WaitAsync(Patience);
        var added = await addTask.WaitAsync(Patience);   // no exception: the runtime was not closed beneath the add
        added.Ok.Should().BeFalse();
        host.ShutdownIncomplete.Should().BeFalse();
        host.HeldRedemptions.Should().Be(0);
        hub.Count("redeem").Should().Be(0, "nothing was redeemed after the host began to close");
    }

    [Fact]
    public async Task DisposingTheHost_WaitsForAnAskOfTheScheduler_ThatIsInTheMiddleOfAStep()
    {
        var pause = new PauseAtStep("invite:probed");
        var (rig, hub, host) = await StartedAsync(crash: pause);
        using var _ = rig;
        hub.Enqueue("identity", new HubFault.Unreachable());   // the first try (the add's own) fails at the probe, so the step is first reached by the scheduler's ask
        var added = await host.AddFromInvitationAsync(hub.IssueText());
        added.Ok.Should().BeTrue(added.Error);
        host.EntryOf(added.AccountId!).Phase.Should().Be(BlindAccountPhases.Pending);
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        var poll = Task.Run(() => host.PollInvitationsAsync(force: true));
        await pause.Reached.WaitAsync(Patience);

        var disposal = host.DisposeAsync().AsTask();

        await TheRootIsStillHeldAsync(rig, "an ask of the hub is an operation of the host: the disposal waits for it before it gives the root back");
        disposal.IsCompleted.Should().BeFalse();
        pause.Release();
        await disposal.WaitAsync(Patience);
        await poll.WaitAsync(Patience);   // no exception
        host.ShutdownIncomplete.Should().BeFalse();
        host.HeldRedemptions.Should().Be(0, "the secret is wiped when the work is over, with the root");
        hub.Count("redeem").Should().Be(0);
    }

    [Fact]
    public async Task AHostThatIsClosing_TakesNoNewAccount_AndAsksNothing()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await host.DisposeAsync();

        var added = await host.AddFromInvitationAsync(hub.IssueText());
        var waiting = await host.PollInvitationsAsync(force: true);

        added.Ok.Should().BeFalse();
        hub.Calls.Should().BeEmpty();
        waiting.Should().Be(0);
    }
}
