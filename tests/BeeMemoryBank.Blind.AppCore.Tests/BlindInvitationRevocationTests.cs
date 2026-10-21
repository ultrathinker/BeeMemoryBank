using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// After the owner confirmed: the owner removes the device (the source answers 401 to a device it had synced), the friend removes the account (the source is told when the device is online,
/// before the keys go), and the old way of pairing stays a second way in, per account.
/// </summary>
public sealed class BlindInvitationRevocationTests
{
    /// <summary>An account added from an invitation, confirmed by the owner, with its first load done (what a first load leaves), ready to sync.</summary>
    internal static async Task<string> ActiveAsync(BlindAccountHost host, FakeInviteHub hub, string name = "Alex")
    {
        var invitation = hub.Issue(name);
        var added = await host.AddFromInvitationAsync(invitation.ToWireText());
        added.Ok.Should().BeTrue(added.Error);
        hub.Confirm(invitation);
        await host.PollInvitationsAsync(force: true);
        host.EntryOf(added.AccountId!).Phase.Should().Be(BlindAccountPhases.Active);
        await host.SeedHubAsync(added.AccountId!, hub.Sync);
        return added.AccountId!;
    }

    /// <summary>
    /// The source's decision takes two refusals on two different tries (<see cref="BlindSourceRemoval.Judge"/>): the round is made, one normal retry interval passes, and it is made again. The answer of the second.
    /// </summary>
    private static async Task<string> RefusedTwiceAsync(InviteRig rig, Func<Task<string>> round)
    {
        await round();
        rig.Clock.Advance(BlindSourceRemoval.MinimumGap);
        return await round();
    }

    internal static Guid NodeOf(InviteRig rig, BlindAccountHost host, string id) => new BlindPhoneState(rig.Accounts.States.For(host.EntryOf(id))).NodeId!.Value;

    // ---- the owner removed the device --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhenTheSourceRefusesADeviceItHadSynced_TheAccountSaysItWasRemoved_AndTheRoundIsNotRetried()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        (await host.For(id).RequestSyncAsync()).Should().Be("Synced.");

        hub.Revoke(NodeOf(rig, host, id));
        var result = await RefusedTwiceAsync(rig, () => host.For(id).RequestSyncAsync());

        result.Should().Be("The source no longer accepts this device. The copy stays until you remove it.");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
        host.StateLineOf(id, rig.Clock).Should().Be("Alex removed this device from the network. The copy stays until you remove it.");
        BlindAccountCard.Build(host.CardOf(id), TimeZoneInfo.Utc, rig.Clock.GetUtcNow()).Kind.Should().Be(BlindCardKind.Problem);
        BlindAccountCard.Build(host.CardOf(id), TimeZoneInfo.Utc, rig.Clock.GetUtcNow()).CanSyncNow.Should().BeFalse();
        host.For(id).GetStatus().LastFailure.Should().BeNull("it is not a failure to retry");
        // the data and the keys stay: the copy is the friend's until he removes it
        (await host.FolderNamesAsync(id)).Should().NotBeNull();
        new BlindPhoneState(rig.Accounts.States.For(host.EntryOf(id))).CallCode.Should().NotBeNull();
    }

    [Fact]
    public async Task ARemovedAccount_IsNotSynced_BySchedulerPasses_NoRetryStorm()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        await using var scheduler = new BlindDeviceScheduler(host, rig.Clock);
        hub.Revoke(NodeOf(rig, host, id));
        await RefusedTwiceAsync(rig, () => host.For(id).RequestSyncAsync());
        var authenticated = hub.Count("authenticate");

        for (var pass = 0; pass < 6; pass++)
        {
            rig.Clock.Advance(TimeSpan.FromMinutes(16));
            await scheduler.TickAsync();
        }

        hub.Count("authenticate").Should().Be(authenticated, "a removed account is left alone");
    }

    [Fact]
    public async Task WhenOneSourceRemovesTheDevice_TheOtherAccountSyncsAsBefore()
    {
        using var rig = new InviteRig();
        var hubOne = rig.Hub("hub-one.test");
        var hubTwo = rig.Hub("hub-two.test");
        await using var host = rig.Host();
        await host.StartAsync();
        var one = await ActiveAsync(host, hubOne, "One");
        var two = await ActiveAsync(host, hubTwo, "Two");
        hubOne.Sync.AddFolder();
        hubTwo.Sync.AddFolder();

        hubOne.Revoke(NodeOf(rig, host, one));
        (await RefusedTwiceAsync(rig, () => host.For(one).RequestSyncAsync())).Should().Contain("no longer accepts");
        (await host.For(two).RequestSyncAsync()).Should().Be("Synced.");

        host.EntryOf(one).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
        host.EntryOf(two).Phase.Should().Be(BlindAccountPhases.Active);
        (await host.FolderNamesAsync(two)).Should().Contain(hubTwo.Sync.Canary).And.NotContain(hubOne.Sync.Canary);
        (await host.FolderNamesAsync(one)).Should().NotContain(hubTwo.Sync.Canary, "a note written on the other hub never reaches this account");
    }

    [Theory]
    [InlineData("unreachable")]
    [InlineData("timeout")]
    [InlineData("server error")]
    public async Task AFailureThatIsNotARefusal_DoesNotMarkTheAccountRemoved(string failure)
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        hub.Enqueue("challenge", failure switch
        {
            "unreachable" => new HubFault.Unreachable(),
            "timeout" => new HubFault.TimeOut(),
            _ => new HubFault.Status(500),
        });

        var result = await host.For(id).RequestSyncAsync();

        result.Should().StartWith("Sync failed");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
    }

    [Fact]
    public async Task APersonCanTryAgain_AndTheNextRoundDecides()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        var node = NodeOf(rig, host, id);
        hub.Revoke(node);
        await RefusedTwiceAsync(rig, () => host.For(id).RequestSyncAsync());
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);

        host.Reconnect(id).Should().BeNull();
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
        (await host.For(id).RequestSyncAsync()).Should().Be(BlindSourceRemoval.FirstRefusalSentence, "trying again starts from nothing: the first refusal is only remembered");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
        (await RefusedTwiceAsync(rig, () => host.For(id).RequestSyncAsync())).Should().Contain("no longer accepts", "still revoked: the rounds decide");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);

        hub.Unrevoke(node);
        host.Reconnect(id).Should().BeNull();
        (await host.For(id).RequestSyncAsync()).Should().Be("Synced.");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
    }

    [Fact]
    public async Task Reconnect_IsOnlyForAnAccountTheSourceRemoved()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);

        host.Reconnect(id).Should().Be("This account was not removed by its owner.");
        host.Reconnect("nope").Should().Be("There is no such account on this device (any more).");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
    }

    [Fact]
    public async Task AnAccountPairedTheOldWay_IsMarkedToo_AndSaysTheOwner()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var old = (await host.AddAsync("My copy")).AccountId!;
        await host.PairAsync(rig.Accounts, old, hub.Sync);
        var node = NodeOf(rig, host, old);
        hub.Accept(node);
        (await host.For(old).RequestSyncAsync()).Should().Be("Synced.");

        hub.Revoke(node);
        await RefusedTwiceAsync(rig, () => host.For(old).RequestSyncAsync());

        host.EntryOf(old).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
        host.StateLineOf(old, rig.Clock).Should().Be("The owner removed this device from the network. The copy stays until you remove it.");
    }

    // ---- the friend removes the account ------------------------------------------------------------------------------------------------------

    private sealed class ObservingLeave(InviteRig rig) : IBlindAccountLeaveHook
    {
        public bool KeysWerePresent { get; private set; }
        public int Calls { get; private set; }

        public async Task LeaveAsync(BlindAccountLeaveContext context, CancellationToken ct)
        {
            Calls++;
            // the host calls the hook while the account's keys are still there: the goodbye is signed with them
            KeysWerePresent = rig.Accounts.Secrets.For(context.Account).LoadIdentitySeed() is not null;
            await BlindSourceLeaveHook.Instance.LeaveAsync(context, ct);
        }
    }

    [Fact]
    public async Task WhenTheFriendRemovesAnAccount_TheSourceIsToldFirst_ThroughTheAccountsOwnClient_BeforeTheKeysAreCleared()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var observing = new ObservingLeave(rig);
        await using var host = rig.Host(leave: observing);
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        var node = NodeOf(rig, host, id);
        var entry = host.EntryOf(id);

        await host.RemoveAsync(id);

        observing.Calls.Should().Be(1);
        observing.KeysWerePresent.Should().BeTrue();
        hub.RoutesCalled.Should().Contain("left:" + node.ToString("D"));
        hub.Calls.Last(c => c.Route == "authenticate").Body.Should().Contain(node.ToString("D"), "the goodbye is under the account's own sync token");
        host.GetSnapshot().Accounts.Should().BeEmpty();
        rig.Accounts.Secrets.BlobCount(entry).Should().Be(0);
        rig.Accounts.States.HasState(entry).Should().BeFalse();
        Directory.Exists(Path.Combine(rig.Accounts.Root, "accounts", entry.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task TheDefaultHook_IsTheOneThatSaysGoodbye()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();    // no hook given: the host's default
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        var node = NodeOf(rig, host, id);

        await host.RemoveAsync(id);

        hub.RoutesCalled.Should().Contain("left:" + node.ToString("D"));
        new BlindAccountHostOptions { RootDirectory = "x", LegacyDatabasePath = "y", Secrets = rig.Accounts.Secrets, States = rig.Accounts.States }
            .LeaveHook.Should().BeSameAs(BlindSourceLeaveHook.Instance);
    }

    [Fact]
    public async Task TheGoodbye_IsCountedByTheAccountsMeter_ButNeverBlockedByItsLimits()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        var node = NodeOf(rig, host, id);
        host.SetPaused(id, true).Should().BeNull();
        host.SetLimits(id, new BlindAccountLimits(MaxTrafficBytesPerDay: 0)).Should().BeNull();
        var ledger = host.Get<BlindUsageLedger>(id);
        var before = ledger.UsageToday().Requests;
        // a normal call is refused while the account is paused ...
        (await host.For(id).RequestSyncAsync()).Should().StartWith("Paused");

        await BlindSourceLeaveHook.Instance.LeaveAsync(new BlindAccountLeaveContext(host.EntryOf(id), host.ServicesFor(id)), CancellationToken.None);

        // ... and the goodbye is not
        hub.RoutesCalled.Should().Contain("left:" + node.ToString("D"));
        ledger.UsageToday().Requests.Should().BeGreaterThan(before, "it is counted");
    }

    [Fact]
    public async Task AHubThatCannotBeReached_DoesNotHoldUpARemoval()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host(leaveTimeout: TimeSpan.FromMilliseconds(300));
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        hub.Enqueue("challenge", new HubFault.Unreachable());

        await host.RemoveAsync(id);

        hub.RoutesCalled.Should().NotContain(r => r.StartsWith("left:", StringComparison.Ordinal));
        host.GetSnapshot().Accounts.Should().BeEmpty("the removal completed locally");
    }

    [Fact]
    public async Task AHubThatNeverAnswers_HoldsARemovalNoLongerThanTheTimeout()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host(leaveTimeout: TimeSpan.FromMilliseconds(300));
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        hub.HoldSyncCalls = new TaskCompletionSource();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await host.RemoveAsync(id);
        watch.Stop();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        host.GetSnapshot().Accounts.Should().BeEmpty();
    }

    [Fact]
    public async Task AHubThatAlreadyRemovedTheDevice_401_DoesNotHoldUpARemoval()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        hub.Revoke(NodeOf(rig, host, id));

        await host.RemoveAsync(id);

        host.GetSnapshot().Accounts.Should().BeEmpty();
    }

    [Theory]
    [InlineData("awaiting")]
    [InlineData("pending")]
    [InlineData("refused")]
    public async Task AnAccountThatNeverConnected_IsRemovedWithoutSayingGoodbye(string phase)
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var invitation = hub.Issue();
        if (phase == "pending") hub.Locked = true;
        var id = (await host.AddFromInvitationAsync(invitation.ToWireText())).AccountId!;
        if (phase == "refused")
        {
            hub.Refuse(invitation);
            await host.PollInvitationsAsync(force: true);
        }
        var entry = host.EntryOf(id);

        await host.RemoveAsync(id);

        hub.RoutesCalled.Should().NotContain(r => r.StartsWith("left:", StringComparison.Ordinal));
        hub.RoutesCalled.Should().NotContain("authenticate");
        host.GetSnapshot().Accounts.Should().BeEmpty();
        rig.Accounts.Secrets.BlobCount(entry).Should().Be(0);
        rig.Accounts.States.HasState(entry).Should().BeFalse();
        Directory.Exists(Path.Combine(rig.Accounts.Root, "accounts", entry.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task ARemovalThatWasCutOff_StillSaysGoodbyeAtTheNextStart()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var crash = new CrashAtNameOnce("remove:marked");
        var dying = rig.Host(crash: crash);
        await dying.StartAsync();
        var id = await ActiveAsync(dying, hub);
        var node = NodeOf(rig, dying, id);
        crash.Armed = true;
        var act = () => dying.RemoveAsync(id);
        await act.Should().ThrowAsync<SimulatedCrash>();
        await dying.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        hub.RoutesCalled.Should().NotContain(r => r.StartsWith("left:", StringComparison.Ordinal));

        await using var next = rig.Host();
        await next.StartAsync();

        next.GetSnapshot().Accounts.Should().BeEmpty("the removal was finished");
        hub.RoutesCalled.Should().Contain("left:" + node.ToString("D"));
    }

    private sealed class CrashAtNameOnce(string step) : IBlindCrashPoints
    {
        private int _fired;

        public volatile bool Armed;

        public void Reach(string reached)
        {
            if (Armed && reached == step && Interlocked.Exchange(ref _fired, 1) == 0) throw new SimulatedCrash(reached);
        }
    }

    // ---- the old way stays --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APastedCallCode_LandsInTheAccountWhoseSecretMadeIt_AndInNoOtherEvenWithInvitedAccountsBesides()
    {
        using var rig = new InviteRig();
        var hubInvited = rig.Hub("hub-invited.test");
        var hubOldOne = rig.Hub("hub-old-one.test");
        var hubOldTwo = rig.Hub("hub-old-two.test");
        await using var host = rig.Host();
        await host.StartAsync();
        var invitedId = (await host.AddFromInvitationAsync(hubInvited.IssueText("Invited"))).AccountId!;
        var oldOne = (await host.AddAsync("old one")).AccountId!;
        var oldTwo = (await host.AddAsync("old two")).AccountId!;

        // the code the second hub made for the SECOND old account's phone code
        var secretTwo = rig.Accounts.Secrets.For(host.EntryOf(oldTwo)).LoadPairingSecret()!;
        host.AcceptCallCode(hubOldTwo.CallCodeFor(secretTwo)).Should().BeNull();

        new BlindPhoneState(rig.Accounts.States.For(host.EntryOf(oldTwo))).CallCode!.NodeId.Should().Be(hubOldTwo.NodeId);
        new BlindPhoneState(rig.Accounts.States.For(host.EntryOf(oldOne))).CallCode.Should().BeNull("a code made for another account does not pair this one");
        new BlindPhoneState(rig.Accounts.States.For(host.EntryOf(invitedId))).CallCode.Should().BeNull("nor an account that waits for an owner");
        host.EntryOf(invitedId).Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);

        var secretOne = rig.Accounts.Secrets.For(host.EntryOf(oldOne)).LoadPairingSecret()!;
        host.AcceptCallCode(hubOldOne.CallCodeFor(secretOne)).Should().BeNull();
        new BlindPhoneState(rig.Accounts.States.For(host.EntryOf(oldOne))).CallCode!.NodeId.Should().Be(hubOldOne.NodeId);
        new BlindPhoneState(rig.Accounts.States.For(host.EntryOf(invitedId))).CallCode.Should().BeNull();
    }

    [Fact]
    public async Task AnInvitedAccountThatWaits_IsNotAwaitingACallCode_SoACodeForNobodyIsSaidToBeForNobody()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var other = rig.Hub("hub-other.test");
        await using var host = rig.Host();
        await host.StartAsync();
        var id = (await host.AddFromInvitationAsync(hub.IssueText())).AccountId!;

        var error = host.AcceptCallCode(other.CallCodeFor(new byte[32]));

        error.Should().Be("No account on this device is waiting for a connection code. Add an account, or choose Re-pair on the one that should call another computer.");
        host.For(id).PairingCode().Should().BeNull("an invited account shows no phone code: it was not made to be paired the old way");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);
    }

    [Fact]
    public async Task APairingCodeOfTheOldWay_ForAnAccountTheSourceRemoved_PairsItAgainAndMakesItActive()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        var other = rig.Hub("hub-other.test");
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ActiveAsync(host, hub);
        hub.Revoke(NodeOf(rig, host, id));
        await RefusedTwiceAsync(rig, () => host.For(id).RequestSyncAsync());
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);

        host.For(id).StartRePair();
        var secret = rig.Accounts.Secrets.For(host.EntryOf(id)).LoadPairingSecret()!;
        host.AcceptCallCode(other.CallCodeFor(secret)).Should().BeNull();

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
        new BlindPhoneState(rig.Accounts.States.For(host.EntryOf(id))).CallCode!.NodeId.Should().Be(other.NodeId);
    }

    [Fact]
    public async Task AddingTheOldWay_StillWorksNextToInvitations()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        await ActiveAsync(host, hub);

        var old = await host.AddAsync("old way");

        old.Ok.Should().BeTrue(old.Error);
        host.For(old.AccountId!).PairingCode().Should().NotBeNull("the old way shows its phone code as it always did");
        host.GetSnapshot().Accounts.Should().HaveCount(2);
    }

    // ---- the owner revoked the device BEFORE its first load (review MAJOR-8) --------------------------------------------------------------------

    /// <summary>A confirmed account whose first load has not happened: what an invitation leaves between "Active" and the first download.</summary>
    internal static async Task<string> ConfirmedBeforeTheFirstLoadAsync(BlindAccountHost host, FakeInviteHub hub)
    {
        var invitation = hub.Issue("Alex");
        var added = await host.AddFromInvitationAsync(invitation.ToWireText());
        added.Ok.Should().BeTrue(added.Error);
        hub.Confirm(invitation);
        await host.PollInvitationsAsync(force: true);
        host.EntryOf(added.AccountId!).Phase.Should().Be(BlindAccountPhases.Active);
        host.For(added.AccountId!).GetStatus().InitialLoadDone.Should().BeFalse();
        return added.AccountId!;
    }

    [Fact]
    public async Task WhenTheOwnerRevokesTheDeviceBeforeTheFirstLoad_TheCardSaysRemovedByOwner_AndNoRetryStormFollows()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ConfirmedBeforeTheFirstLoadAsync(host, hub);
        await using var scheduler = new BlindDeviceScheduler(host, rig.Clock);
        hub.Revoke(NodeOf(rig, host, id));

        var result = await RefusedTwiceAsync(rig, () => host.For(id).RunHeavyAsync(forceBackup: false));

        result.Should().Be("The source no longer accepts this device. The copy stays until you remove it.");
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
        host.StateLineOf(id, rig.Clock).Should().Be("Alex removed this device from the network. The copy stays until you remove it.");
        host.For(id).GetStatus().LastFailure.Should().BeNull("it is not a first load that failed and will be tried again");
        var authenticated = hub.Count("authenticate");

        for (var pass = 0; pass < 6; pass++)
        {
            rig.Clock.Advance(TimeSpan.FromMinutes(61));
            await scheduler.TickAsync();
        }

        hub.Count("authenticate").Should().Be(authenticated, "a removed account is left alone: no retry storm");
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task ARefusalAtTheChallengeOrTheAuthentication_OfTheFirstLoad_IsTheSourceRemovingTheDevice(int status)
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ConfirmedBeforeTheFirstLoadAsync(host, hub);
        hub.Enqueue("authenticate", new HubFault.Status(status), new HubFault.Status(status));

        await RefusedTwiceAsync(rig, () => host.For(id).RunHeavyAsync(forceBackup: false));

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
    }

    [Fact]
    public async Task ARefusalOfTheChallenge_AtTheFirstLoad_IsTheSourceRemovingTheDevice()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ConfirmedBeforeTheFirstLoadAsync(host, hub);
        hub.Enqueue("challenge", new HubFault.Status(403), new HubFault.Status(403));

        await RefusedTwiceAsync(rig, () => host.For(id).RunHeavyAsync(forceBackup: false));

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.RemovedByOwner);
    }

    [Fact]
    public async Task ANetworkErrorOrAServerFault_AtTheFirstLoad_IsAFailureToRetry_NotARevocation()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        var id = await ConfirmedBeforeTheFirstLoadAsync(host, hub);

        hub.Enqueue("challenge", new HubFault.Unreachable());
        (await host.For(id).RunHeavyAsync(forceBackup: false)).Should().StartWith("First load failed");
        hub.Enqueue("challenge", new HubFault.Status(500));
        (await host.For(id).RunHeavyAsync(forceBackup: false)).Should().StartWith("First load failed");
        hub.Enqueue("authenticate", new HubFault.Status(503, RetryAfterSeconds: 30));
        (await host.For(id).RunHeavyAsync(forceBackup: false)).Should().StartWith("First load failed");
        hub.Enqueue("challenge", new HubFault.TimeOut());
        (await host.For(id).RunHeavyAsync(forceBackup: false)).Should().NotContain("no longer accepts");

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active, "none of these says the owner removed the device");
        host.For(id).GetStatus().LastFailure.Should().NotBeNull();
    }
}
