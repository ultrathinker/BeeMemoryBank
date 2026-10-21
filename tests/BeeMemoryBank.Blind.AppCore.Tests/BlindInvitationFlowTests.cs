using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Adding an account from an invitation (release 2.7), against a fake hub that has the real hub's rules: the identity probe before anything is sent, the redemption with the account's
/// own identity, every answer the hub can give to it, the status polling, and what the account is in each state.
/// </summary>
public sealed class BlindInvitationFlowTests
{
    private static async Task<(InviteRig Rig, FakeInviteHub Hub, BlindAccountHost Host)> StartedAsync(string hubHost = "hub-a.test", bool isPhone = false)
    {
        var rig = new InviteRig();
        var hub = rig.Hub(hubHost);
        var host = rig.Host(isPhone: isPhone);
        await host.StartAsync();
        return (rig, hub, host);
    }

    private static async Task<string> AddedAsync(BlindAccountHost host, BlindInvitation invitation, BlindAccountSettings? settings = null, FakeInviteHub? hub = null)
    {
        var added = await host.AddFromInvitationAsync(invitation.ToWireText(), settings);
        var because = added.Error;
        if (!added.Ok && hub is { } asked)
        {
            // A failed add of a valid invitation is a race this suite must not have: what the hub was asked for says which side gave up —
            // an ask for the identity only means the device refused to build the redemption (its keys or its held secret), a redeem ask that
            // came back failed means the account's state did not hold what it had just been verified to hold.
            because += " [the hub was asked for: " + string.Join(", ", asked.RoutesCalled.DefaultIfEmpty("nothing")) + "]";
        }
        added.Ok.Should().BeTrue(because);
        return added.AccountId!;
    }

    // ---- the happy path ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnInvitation_MakesAnAccountThatRedeemsWithItsOwnIdentity_AndWaitsWithTheCodeToReadToTheOwner()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue("Alex");

        var id = await AddedAsync(host, invitation);

        var card = host.CardOf(id);
        card.Entry.Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);
        card.Entry.Name.Should().Be("Alex");
        card.Entry.SourceOrigin.Should().Be(hub.Origin);
        card.Entry.InviteId.Should().Be(invitation.InviteIdText);
        card.Entry.Layout.Should().Be(BlindAccountLayouts.Dir);
        card.Entry.Established.Should().BeTrue("the identity was made");

        // The hub saw this device's own identity (not the legacy one, not another account's), and the code on the owner's page is the one on this device's card.
        var nodeId = card.Status!.NodeId!.Value;
        hub.RedeemerOf(invitation).Should().Be(nodeId);
        BlindNodeId.IsBlind(nodeId).Should().BeTrue();
        hub.RedeemerKeyOf(invitation).Should().Equal(new BlindPhoneState(rig.Accounts.States.For(card.Entry)).PublicKey);
        card.Entry.MatchCode.Should().Be(hub.MatchCodeOf(invitation));
        BlindInviteMatchCode.Normalize(card.Entry.MatchCode).Should().Be(card.Entry.MatchCode);
        host.StateLineOf(id, rig.Clock).Should().Be($"Waiting for Alex to confirm. Read this code out to them: {card.Entry.MatchCode}");
        card.Status.IsPaired.Should().BeFalse("before the owner decides the account has no call code and so no data");
        card.Status.AwaitingAnswer.Should().BeFalse("it is not paired the old way: its pairing secret is spent");

        // The redemption carries the backup key of the account (the owner seals it when he confirms) and says what this device is.
        hub.BackupKeyOf(invitation).Should().Equal(rig.Accounts.Secrets.For(card.Entry).LoadBackupKey());
        var (name, platform, version) = hub.DescribedBy(invitation);
        name.Should().Be("Test device");
        platform.Should().NotBeNull();
        version.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TheIdentityIsProbed_BeforeTheSecretIsSent_ThroughTheClientTheInvitationsTrustSelects()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;

        await AddedAsync(host, hub.Issue());

        // identity first; the redemption only after it
        hub.RoutesCalled.Where(r => r is "identity" or "redeem").Should().Equal("identity", "redeem");
    }

    [Theory]
    [InlineData(BlindTrust.Pin)]
    [InlineData(BlindTrust.PublicCa)]
    public async Task BothTrustModes_AreAccepted(string trust)
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;

        var id = await AddedAsync(host, hub.Issue(trust: trust));

        host.CardOf(id).Entry.Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);
    }

    [Fact]
    public async Task TheInvitationTextIsNotKept_OnlyWhatIsNeededAfterTheRedemption()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue("Alex");

        var id = await AddedAsync(host, invitation, hub: hub);

        var entry = host.EntryOf(id);
        var kept = new BlindInvitationState(rig.Accounts.States.For(entry));
        kept.Target.Should().NotBeNull();
        kept.Target!.HubNodeId.Should().Be(hub.NodeId);
        kept.Target.Address.Should().Be(hub.Origin);
        kept.Target.Trust.Should().Be(BlindTrust.Pin);
        kept.Target.SpkiPin.Should().Be(hub.Pin);
        kept.Target.HubPublicKey.Should().Equal(hub.PublicKey);
        kept.Target.InviteIdText.Should().Be(invitation.InviteIdText);
        kept.ConfirmByUnix.Should().NotBeNull();
        new BlindPhoneState(rig.Accounts.States.For(entry)).CallCode.Should().BeNull();
    }

    // ---- the owner's decision ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhenTheOwnerConfirms_TheAccountKeepsACallCodeBuiltFromTheInvitation_AndEverythingAfterItIsTheOldPath()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue("Alex");
        var id = await AddedAsync(host, invitation);

        hub.Confirm(invitation);
        (await host.PollInvitationsAsync(force: true)).Should().Be(0);

        var entry = host.EntryOf(id);
        entry.Phase.Should().Be(BlindAccountPhases.Active);
        entry.MatchCode.Should().BeNull();
        var state = new BlindPhoneState(rig.Accounts.States.For(entry));
        state.CallCode.Should().NotBeNull();
        state.CallCode!.NodeId.Should().Be(hub.NodeId);
        state.CallCode.Address.Should().Be(hub.Origin);
        state.CallCode.SpkiPin.Should().Be(hub.Pin);
        state.CallCode.PublicKey.Should().Equal(hub.PublicKey);
        state.CallCode.Trust.Should().Be(BlindTrust.Pin);
        // the secret its MAC was made under was spent at once: nothing can authenticate it again, and no pairing secret is waiting
        rig.Accounts.Secrets.For(entry).LoadPairingSecret().Should().BeNull();
        new BlindInvitationState(rig.Accounts.States.For(entry)).Exists.Should().BeFalse("nothing of the invitation is kept once the account is active");

        var status = host.For(id).GetStatus();
        status.IsPaired.Should().BeTrue();
        status.Endpoint.Should().Be(hub.Origin);
        status.AwaitingAnswer.Should().BeFalse();
    }

    [Fact]
    public async Task AConfirmedAccountOnAPublicCertificate_GetsAPublicCaCallCode()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue(trust: BlindTrust.PublicCa);
        var id = await AddedAsync(host, invitation);

        hub.Confirm(invitation);
        await host.PollInvitationsAsync(force: true);

        var call = new BlindPhoneState(rig.Accounts.States.For(host.EntryOf(id))).CallCode!;
        call.Trust.Should().Be(BlindTrust.PublicCa);
        call.SpkiPin.Should().BeEmpty();
    }

    [Fact]
    public async Task AfterTheConfirmation_TheAccountSyncsThroughTheOldPath_AndATheNoteWrittenOnTheHubReachesIt()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue();
        var id = await AddedAsync(host, invitation);
        hub.Confirm(invitation);
        await host.PollInvitationsAsync(force: true);
        await host.SeedHubAsync(id, hub.Sync);       // what the first load leaves: the hub's whitelist row, and "first load done"

        hub.Sync.AddFolder();
        var result = await host.For(id).RequestSyncAsync();

        result.Should().Be("Synced.");
        (await host.FolderNamesAsync(id)).Should().Contain(hub.Sync.Canary);
        hub.Calls.Count(c => c.Route == "authenticate").Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task WhenTheOwnerRefuses_TheAccountSaysSo_AndOffersRemove()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue("Alex");
        var id = await AddedAsync(host, invitation);

        hub.Refuse(invitation);
        await host.PollInvitationsAsync(force: true);

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Refused);
        host.StateLineOf(id, rig.Clock).Should().Be("Alex refused this device. You can remove this account.");
        new BlindPhoneState(rig.Accounts.States.For(host.EntryOf(id))).CallCode.Should().BeNull();
        new BlindInvitationState(rig.Accounts.States.For(host.EntryOf(id))).Exists.Should().BeFalse();
    }

    [Fact]
    public async Task WhenTheOwnersTimeRunsOut_TheAccountSaysTheInvitationExpired()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue();
        var id = await AddedAsync(host, invitation);

        hub.Expire(invitation);
        await host.PollInvitationsAsync(force: true);

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Expired);
        host.StateLineOf(id, rig.Clock).Should().Be("The invitation expired. You can remove this account.");
    }

    [Fact]
    public async Task AWaitingAccount_IsOnlyAskedAtItsOwnRhythm_UnlessTheScreenAsks()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue();
        await AddedAsync(host, invitation);
        var before = hub.Count("status");

        // the scheduler's passes (not forced): nothing is due for 15 minutes
        for (var i = 0; i < 5; i++) await host.PollInvitationsAsync();
        hub.Count("status").Should().Be(before);

        rig.Clock.Advance(BlindAccountHost.BackgroundAsk + TimeSpan.FromSeconds(1));
        await host.PollInvitationsAsync();
        hub.Count("status").Should().Be(before + 1);

        // while the screen is open every call asks (the watch makes one every 15 seconds)
        await host.PollInvitationsAsync(force: true);
        await host.PollInvitationsAsync(force: true);
        hub.Count("status").Should().Be(before + 3);
    }

    // ---- the identity probe ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHubWithAnotherNodeId_IsRefused_AndNothingIsSent()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue();
        hub.IdentityNodeId = Guid.NewGuid();

        var added = await host.AddFromInvitationAsync(invitation.ToWireText());

        added.Ok.Should().BeFalse();
        added.Error.Should().Be(BlindInvitationWords.HubMismatch(new Uri(hub.Origin).Host));
        hub.RoutesCalled.Should().Equal("identity");
        host.GetSnapshot().Accounts.Should().BeEmpty("a refused invitation leaves no account");
        hub.StateOf(invitation).Should().Be("issued");
    }

    [Fact]
    public async Task AHubWithAnotherKey_IsRefused_AndNothingIsSent()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue();
        hub.IdentityKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        var added = await host.AddFromInvitationAsync(invitation.ToWireText());

        added.Ok.Should().BeFalse();
        hub.RoutesCalled.Should().Equal("identity");
        host.GetSnapshot().Accounts.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAddressThatIsNotANode_IsRefused_AndNothingIsSent()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue();
        hub.IdentityStatus = 404;

        var added = await host.AddFromInvitationAsync(invitation.ToWireText());

        added.Ok.Should().BeFalse();
        hub.RoutesCalled.Should().Equal("identity");
        host.GetSnapshot().Accounts.Should().BeEmpty();
    }

    // ---- what the hub can answer to the redemption --------------------------------------------------------------------------------------------

    [Fact]
    public async Task OneAnswerForEverythingNotValid_410_RemovesTheAccount_WithOneSentence()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue("Alex");
        hub.Enqueue("redeem", new HubFault.Status(410, Error: "invite_not_valid"));

        var added = await host.AddFromInvitationAsync(invitation.ToWireText());

        added.Ok.Should().BeFalse();
        added.Error.Should().Be(BlindInvitationWords.NotValid("Alex"));
        host.GetSnapshot().Accounts.Should().BeEmpty();
        Directory.Exists(Path.Combine(rig.Accounts.Root, "accounts")).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(Path.Combine(rig.Accounts.Root, "accounts")).Should().BeEmpty("the folder of the refused account is gone");
    }

    [Fact]
    public async Task AnInvitationAnotherDeviceUsedFirst_IsAnswered410_AndTheEavesdroppersOwnRedemptionIsNotOurs()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue("Alex");
        // an eavesdropper redeemed first (another device, another key)
        using var other = new InviteRig();
        var otherHub = other.Hub();
        await using var otherHost = other.Host();
        await otherHost.StartAsync();
        // the same invitation text, redeemed by a device whose identity is not ours, against OUR hub: the hub's router knows our hub only, so the thief's account is built over the same fake
        other.Router.Add(hub);
        var thief = await otherHost.AddFromInvitationAsync(invitation.ToWireText());
        thief.Ok.Should().BeTrue(thief.Error);

        var added = await host.AddFromInvitationAsync(invitation.ToWireText());

        added.Ok.Should().BeFalse();
        added.Error.Should().Be(BlindInvitationWords.NotValid("Alex"), "the friend is told it was already used (one sentence for everything not valid)");
        host.GetSnapshot().Accounts.Should().BeEmpty();
        hub.StateOf(invitation).Should().Be("redeemed", "the eavesdropper's request is still pending: the owner refuses it");
    }

    [Fact]
    public async Task AHubOlderThan27_Answers404_AndTheSentenceIsTheExactOne_WithNoFallbackToTheOldWay()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue();
        hub.Older = true;

        var added = await host.AddFromInvitationAsync(invitation.ToWireText());

        added.Ok.Should().BeFalse();
        added.Error.Should().Be("The owner's Bee Memory Bank is older than 2.7 and cannot take invitations; ask the owner to update, or pair with a code the old way.");
        host.GetSnapshot().Accounts.Should().BeEmpty("nothing is started the old way on its own");
        hub.RoutesCalled.Should().Equal("identity", "redeem");
    }

    [Fact]
    public async Task A409_RemovesTheAccount_WithASentence()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        hub.Enqueue("redeem", new HubFault.Status(409, Error: "device_known_under_other_key"));

        var added = await host.AddFromInvitationAsync(hub.IssueText());

        added.Ok.Should().BeFalse();
        added.Error.Should().Be(BlindInvitationWords.KnownUnderOtherKey(new Uri(hub.Origin).Host));
        host.GetSnapshot().Accounts.Should().BeEmpty();
    }

    [Fact]
    public async Task A429_KeepsTheAccountPending_AndTheHubIsNotAskedAgainBeforeItsRetryAfter_EvenByAScreen()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue();
        hub.Enqueue("redeem", new HubFault.Status(429, RetryAfterSeconds: 120, Error: "too_many"));

        var added = await host.AddFromInvitationAsync(invitation.ToWireText());

        added.Ok.Should().BeTrue(added.Error);
        var id = added.AccountId!;
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Pending);
        host.StateLineOf(id, rig.Clock).Should().Be(BlindInvitationWords.AskedToWait(new Uri(hub.Origin).Host));
        var asked = hub.Count("redeem");

        rig.Clock.Advance(TimeSpan.FromSeconds(60));
        await host.PollInvitationsAsync(force: true);
        await host.PollInvitationsAsync(force: true);
        hub.Count("redeem").Should().Be(asked, "the hub said 120 seconds, and a forced ask does not shorten it");

        rig.Clock.Advance(TimeSpan.FromSeconds(61));
        await host.PollInvitationsAsync(force: true);

        hub.Count("redeem").Should().Be(asked + 1);
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);
    }

    [Fact]
    public async Task AHubThatIsLocked_503_IsRetriedWithABackOff_AndTheAccountGoesOnWhenItIsUnlocked()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue();
        hub.Locked = true;

        var added = await host.AddFromInvitationAsync(invitation.ToWireText());

        added.Ok.Should().BeTrue(added.Error);
        var id = added.AccountId!;
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Pending);
        host.StateLineOf(id, rig.Clock).Should().Be(BlindInvitationWords.Locked);
        host.CardOf(id).Status!.NodeId.Should().NotBeNull("the account exists and holds an identity, and nothing else");

        // a minute later, still locked: another try, then waiting longer
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await host.PollInvitationsAsync(force: true);
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Pending);

        hub.Locked = false;
        rig.Clock.Advance(TimeSpan.FromMinutes(6));
        (await host.PollInvitationsAsync(force: true)).Should().Be(1);

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);
        hub.StateOf(invitation).Should().Be("redeemed");
    }

    [Fact]
    public async Task ALockedHub_IsNotAskedInAStorm_WhateverIsAskingAndHoweverOften()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue(lifetime: TimeSpan.FromMinutes(30));
        hub.Locked = true;
        var id = (await host.AddFromInvitationAsync(invitation.ToWireText())).AccountId!;

        // thirty minutes of a screen that asks every 15 seconds, and a scheduler that asks every 30
        for (var second = 0; second < 30 * 60; second += 15)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(15));
            await host.PollInvitationsAsync(force: true);
        }

        hub.Count("redeem").Should().BeLessThan(16, "a bounded back-off, not one request per ask");
        hub.Calls.Count.Should().BeLessThan(40, "identity, redemption and status together: a bounded back-off all the time");
        id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ANetworkErrorBeforeTheRedemption_KeepsTheAccountPending_AndItGoesOnWhenTheNetworkIsBack()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue();
        hub.Enqueue("identity", new HubFault.Unreachable());

        var added = await host.AddFromInvitationAsync(invitation.ToWireText());

        added.Ok.Should().BeTrue(added.Error);
        var id = added.AccountId!;
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Pending);
        host.StateLineOf(id, rig.Clock).Should().Be(BlindInvitationWords.Unreachable(new Uri(hub.Origin).Host));
        hub.Count("redeem").Should().Be(0);

        rig.Clock.Advance(TimeSpan.FromSeconds(16));
        await host.PollInvitationsAsync(force: true);

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);
    }

    [Fact]
    public async Task ALostAnswer_IsRetriedWithTheSameIdentity_AndTheHubAnswersTheSame202()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue();
        hub.Enqueue("redeem", new HubFault.LoseAnswer());

        var added = await host.AddFromInvitationAsync(invitation.ToWireText());

        added.Ok.Should().BeTrue(added.Error);
        var id = added.AccountId!;
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Pending, "the hub took the redemption but the device does not know");
        hub.StateOf(invitation).Should().Be("redeemed");
        var node = hub.RedeemerOf(invitation);

        rig.Clock.Advance(TimeSpan.FromSeconds(16));
        await host.PollInvitationsAsync(force: true);

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);
        hub.RedeemerOf(invitation).Should().Be(node, "the same identity asked again");
        hub.RedemptionsOf(invitation).Should().Be(1, "the hub counted one redemption: the repeat was recognised");
        hub.Count("redeem").Should().Be(2);
        host.EntryOf(id).MatchCode.Should().Be(hub.MatchCodeOf(invitation));
    }

    [Fact]
    public async Task ARequestThatTimedOut_IsTreatedLikeALostAnswer()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue();
        hub.Enqueue("redeem", new HubFault.TimeOut());

        var id = await AddedAsync(host, invitation);

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Pending);
        rig.Clock.Advance(TimeSpan.FromSeconds(16));
        await host.PollInvitationsAsync(force: true);
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);
    }

    [Fact]
    public async Task ALostAnswer_ThenTheOwnerConfirmsBeforeTheRetry_410OnTheRetry_IsAnsweredByTheStatus()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue();
        hub.Enqueue("redeem", new HubFault.LoseAnswer());
        var id = await AddedAsync(host, invitation);
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Pending);

        hub.Confirm(invitation);            // the owner was quick: the retry's redeem will be 410 (not issued any more)
        rig.Clock.Advance(TimeSpan.FromSeconds(16));
        await host.PollInvitationsAsync(force: true);

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active, "the 410 was not taken as 'not valid': the signed status said confirmed");
        new BlindPhoneState(rig.Accounts.States.For(host.EntryOf(id))).CallCode.Should().NotBeNull();
    }

    [Fact]
    public async Task AnInvitationThatRunsOutBeforeItIsRedeemed_EndsTheTries_AndTheAccountIsRemoved()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue(lifetime: TimeSpan.FromMinutes(15));
        hub.Enqueue("identity", new HubFault.Unreachable());
        var id = await AddedAsync(host, invitation);
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Pending);

        rig.Clock.Advance(TimeSpan.FromMinutes(16));
        await host.PollInvitationsAsync(force: true);

        host.GetSnapshot().Accounts.Should().BeEmpty();
        hub.Count("redeem").Should().Be(0, "an invitation that has expired is not redeemed");
    }

    [Fact]
    public async Task A202WithNoAnswerTheDeviceCanRead_IsNotUnderstood_AndTheAccountIsRemoved()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        hub.Enqueue("redeem", new HubFault.Status(202));

        var added = await host.AddFromInvitationAsync(hub.IssueText("Alex"));

        added.Ok.Should().BeFalse();
        added.Error.Should().Be(BlindInvitationWords.NotUnderstood(new Uri(hub.Origin).Host));
        host.GetSnapshot().Accounts.Should().BeEmpty();
    }

    [Fact]
    public async Task AnAnswerWithAnotherMatchCodeThanTheDeviceComputes_IsRefused_AndTheAccountRemoved()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        hub.MatchCodeOverride = "AAA-AAA";

        var added = await host.AddFromInvitationAsync(hub.IssueText("Alex"));

        added.Ok.Should().BeFalse("the code the owner would read out is not the one this device computed from its own identity");
        host.GetSnapshot().Accounts.Should().BeEmpty();
    }

    // ---- limits: the calls are the account's own, counted and held back like any ------------------------------------------------------------

    [Fact]
    public async Task EveryCallIsCounted_ByTheAccountsOwnMeter()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;

        var id = await AddedAsync(host, hub.Issue());

        var usage = host.CardOf(id).Usage!;
        usage.RequestsToday.Should().Be(2, "the identity probe and the redemption");
        usage.BytesOutToday.Should().BeGreaterThan(0);
        usage.BytesInToday.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AnAccountOverItsLimit_SaysSo_InsteadOfRedeeming_AndGoesOnWhenTheLimitIsRaised()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue();
        var settings = new BlindAccountSettings(new BlindAccountLimits(MaxStoredBytes: 1L << 30, MaxTrafficBytesPerDay: 0), false, false);

        var added = await host.AddFromInvitationAsync(invitation.ToWireText(), settings);

        added.Ok.Should().BeTrue(added.Error);
        var id = added.AccountId!;
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Pending);
        host.StateLineOf(id, rig.Clock).Should().StartWith("Paused:").And.Contain("traffic");
        hub.RoutesCalled.Should().BeEmpty("nothing went out: a paused account sends nothing, not even the identity probe");

        host.SetLimits(id, new BlindAccountLimits(MaxStoredBytes: 1L << 30, MaxTrafficBytesPerDay: 200L << 20)).Should().BeNull();
        rig.Clock.Advance(TimeSpan.FromMinutes(6));
        await host.PollInvitationsAsync(force: true);

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);
        hub.RoutesCalled.Should().Contain("redeem");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnInvitedAccountStartsWithTheProtectingDefaults_AndOnAPhoneTheTwoSwitchesAreOn(bool isPhone)
    {
        var (rig, hub, host) = await StartedAsync(isPhone: isPhone);
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue();

        var preview = host.PreviewInvitation(invitation.ToWireText());

        preview.Settings.Should().Be(BlindAccountDefaults.ForInvited(isPhone));
        preview.Settings.Limits.Should().Be(new BlindAccountLimits(2L << 30, 200L << 20, null));
        preview.Settings.UnmeteredOnly.Should().Be(isPhone);
        preview.Settings.PauseOnPowerSaving.Should().Be(isPhone);
    }
}
