using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>
/// The "no contact" warning, one local notification per account: planned three days after the account's last contact, moved only when something it depends on changed, named after the source,
/// not planned for an account that was told to stop, and withdrawn with the account. Account 1 keeps the request id of 2.6.
/// </summary>
public class IosSilenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private const string AnnaId = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";
    private const string BenId = "d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4";

    private static BlindAccountSnapshot Account(BlindAppStatus? status, string name = "Anna", string? id = null, string? inviteId = "i1",
        string phase = BlindAccountPhases.Active, bool pausedByUser = false, BlindPause? pause = null) =>
        new(new BlindAccountEntry(id ?? AnnaId, BlindAccountLayouts.Dir, name, phase, InviteId: inviteId, PausedByUser: pausedByUser), status, Pause: pause);

    private static BlindAppStatus Status(bool paired = true, DateTimeOffset? lastSync = null) =>
        new(Guid.NewGuid(), "iPhone", paired, InitialLoadDone: true, lastSync, null, BlindBackupSchedule.Off, false, null, null, [], [],
            Endpoint: paired ? "https://192.0.2.10:5610" : null);

    private static BlindAccountsSnapshot Snapshot(params BlindAccountSnapshot[] accounts) => new(accounts, BlindDeviceLimits.None, null, true, 8);

    [Fact]
    public void TheAlarm_IsPendingOnlyForAPairedActiveAccount_ThreeDaysAfterTheLastContact()
    {
        BlindSilenceAlarm.Plan(Account(Status(paired: false)), Now).Should().BeNull("an unpaired copy has no node to miss");
        BlindSilenceAlarm.Plan(Account(null), Now).Should().BeNull("an account whose runtime is not up has no status to judge");
        BlindSilenceAlarm.Plan(Account(Status(), phase: BlindAccountPhases.AwaitingConfirmation), Now).Should().BeNull("an account the owner has not confirmed has not been in contact");
        BlindSilenceAlarm.Plan(Account(Status(), phase: BlindAccountPhases.RemovedByOwner), Now).Should().BeNull("the source already knows");

        var notice = BlindSilenceAlarm.Plan(Account(Status(lastSync: Now.AddHours(-1))), Now)!;
        notice.FireAt.Should().Be(Now.AddHours(-1) + TimeSpan.FromDays(3));
        notice.Body.Should().Contain("has not synced");

        BlindSilenceAlarm.Plan(Account(Status()), Now)!.FireAt.Should().Be(Now + TimeSpan.FromDays(3), "never synced: counted from now");
    }

    [Fact]
    public void ASilenceThatAlreadyLasted_IsSaidAgainADayLater_NotAtOnce_AndTheTitleCountsTheDays()
    {
        var notice = BlindSilenceAlarm.Plan(Account(Status(lastSync: Now.AddDays(-5))), Now)!;

        notice.FireAt.Should().Be(Now + BlindSilenceAlarm.RemindAfter);
        notice.Title.Should().Be("No contact with Anna's server for 6 days", "it fires a day from now: six days after the last contact");
    }

    [Fact]
    public void TheNotification_NamesTheSource_ForAnInvitedAccount_AndSaysTheNodeForOneThatWasPairedTheOldWay()
    {
        BlindSilenceAlarm.Plan(Account(Status(lastSync: Now), name: "Anna"), Now)!.Title.Should().Be("No contact with Anna's server for 3 days");
        BlindSilenceAlarm.Plan(Account(Status(lastSync: Now), name: "192.0.2.10", inviteId: null), Now)!.Title.Should().Be("Blind copy: no contact with the node for 3 days");
        BlindSilenceAlarm.Plan(Account(Status(lastSync: Now), name: "Anna"), Now)!.Body.Should().Contain("\"Anna\"");
    }

    [Fact]
    public void AnAccountHeldByALimitOrByThePerson_IsNotSilent_ButOneThatWaitsForWifiStillIs()
    {
        var synced = Status(lastSync: Now.AddDays(-1));
        BlindSilenceAlarm.Plan(Account(synced, pausedByUser: true), Now).Should().BeNull("the person told it to stop");
        BlindSilenceAlarm.Plan(Account(synced, pause: new BlindPause(BlindPauseReasons.Traffic, Now.AddHours(5), Limit: 200L << 20)), Now).Should().BeNull("a daily limit is a pause, not silence");
        BlindSilenceAlarm.Plan(Account(synced, pause: new BlindPause(BlindPauseReasons.MeteredNetwork)), Now).Should().NotBeNull("a phone that never meets Wi-Fi should still be told");
        BlindSilenceAlarm.Plan(Account(synced, pause: new BlindPause(BlindPauseReasons.PowerSaving)), Now).Should().NotBeNull();
    }

    [Fact]
    public void Account1_KeepsTheRequestIdOf26_AndTheOthersCarryTheirId()
    {
        var legacy = new BlindAccountEntry(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");
        var other = new BlindAccountEntry(AnnaId, BlindAccountLayouts.Dir, "Anna");

        BlindSilenceAlarm.RequestId.Should().Be("bmb-blind-silent");
        BlindSilenceAlarm.RequestIdFor(legacy).Should().Be("bmb-blind-silent", "the notification an upgraded phone already has pending is simply moved on");
        BlindSilenceAlarm.RequestIdFor(other).Should().Be("bmb-blind-silent." + AnnaId);
    }

    [Fact]
    public void ThePendingNotificationsOfAccountsThatAreGone_AreFound_AndNothingElseIs()
    {
        var legacy = new BlindAccountEntry(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");
        var kept = new BlindAccountEntry(AnnaId, BlindAccountLayouts.Dir, "Anna");
        var pending = new[] { "bmb-blind-silent", "bmb-blind-silent." + AnnaId, "bmb-blind-silent." + BenId, "something-else", "bmb-blind-silentx" };

        BlindSilenceAlarm.StaleRequestIds(pending, [legacy, kept]).Should().Equal("bmb-blind-silent." + BenId);
        BlindSilenceAlarm.StaleRequestIds(pending, [kept]).Should().Equal("bmb-blind-silent", "bmb-blind-silent." + BenId);
    }

    [Fact]
    public void ThePlanner_MovesANotificationOnlyWhenWhatItDependsOnChanged_AndWithdrawsTheOnesThatAreNotNeeded()
    {
        var planner = new BlindSilencePlanner();
        var anna = Account(Status(lastSync: Now.AddMinutes(-1)), "Anna", AnnaId);
        var ben = Account(Status(lastSync: Now.AddMinutes(-1)), "Ben", BenId);

        var first = planner.Update(Snapshot(anna, ben), Now);
        first.Set.Select(s => s.RequestId).Should().BeEquivalentTo("bmb-blind-silent." + AnnaId, "bmb-blind-silent." + BenId);
        first.Remove.Should().BeEmpty();

        planner.Update(Snapshot(anna, ben), Now.AddSeconds(5)).IsEmpty.Should().BeTrue("a screen that refreshes every few seconds does not keep pushing the warning away");

        var annaSynced = Account(Status(lastSync: Now), "Anna", AnnaId);
        planner.Update(Snapshot(annaSynced, ben), Now.AddSeconds(10)).Set.Select(s => s.RequestId)
            .Should().Equal(["bmb-blind-silent." + AnnaId], "only the account that heard from its node is moved");

        var benPaused = Account(Status(lastSync: Now.AddMinutes(-1)), "Ben", BenId, pausedByUser: true);
        planner.Update(Snapshot(annaSynced, benPaused), Now.AddSeconds(20)).Remove.Should().Equal("bmb-blind-silent." + BenId);

        planner.Update(Snapshot(annaSynced), Now.AddSeconds(30)).Remove.Should().Equal(["bmb-blind-silent." + BenId], "an account that was removed takes its notification with it");
    }

    [Fact]
    public void ADamagedList_MovesAndWithdrawsNothing_AndForgettingPlansEverythingAgain()
    {
        var planner = new BlindSilencePlanner();
        var anna = Account(Status(lastSync: Now), "Anna", AnnaId);
        planner.Update(Snapshot(anna), Now);

        planner.Update(new BlindAccountsSnapshot([], BlindDeviceLimits.None, "The list of accounts is damaged.", false, 8), Now).IsEmpty.Should().BeTrue();

        planner.Forget();
        planner.Update(Snapshot(anna), Now).Set.Should().HaveCount(1, "alerts were just allowed: everything is queued again");
    }
}
