using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// What a person is shown before an invitation is accepted, and every reason it is not taken: from the pasted text and the accounts alone. Nothing is sent and nothing is written
/// by a preview, and an add that is refused leaves nothing behind.
/// </summary>
public sealed class BlindInvitationPreviewTests
{
    private static async Task<(InviteRig Rig, FakeInviteHub Hub, BlindAccountHost Host)> StartedAsync(int maxAccounts = BlindAccountHost.DefaultMaxAccounts)
    {
        var rig = new InviteRig();
        var hub = rig.Hub();
        var host = rig.Host(maxAccounts: maxAccounts);
        await host.StartAsync();
        return (rig, hub, host);
    }

    [Fact]
    public async Task APreview_NamesTheSourceTheComputerAndTheLimits_AndSaysTheDeviceCannotReadIt()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue("Alex", lifetime: TimeSpan.FromMinutes(30));
        var before = AccountRig.Fingerprint(rig.Accounts.Root);

        var preview = host.PreviewInvitation(invitation.ToWireText());

        preview.CanAdd.Should().BeTrue(preview.Refusal);
        preview.Refusal.Should().BeNull();
        preview.RefusalKind.Should().BeNull();
        preview.SourceName.Should().Be("Alex");
        preview.SourceHost.Should().Be("hub-a.test");
        preview.Description.Should().Be("A copy of Alex's Bee Memory Bank, kept encrypted: this device cannot read it. It comes from hub-a.test. This invitation is a one-time code, not an encrypted one.");
        preview.Limits.Should().Be("It will use at most 2 GB of storage, 200 MB a day.");
        preview.ExpiresAt.Should().Be(invitation.ExpiresAt);
        preview.Validity(rig.Clock.GetUtcNow()).Should().Be("This invitation works for about 30 minutes more.");
        AccountRig.Fingerprint(rig.Accounts.Root).Should().BeEquivalentTo(before, "a preview writes nothing");
        hub.RoutesCalled.Should().BeEmpty("a preview sends nothing");
        host.GetSnapshot().Accounts.Should().BeEmpty();
    }

    [Fact]
    public async Task TheText_MayComeWithSpacesAndLineBreaksAroundIt()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;

        host.PreviewInvitation("  \n" + hub.IssueText() + "\r\n ").CanAdd.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("bmb-invite:")]
    [InlineData("bmb-invite:?v=1")]
    [InlineData("https://example.org/bmb-invite:?v=1")]
    public async Task ATextThatIsNotAnInvitation_IsMalformed(string? text)
    {
        var (rig, _, host) = await StartedAsync();
        using var _r = rig;
        await using var __ = host;

        var preview = host.PreviewInvitation(text);

        preview.CanAdd.Should().BeFalse();
        preview.RefusalKind.Should().Be(BlindInvitationRefusals.Malformed);
        preview.Refusal.Should().Be(BlindInvitationWords.Malformed);
    }

    [Fact]
    public async Task AnInvitationCutShortOrChanged_IsMalformed()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var text = hub.IssueText();

        foreach (var damaged in new[]
                 {
                     text[..^10],                                                   // cut short
                     text + "&z=1",                                                 // a field it does not know
                     text.Replace("&e=", "&e=x"),                                   // an expiry that is not a number
                     text.Replace("&n=", "&n=0"),                                   // a node id that is not one
                     text + new string('a', 3000),                                  // far too long
                 })
        {
            var preview = host.PreviewInvitation(damaged);
            preview.CanAdd.Should().BeFalse();
            preview.RefusalKind.Should().Be(BlindInvitationRefusals.Malformed, damaged.Length > 100 ? damaged[^30..] : damaged);
        }
    }

    [Theory]
    [InlineData("bmb-blind-call:?a=https%3A%2F%2Fx.example&n=00000000-0000-0000-0000-000000000000")]
    [InlineData("bmb-blind-phone:?n=00000000-0000-0000-0000-000000000000")]
    public async Task APairingCodeOfTheOldWay_IsSaidToBeOne(string text)
    {
        var (rig, _, host) = await StartedAsync();
        using var _r = rig;
        await using var __ = host;

        var preview = host.PreviewInvitation(text);

        preview.RefusalKind.Should().Be(BlindInvitationRefusals.OtherCode);
        preview.Refusal.Should().Be(BlindInvitationWords.OtherCode).And.Contain("Add with a pairing code");
    }

    [Fact]
    public async Task AnInvitationOfAVersionThisAppDoesNotKnow_IsRefused_NotGuessedAt()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var text = hub.IssueText();
        text.Should().Contain("v=1");

        var preview = host.PreviewInvitation(text.Replace("v=1", "v=2"));
        var added = await host.AddFromInvitationAsync(text.Replace("v=1", "v=2"));

        preview.CanAdd.Should().BeFalse();
        preview.RefusalKind.Should().Be(BlindInvitationRefusals.UnknownVersion);
        preview.Refusal.Should().Be(BlindInvitationWords.UnknownVersion);
        added.Ok.Should().BeFalse();
        added.Error.Should().Be(BlindInvitationWords.UnknownVersion);
        hub.RoutesCalled.Should().BeEmpty();
        host.GetSnapshot().Accounts.Should().BeEmpty();
    }

    [Fact]
    public async Task AnInvitationThatNamesAWayOfTrustingTheHubThisAppDoesNotKnow_IsRefused()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var text = hub.IssueText(trust: BlindTrust.PublicCa);
        text.Should().Contain("t=public-ca");
        var altered = text.Replace("t=public-ca", "t=quantum");

        var preview = host.PreviewInvitation(altered);
        var added = await host.AddFromInvitationAsync(altered);

        preview.RefusalKind.Should().Be(BlindInvitationRefusals.UnknownTrust);
        preview.Refusal.Should().Be(BlindInvitationWords.UnknownTrust);
        added.Ok.Should().BeFalse();
        hub.RoutesCalled.Should().BeEmpty();
    }

    [Fact]
    public async Task AnExpiredInvitation_IsRefused_WithItsNameAndHostStillShown()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var invitation = hub.Issue("Alex", lifetime: TimeSpan.FromMinutes(15));
        rig.Clock.Advance(TimeSpan.FromMinutes(16));

        var preview = host.PreviewInvitation(invitation.ToWireText());
        var added = await host.AddFromInvitationAsync(invitation.ToWireText());

        preview.CanAdd.Should().BeFalse();
        preview.RefusalKind.Should().Be(BlindInvitationRefusals.Expired);
        preview.Refusal.Should().Be(BlindInvitationWords.Expired);
        preview.SourceName.Should().Be("Alex");
        preview.Validity(rig.Clock.GetUtcNow()).Should().Be("This invitation has expired.");
        added.Error.Should().Be(BlindInvitationWords.Expired);
        hub.RoutesCalled.Should().BeEmpty();
        host.GetSnapshot().Accounts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, "This invitation has expired.")]
    [InlineData(30, "This invitation works for less than a minute more.")]
    [InlineData(60, "This invitation works for about a minute more.")]
    [InlineData(25 * 60, "This invitation works for about 25 minutes more.")]
    public void TheValidity_IsToldInWords(int secondsLeft, string text)
    {
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

        BlindInvitationWords.ValidFor(now.AddSeconds(secondsLeft), now).Should().Be(text);
    }

    // ---- accounts already here ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASecondInvitationForAHubThatHasAnAccount_IsRefused_WithAClearSentence()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var first = await host.AddFromInvitationAsync(hub.IssueText("Alex"));
        first.Ok.Should().BeTrue(first.Error);
        var second = hub.Issue("Alex again");

        var preview = host.PreviewInvitation(second.ToWireText());
        var added = await host.AddFromInvitationAsync(second.ToWireText());

        preview.CanAdd.Should().BeFalse();
        preview.RefusalKind.Should().Be(BlindInvitationRefusals.AlreadyHere);
        preview.Refusal.Should().Be(BlindInvitationWords.AlreadyHere("hub-a.test")).And.Contain("hub-a.test").And.Contain("already has an account");
        added.Ok.Should().BeFalse();
        added.Error.Should().Be(preview.Refusal);
        host.GetSnapshot().Accounts.Should().ContainSingle();
        hub.StateOf(second).Should().Be("issued", "the second invitation was not redeemed");
    }

    [Fact]
    public async Task AnAccountPairedTheOldWayToTheSameNode_CountsAsAnAccountForIt()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var old = await host.AddAsync("old way");
        await host.PairAsync(rig.Accounts, old.AccountId!, hub.Sync, loaded: false);

        var preview = host.PreviewInvitation(hub.IssueText());

        preview.RefusalKind.Should().Be(BlindInvitationRefusals.AlreadyHere);
    }

    [Fact]
    public async Task AnAccountThatIsBeingRemoved_NoLongerBlocksTheSameHub()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var first = await host.AddFromInvitationAsync(hub.IssueText("Alex"));
        await host.RemoveAsync(first.AccountId!);

        var again = await host.AddFromInvitationAsync(hub.IssueText("Alex"));

        again.Ok.Should().BeTrue(again.Error);
    }

    [Fact]
    public async Task TwoInvitationsForTheSameHubAtOnce_MakeOneAccount()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;
        var one = hub.Issue("One");
        var two = hub.Issue("Two");

        var results = await Task.WhenAll(host.AddFromInvitationAsync(one.ToWireText()), host.AddFromInvitationAsync(two.ToWireText()));

        results.Count(r => r.Ok).Should().Be(1, "the check and the entry are one step");
        host.GetSnapshot().Accounts.Should().ContainSingle();
    }

    [Fact]
    public async Task AnInvitationAndTheOldWaysAdd_AtTheLastPlaceAtOnce_MakeOneAccount()
    {
        var (rig, hub, host) = await StartedAsync(maxAccounts: 1);
        using var _ = rig;
        await using var __ = host;

        var results = await Task.WhenAll(host.AddFromInvitationAsync(hub.IssueText()), host.AddAsync("old way"));

        results.Count(r => r.Ok).Should().Be(1, "the cap is checked in the same step as the entry is made, by both ways of adding");
        host.GetSnapshot().Accounts.Should().ContainSingle();
    }

    [Fact]
    public async Task AnAccountWhoseStateCannotBeRead_ReadsAsUnknown_AndNothingIsAdded()
    {
        using var rig = new InviteRig();
        var hubA = rig.Hub("hub-a.test");
        var hubB = rig.Hub("hub-b.test");
        var failing = new ThrowingStates(rig.Accounts.States);
        await using var host = new BlindAccountHost(new BlindAccountHostOptions
        {
            RootDirectory = rig.Accounts.Root, LegacyDatabasePath = rig.Accounts.LegacyDb, Secrets = rig.Accounts.Secrets, States = failing, Time = rig.Clock,
            PrimaryHandlerFactory = _ => () => rig.Router, DisplayNameFactory = () => "Test device", RootLockWait = TimeSpan.FromMilliseconds(150),
        });
        await host.StartAsync();
        var old = await host.AddAsync("old way");
        old.Ok.Should().BeTrue(old.Error);
        failing.ThrowFor = old.AccountId;
        var invitation = hubB.Issue();

        var preview = host.PreviewInvitation(invitation.ToWireText());
        var added = await host.AddFromInvitationAsync(invitation.ToWireText());

        preview.RefusalKind.Should().Be(BlindInvitationRefusals.Unreadable);
        preview.Refusal.Should().Be(BlindInvitationWords.Unreadable);
        added.Ok.Should().BeFalse();
        hubA.RoutesCalled.Concat(hubB.RoutesCalled).Should().BeEmpty();
        failing.ThrowFor = null;
        host.GetSnapshot().Accounts.Should().ContainSingle();
    }

    private sealed class ThrowingStates(IBlindStateStoreFactory inner) : IBlindStateStoreFactory
    {
        public string? ThrowFor { get; set; }

        public IBlindStateStore For(BlindAccountEntry account) =>
            account.Id == ThrowFor ? throw new UnauthorizedAccessException("the state cannot be read") : inner.For(account);
    }

    // ---- the cap, a damaged list, a repair, an app that is still starting -------------------------------------------------------------------------

    [Fact]
    public void TheDeviceHoldsEightAccountsByDefault() => BlindAccountHost.DefaultMaxAccounts.Should().Be(8);

    [Fact]
    public async Task WhenTheDeviceHoldsAsManyAsItMay_TheInvitationIsRefused_WithTheCap()
    {
        var (rig, hubA, host) = await StartedAsync(maxAccounts: 2);
        using var _ = rig;
        await using var __ = host;
        var hubB = rig.Hub("hub-b.test");
        var hubC = rig.Hub("hub-c.test");
        (await host.AddFromInvitationAsync(hubA.IssueText())).Ok.Should().BeTrue();
        (await host.AddFromInvitationAsync(hubB.IssueText())).Ok.Should().BeTrue();
        var third = hubC.Issue();

        var preview = host.PreviewInvitation(third.ToWireText());
        var added = await host.AddFromInvitationAsync(third.ToWireText());

        preview.RefusalKind.Should().Be(BlindInvitationRefusals.TooMany);
        preview.Refusal.Should().Be("This device holds at most 2 accounts. Remove one first.");
        added.Error.Should().Be(preview.Refusal);
        hubC.RoutesCalled.Should().BeEmpty();
        host.GetSnapshot().Accounts.Should().HaveCount(2);
    }

    [Fact]
    public async Task ADamagedList_BlocksAdding_AndTouchesNoAccount()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        File.WriteAllText(Path.Combine(rig.Accounts.Root, "accounts.json"), "{ this is not json");
        await using var host = rig.Host();
        await host.StartAsync();
        var invitation = hub.Issue();
        var before = AccountRig.Fingerprint(rig.Accounts.Root);

        var preview = host.PreviewInvitation(invitation.ToWireText());
        var added = await host.AddFromInvitationAsync(invitation.ToWireText());

        preview.RefusalKind.Should().Be(BlindInvitationRefusals.Blocked);
        preview.Refusal.Should().Be(host.GetSnapshot().RegistryProblem);
        added.Ok.Should().BeFalse();
        added.Error.Should().Be(host.GetSnapshot().RegistryProblem);
        hub.RoutesCalled.Should().BeEmpty();
        AccountRig.Fingerprint(rig.Accounts.Root).Should().BeEquivalentTo(before, "nothing was written over a damaged list");
    }

    [Fact]
    public async Task ARepairThatWaits_BlocksAdding()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using (var first = rig.Host())
            await first.StartAsync();
        Directory.CreateDirectory(Path.Combine(rig.Accounts.Root, "accounts", new string('a', 32)));   // a folder the list does not name
        await using var host = rig.Host();
        await host.StartAsync();
        host.GetSnapshot().RepairIssues.Should().NotBeEmpty();

        var preview = host.PreviewInvitation(hub.IssueText());
        var added = await host.AddFromInvitationAsync(hub.IssueText());

        preview.RefusalKind.Should().Be(BlindInvitationRefusals.Blocked);
        added.Ok.Should().BeFalse();
        hub.RoutesCalled.Should().BeEmpty();
    }

    [Fact]
    public async Task BeforeTheHostHasStarted_NothingIsAdded()
    {
        using var rig = new InviteRig();
        var hub = rig.Hub();
        await using var host = rig.Host();

        var preview = host.PreviewInvitation(hub.IssueText());
        var added = await host.AddFromInvitationAsync(hub.IssueText());

        preview.RefusalKind.Should().Be(BlindInvitationRefusals.Blocked);
        preview.Refusal.Should().Contain("still starting");
        added.Ok.Should().BeFalse();
        hub.RoutesCalled.Should().BeEmpty();
    }

    [Fact]
    public async Task ANegativeLimit_IsRefusedBeforeAnythingIsMade()
    {
        var (rig, hub, host) = await StartedAsync();
        using var _ = rig;
        await using var __ = host;

        var added = await host.AddFromInvitationAsync(hub.IssueText(), new BlindAccountSettings(new BlindAccountLimits(MaxStoredBytes: -1), false, false));

        added.Ok.Should().BeFalse();
        hub.RoutesCalled.Should().BeEmpty();
        host.GetSnapshot().Accounts.Should().BeEmpty();
    }
}
