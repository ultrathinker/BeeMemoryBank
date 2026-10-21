using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Fix round 1, MAJOR-7 and MINOR-1. A 410 to the status request is "this invitation is not valid" only when the device's clock and the hub's agree (the proof of the request carries
/// the device's time, which the hub takes within ten minutes of its own): from a device whose clock is off it says nothing, and the card says that the clock is off, by how much, and keeps asking
/// at a low rate - never "refused", never "expired". And a held Retry-After never postpones what the invitation running out means.
/// </summary>
public sealed class BlindInvitationClockTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static async Task<(InviteRig Rig, FakeInviteHub Hub, BlindAccountHost Host, BlindInvitation Invitation, string Id)> AwaitingAsync(string name = "Alex")
    {
        var rig = new InviteRig();
        var hub = rig.Hub();
        var host = rig.Host();
        await host.StartAsync();
        var invitation = hub.Issue(name);
        var added = await host.AddFromInvitationAsync(invitation.ToWireText());
        added.Ok.Should().BeTrue(added.Error);
        host.EntryOf(added.AccountId!).Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);
        return (rig, hub, host, invitation, added.AccountId!);
    }

    // ---- a clock that is off (MAJOR-7) --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(15, "behind")]
    [InlineData(-15, "ahead of")]
    public async Task A410ToTheStatusRequest_FromADeviceWhoseClockIsOff_IsAVisibleNonTerminalState_NotExpired(int hubIsAheadByMinutes, string direction)
    {
        var (rig, hub, host, invitation, id) = await AwaitingAsync();
        using var _ = rig;
        await using var __ = host;
        hub.HubClockOffset = TimeSpan.FromMinutes(hubIsAheadByMinutes);   // the hub's clock against this device's: the proof of the request is outside the hub's ten minutes

        await host.PollInvitationsAsync(force: true);

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation, "the clock is what is wrong: nothing is concluded");
        var line = host.StateLineOf(id, rig.Clock);
        line.Should().Contain("This device's clock is 15 minutes off").And.Contain(direction).And.Contain("Correct the date and time");
        line.ToLowerInvariant().Should().NotContain("refused").And.NotContain("expired").And.NotContain("not valid");
        BlindAccountCard.Build(host.CardOf(id), TimeZoneInfo.Utc, rig.Clock.GetUtcNow()).Kind.Should().Be(BlindCardKind.Waiting, "not a final state");
        hub.StateOf(invitation).Should().Be("redeemed", "the owner's side is untouched");
    }

    [Fact]
    public async Task WhileTheClockIsOff_TheHubIsAskedAgainAtALowRate_EvenWhenAScreenAsksOften()
    {
        var (rig, hub, host, _, id) = await AwaitingAsync();
        using var _ = rig;
        await using var __ = host;
        hub.HubClockOffset = TimeSpan.FromMinutes(15);
        await host.PollInvitationsAsync(force: true);
        var asked = hub.Count("status");

        for (var second = 0; second < 240; second += 5)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(5));
            await host.PollInvitationsAsync(force: true);
        }
        hub.Count("status").Should().Be(asked, "four minutes of a screen asking every five seconds sends nothing");

        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        await host.PollInvitationsAsync(force: true);
        hub.Count("status").Should().Be(asked + 1, "after the pause it asks once");
        host.StateLineOf(id, rig.Clock).Should().Contain("clock is 15 minutes off");
    }

    [Fact]
    public async Task WhenTheClockIsCorrected_TheStateGoesAway_AndTheOwnersConfirmationComesThrough()
    {
        var (rig, hub, host, invitation, id) = await AwaitingAsync();
        using var _ = rig;
        await using var __ = host;
        hub.HubClockOffset = TimeSpan.FromMinutes(15);
        await host.PollInvitationsAsync(force: true);
        host.StateLineOf(id, rig.Clock).Should().Contain("clock is 15 minutes off");

        hub.HubClockOffset = TimeSpan.Zero;       // the person corrected it
        rig.Clock.Advance(TimeSpan.FromMinutes(6));
        await host.PollInvitationsAsync(force: true);
        host.StateLineOf(id, rig.Clock).Should().StartWith("Waiting for Alex to confirm");

        hub.Confirm(invitation);
        await host.PollInvitationsAsync(force: true);
        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Active);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-4)]
    public async Task A410FromAHubWhoseClockAgrees_StillMeansTheInvitationIsNotValid(int hubIsAheadByMinutes)
    {
        var (rig, hub, host, _, id) = await AwaitingAsync();
        using var _ = rig;
        await using var __ = host;
        hub.HubClockOffset = TimeSpan.FromMinutes(hubIsAheadByMinutes);   // within the five minutes the device lets pass
        hub.Enqueue("status", new HubFault.Status(410, Error: "invite_not_valid"));

        await host.PollInvitationsAsync(force: true);

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Expired);
        host.StateLineOf(id, rig.Clock).Should().Be(BlindInvitationWords.ExpiredCard);
    }

    [Fact]
    public async Task A410FromAHubThatSendsNoDate_CannotBeComparedAndIsTakenAtItsWord()
    {
        var (rig, hub, host, _, id) = await AwaitingAsync();
        using var _ = rig;
        await using var __ = host;
        hub.OmitDate = true;
        hub.Enqueue("status", new HubFault.Status(410, Error: "invite_not_valid"));

        await host.PollInvitationsAsync(force: true);

        host.EntryOf(id).Phase.Should().Be(BlindAccountPhases.Expired);
    }

    [Fact]
    public async Task APendingAccount_ThatAsksWhetherItsRedemptionLanded_ByADeviceWhoseClockIsOff_IsNotTakenAway()
    {
        // The status request that follows an invitation that ran out also carries the device's time: a 410 for it from a hub whose clock disagrees is not "this account never got anywhere".
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        hub.Enqueue("redeem", new HubFault.LoseAnswer());   // the hub took it; the answer was lost: the redemption may have landed
        var invitation = hub.Issue("Alex", lifetime: TimeSpan.FromMinutes(1));
        var added = await host.AddFromInvitationAsync(invitation.ToWireText());
        added.Ok.Should().BeTrue(added.Error);
        host.EntryOf(added.AccountId!).Phase.Should().Be(BlindAccountPhases.Pending);
        hub.HubClockOffset = TimeSpan.FromMinutes(-15);
        rig.Clock.Advance(TimeSpan.FromMinutes(2));

        await host.PollInvitationsAsync(force: true);

        host.GetSnapshot().Accounts.Should().ContainSingle("the account was not removed on a 410 from a clock that disagrees");
        host.EntryOf(added.AccountId!).Phase.Should().Be(BlindAccountPhases.Pending);
        host.StateLineOf(added.AccountId!, rig.Clock).Should().Contain("This device's clock is 15 minutes off");
    }

    // ---- a held Retry-After and the invitation's end (MINOR-1) --------------------------------------------------------------------------------

    [Fact]
    public async Task AHeldRetryAfter_NeverPostponesTheExpiry_AndNoStatusRequestIsSentWhenNothingWasDelivered()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        hub.Enqueue("redeem", new HubFault.Status(429, RetryAfterSeconds: 3600, Error: "rate_limited"));
        var invitation = hub.Issue("Alex", lifetime: TimeSpan.FromSeconds(30));
        var added = await host.AddFromInvitationAsync(invitation.ToWireText());
        added.Ok.Should().BeTrue(added.Error);
        host.EntryOf(added.AccountId!).Phase.Should().Be(BlindAccountPhases.Pending);
        host.HeldRedemptions.Should().Be(1);

        rig.Clock.Advance(TimeSpan.FromSeconds(31));
        await host.PollInvitationsAsync(force: true);

        host.GetSnapshot().Accounts.Should().BeEmpty("the invitation ran out and nothing was ever delivered: the account ends then, not when a held hour does");
        host.HeldRedemptions.Should().Be(0);
        hub.Count("status").Should().Be(0, "no status request is sent for an invitation that cannot be redeemed or decided any more");
        hub.Count("redeem").Should().Be(1);
    }

    [Fact]
    public async Task AHeldRetryAfter_IsCappedAtTheInvitationsEnd_AndNothingIsAskedBeforeIt()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        hub.Enqueue("redeem", new HubFault.Status(429, RetryAfterSeconds: 3600, Error: "rate_limited"));
        var invitation = hub.Issue("Alex", lifetime: TimeSpan.FromMinutes(10));
        var added = await host.AddFromInvitationAsync(invitation.ToWireText());
        added.Ok.Should().BeTrue(added.Error);
        var asked = hub.Calls.Count;

        rig.Clock.Advance(TimeSpan.FromMinutes(9));
        await host.PollInvitationsAsync(force: true);
        hub.Calls.Count.Should().Be(asked, "the hub asked to wait: nothing is sent before the hold ends");
        host.HoldOf(added.AccountId!).Should().NotBeNull();
        host.HoldOf(added.AccountId!)!.Value.Should().BeOnOrBefore(invitation.ExpiresAt, "a hold never lasts past the invitation");

        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        await host.PollInvitationsAsync(force: true);
        host.GetSnapshot().Accounts.Should().BeEmpty("the end of the invitation was judged when it came, not when the 15 minutes the hub asked for were over");
    }

    [Fact]
    public async Task ARedemptionThatMayHaveLanded_IsStillAskedAboutAfterTheInvitationRanOut()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();
        await host.StartAsync();
        hub.Enqueue("redeem", new HubFault.LoseAnswer());
        var invitation = hub.Issue("Alex", lifetime: TimeSpan.FromSeconds(30));
        var added = await host.AddFromInvitationAsync(invitation.ToWireText());
        added.Ok.Should().BeTrue(added.Error);
        host.EntryOf(added.AccountId!).Phase.Should().Be(BlindAccountPhases.Pending);
        hub.StateOf(invitation).Should().Be("redeemed", "the hub took it; only the answer was lost");

        rig.Clock.Advance(TimeSpan.FromSeconds(45));
        await host.PollInvitationsAsync(force: true);

        hub.Count("status").Should().Be(1, "the owner can still decide about a redemption that landed before the end: the only way to learn it is to ask");
        host.EntryOf(added.AccountId!).Phase.Should().Be(BlindAccountPhases.AwaitingConfirmation);
        host.HeldRedemptions.Should().Be(0);
    }
}
