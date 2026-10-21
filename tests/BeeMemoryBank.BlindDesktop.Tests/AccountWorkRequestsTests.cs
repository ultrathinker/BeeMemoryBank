using BeeMemoryBank.BlindDesktop.Scheduling;
using BeeMemoryBank.BlindDesktop.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>What one account's panel asks of the device loop: its requests reach THAT account's controller, the loop's answers come back to that panel only, and the retry of a failed first load is told from the loop's plan.</summary>
public sealed class AccountWorkRequestsTests
{
    private static readonly BlindDeviceSchedulerOptions Options = new()
    {
        Tick = TimeSpan.FromMilliseconds(20), FirstLoadRetry = TimeSpan.FromSeconds(30), FirstLoadRetryMax = TimeSpan.FromMinutes(2),
    };

    private sealed class Rig
    {
        public FakeAccounts Accounts { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public BlindDeviceScheduler Scheduler { get; }
        public (BlindAccountEntry Entry, FakeApp App) One { get; }
        public (BlindAccountEntry Entry, FakeApp App) Two { get; }

        public Rig()
        {
            One = Accounts.AddAccount("Berlin office");
            Two = Accounts.AddAccount("Paris");
            Scheduler = new BlindDeviceScheduler(Accounts, Time, Options);
        }

        public AccountWorkRequests For(BlindAccountEntry entry) => new(Scheduler, entry.Id, Options);
    }

    [Fact]
    public async Task ASyncRequest_ReachesTheControllerOfThatAccountOnly()
    {
        var rig = new Rig();
        await rig.Scheduler.TickAsync();   // the first pass looks at both accounts
        var (onePasses, twoPasses) = (rig.One.App.SyncCalls, rig.Two.App.SyncCalls);

        rig.For(rig.One.Entry).RequestSync();
        await rig.Scheduler.TickAsync();

        rig.One.App.SyncCalls.Should().Be(onePasses + 1);
        rig.Two.App.SyncCalls.Should().Be(twoPasses, "the other account was not asked");
    }

    [Fact]
    public async Task ABackupRequest_RunsTheLongJobWithForce_ForThatAccount()
    {
        var rig = new Rig();
        await rig.Scheduler.TickAsync();
        var before = rig.One.App.HeavyForce.Count;

        rig.For(rig.One.Entry).RequestBackup();
        await rig.Scheduler.TickAsync();

        rig.One.App.HeavyForce.Skip(before).Should().Contain(true, "Back up now is a forced backup");
        rig.Two.App.HeavyForce.Should().NotContain(true);
    }

    [Fact]
    public async Task TheLoopsAnswerToAUserRequest_ComesBackToThePanelOfThatAccountOnly()
    {
        var rig = new Rig();
        await rig.Scheduler.TickAsync();
        var forOne = new List<string>();
        var forTwo = new List<string>();
        var one = rig.For(rig.One.Entry);
        var two = rig.For(rig.Two.Entry);
        one.UserJobReported += forOne.Add;
        two.UserJobReported += forTwo.Add;
        rig.One.App.SyncResult = "Synced.";

        one.RequestSync();
        await rig.Scheduler.TickAsync();

        forOne.Should().Equal("Synced.");
        forTwo.Should().BeEmpty("the other account's panel is not told about this account's job");
    }

    [Fact]
    public async Task AfterTheLastListenerLeaves_NothingIsDeliveredAnyMore()
    {
        var rig = new Rig();
        await rig.Scheduler.TickAsync();
        var got = new List<string>();
        var requests = rig.For(rig.One.Entry);
        Action<string> handler = got.Add;
        requests.UserJobReported += handler;
        requests.UserJobReported -= handler;

        requests.RequestSync();
        await rig.Scheduler.TickAsync();

        got.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedFirstLoad_ShowsItsNextTry_FromTheLoopsPlan_WithTheWaitThatLedToIt()
    {
        var rig = new Rig();
        rig.One.App.Status = FakeApp.StatusOf(paired: true, loaded: false);
        rig.One.App.HeavyResult = "First load failed: no answer: connection refused";
        var requests = rig.For(rig.One.Entry);
        requests.FirstLoadRetry.Should().BeNull("nothing failed yet");

        await rig.Scheduler.TickAsync();

        var retry = requests.FirstLoadRetry;
        retry.Should().NotBeNull();
        retry!.Every.Should().Be(TimeSpan.FromSeconds(30));
        retry.Attempts.Should().Be(1);
        retry.AtCeiling.Should().BeFalse();
        retry.NextAt.Should().Be(rig.Time.GetUtcNow() + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TheWaitDoubles_UntilItReachesTheCeiling_AndThenTheScreenSaysItKeepsTrying()
    {
        var rig = new Rig();
        rig.One.App.Status = FakeApp.StatusOf(paired: true, loaded: false);
        rig.One.App.HeavyResult = "First load failed: no answer: connection refused";
        var requests = rig.For(rig.One.Entry);

        FirstLoadRetryInfo? last = null;
        for (var i = 0; i < 6; i++)
        {
            await rig.Scheduler.TickAsync();
            last = requests.FirstLoadRetry;
            rig.Time.Advance(last!.Every + TimeSpan.FromSeconds(1));
        }

        last!.Every.Should().Be(TimeSpan.FromMinutes(2), "30 s, 1 min, 2 min, and then it stays at the ceiling");
        last.AtCeiling.Should().BeTrue();
        last.Attempts.Should().BeGreaterThan(2);
    }

    [Fact]
    public async Task APause_IsNotAFailedLoad_NoRetryIsShown()
    {
        var rig = new Rig();
        rig.One.App.Status = FakeApp.StatusOf(paired: true, loaded: false);
        rig.One.App.HeavyResult = "Paused: daily traffic limit reached (200 MB). Resumes at 00:00.";

        await rig.Scheduler.TickAsync();

        rig.For(rig.One.Entry).FirstLoadRetry.Should().BeNull();
    }
}
