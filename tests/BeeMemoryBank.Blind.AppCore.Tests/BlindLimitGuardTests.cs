using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>The guard's decisions, table-driven: each reason, the account against the device, the day's end, the switches with a device that cannot say, zero as a pause.</summary>
public sealed class BlindLimitGuardTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb-guard-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class MutablePolicy : IBlindAccountPolicySource
    {
        public BlindAccountPolicy Current { get; set; } = BlindAccountPolicy.Unlimited;
    }

    private sealed class Rig
    {
        public required FakeClock Clock { get; init; }
        public required MutablePolicy Policy { get; init; }
        public required BlindUsageLedger Ledger { get; init; }
        public required long Stored { get; set; }
        public required BlindDeviceUsage Device { get; init; }
        public required FakeBlindDeviceConditions Conditions { get; init; }
        public required BlindLimitGuard Guard { get; init; }
        public BlindDeviceLimits DeviceLimits { get; set; } = BlindDeviceLimits.None;
        public required BlindUsageLedger OtherAccount { get; init; }
    }

    private Rig NewRig(TimeZoneInfo? zone = null)
    {
        var clock = new FakeClock(Noon, zone);
        var policy = new MutablePolicy();
        var ledger = new BlindUsageLedger(Path.Combine(_dir, "a", "usage.json"), clock);
        var other = new BlindUsageLedger(Path.Combine(_dir, "b", "usage.json"), clock);
        var conditions = new FakeBlindDeviceConditions { IsMetered = false, IsPowerSaving = false };
        Rig? rig = null;
        var device = new BlindDeviceUsage(clock, null, () => rig!.DeviceLimits);
        // The account's stored bytes are the size of one file in a folder of its own, which the test sets (see Hold).
        var folder = Path.Combine(_dir, "data");
        Directory.CreateDirectory(folder);
        var diskStored = new BlindStoredBytes(() => [folder], clock);
        device.Register("a", ledger, diskStored);
        device.Register("b", other, new BlindStoredBytes(() => [], clock));
        var guard = new BlindLimitGuard(policy, ledger, diskStored, device, conditions);
        rig = new Rig { Clock = clock, Policy = policy, Ledger = ledger, Stored = 0, Device = device, Conditions = conditions, Guard = guard, OtherAccount = other };
        _stored = diskStored;
        _file = Path.Combine(folder, "blob.bin");
        return rig;
    }

    private BlindStoredBytes _stored = null!;
    private string _file = "";

    /// <summary>Makes the account hold exactly <paramref name="bytes"/> (a single file in its folder) and measures it at once.</summary>
    private void Hold(long bytes)
    {
        using (var stream = new FileStream(_file, FileMode.Create)) stream.SetLength(bytes);
        _stored.Refresh();
    }

    private static BlindAccountPolicy Policy(long? stored = null, long? traffic = null, int? requests = null, bool unmetered = false, bool saver = false, bool paused = false) =>
        new(new BlindAccountLimits(stored, traffic, requests), unmetered, saver, paused);

    [Fact]
    public void NoPolicy_NoPause()
    {
        var rig = NewRig();
        rig.Ledger.AddRequest();
        rig.Ledger.AddIn(1L << 30);
        Hold(5L << 30);

        rig.Guard.Check().Should().BeNull("a copy that was never given a limit is never paused (2.6 behaviour)");
    }

    [Fact]
    public void PausedByYou_HoldsTheAccount_WithNoEnd()
    {
        var rig = NewRig();
        rig.Policy.Current = Policy(paused: true);

        var pause = rig.Guard.Check()!;

        pause.Reason.Should().Be(BlindPauseReasons.PausedByYou);
        pause.Until.Should().BeNull();
    }

    [Fact]
    public void TheDailyTrafficLimit_HoldsUntilTheNextLocalMidnight()
    {
        var rig = NewRig();
        rig.Policy.Current = Policy(traffic: 1000);
        rig.Ledger.AddIn(999);
        rig.Guard.Check().Should().BeNull("1 byte of the day is left");

        rig.Ledger.AddOut(1);
        var pause = rig.Guard.Check()!;

        pause.Reason.Should().Be(BlindPauseReasons.Traffic);
        pause.Limit.Should().Be(1000);
        pause.Until.Should().Be(new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero));

        rig.Clock.NextDay();
        rig.Guard.Check().Should().BeNull("the new day has its own allowance");
    }

    [Fact]
    public void TheDailyRequestLimit_AllowsExactlyThatMany_ThenHolds()
    {
        var rig = NewRig();
        rig.Policy.Current = Policy(requests: 3);
        for (var i = 0; i < 3; i++)
        {
            rig.Guard.Check().Should().BeNull($"request {i + 1} of 3");
            rig.Ledger.AddRequest();
        }

        var pause = rig.Guard.Check()!;

        pause.Reason.Should().Be(BlindPauseReasons.Requests);
        pause.Limit.Should().Be(3);
        pause.Until.Should().NotBeNull();
    }

    [Fact]
    public void TheStorageLimit_HoldsWhenFull_WithNoEnd_AndAPlannedDownloadThatCannotFitIsRefusedBeforeItStarts()
    {
        var rig = NewRig();
        rig.Policy.Current = Policy(stored: 1000);
        Hold(400);
        rig.Guard.Check().Should().BeNull();
        rig.Guard.Check(plannedBytes: 600).Should().BeNull("exactly fits");

        var tooBig = rig.Guard.Check(plannedBytes: 601)!;
        tooBig.Reason.Should().Be(BlindPauseReasons.Disk);
        tooBig.Until.Should().BeNull();
        tooBig.Needed.Should().Be(1001, "stored bytes plus the download");
        tooBig.Limit.Should().Be(1000);

        Hold(1000);
        rig.Guard.Check()!.Reason.Should().Be(BlindPauseReasons.Disk, "a full store holds the account");
    }

    [Theory]
    [InlineData(true, null, true)]     // metered: wait
    [InlineData(null, null, true)]     // the device cannot say: wait (fail closed toward the data plan)
    [InlineData(false, null, false)]   // unmetered: go
    public void TheUnmeteredSwitch_WaitsUnlessTheDeviceSaysTheNetworkIsNotMetered(bool? metered, bool? unused, bool paused)
    {
        _ = unused;
        var rig = NewRig();
        rig.Conditions.IsMetered = metered;
        rig.Policy.Current = Policy(unmetered: true);

        var pause = rig.Guard.Check();

        (pause is not null).Should().Be(paused);
        if (paused)
        {
            pause!.Reason.Should().Be(BlindPauseReasons.MeteredNetwork);
            pause.ConditionUnknown.Should().Be(metered is null, "the card says whether the device said so or could not tell");
            pause.Until.Should().BeNull();
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(null, true)]
    [InlineData(false, false)]
    public void TheBatterySaverSwitch_WaitsUnlessTheDeviceSaysItIsNotSaving(bool? saving, bool paused)
    {
        var rig = NewRig();
        rig.Conditions.IsPowerSaving = saving;
        rig.Policy.Current = Policy(saver: true);

        var pause = rig.Guard.Check();

        (pause is not null).Should().Be(paused);
        if (paused) pause!.Reason.Should().Be(BlindPauseReasons.PowerSaving);
    }

    [Fact]
    public void ASwitchThatIsOff_NeverAsksTheDevice()
    {
        var rig = NewRig();
        rig.Conditions.IsMetered = true;
        rig.Conditions.IsPowerSaving = true;
        rig.Policy.Current = Policy(unmetered: false, saver: false);

        rig.Guard.Check().Should().BeNull("metered and saving power, but the person asked for neither switch");
    }

    [Fact]
    public void ZeroMeansPaused_NeverUnlimited()
    {
        var rig = NewRig();

        rig.Policy.Current = Policy(traffic: 0);
        rig.Guard.Check()!.Should().Match<BlindPause>(p => p.Reason == BlindPauseReasons.Traffic && p.Until == null && p.Limit == 0);
        rig.Policy.Current = Policy(requests: 0);
        rig.Guard.Check()!.Reason.Should().Be(BlindPauseReasons.Requests);
        rig.Policy.Current = Policy(stored: 0);
        rig.Guard.Check()!.Reason.Should().Be(BlindPauseReasons.Disk);
        rig.Policy.Current = Policy();
        rig.DeviceLimits = new BlindDeviceLimits(MaxTrafficBytesPerDay: 0);
        rig.Guard.Check()!.Reason.Should().Be(BlindPauseReasons.DeviceTraffic);
        rig.DeviceLimits = new BlindDeviceLimits(MaxStoredBytes: 0);
        rig.Guard.Check()!.Reason.Should().Be(BlindPauseReasons.DeviceDisk);
        rig.DeviceLimits = BlindDeviceLimits.None;
        rig.Guard.Check().Should().BeNull("no limit at all is the only unlimited");
    }

    [Fact]
    public void TheDeviceLimit_CountsEveryAccount_SoAnAccountWithinItsOwnLimitCanStillBeHeld()
    {
        var rig = NewRig();
        rig.Policy.Current = Policy(traffic: 1L << 30);   // this account has plenty of its own allowance
        rig.DeviceLimits = new BlindDeviceLimits(MaxTrafficBytesPerDay: 5000);
        rig.Ledger.AddIn(1000);
        rig.Guard.Check().Should().BeNull();

        rig.OtherAccount.AddIn(4000);            // the other account on the same device uses the rest of the device's day

        var pause = rig.Guard.Check()!;
        pause.Reason.Should().Be(BlindPauseReasons.DeviceTraffic);
        pause.Limit.Should().Be(5000);
        pause.Until.Should().Be(new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TheDeviceStorageLimit_CountsTheBytesOfEveryAccount()
    {
        var rig = NewRig();
        rig.DeviceLimits = new BlindDeviceLimits(MaxStoredBytes: 1000);
        Hold(999);
        rig.Guard.Check().Should().BeNull();
        Hold(1000);

        rig.Guard.Check()!.Reason.Should().Be(BlindPauseReasons.DeviceDisk);
    }

    [Fact]
    public void ThePersonsOwnWordsComeFirst_ThenTheAccount_ThenTheDevice()
    {
        var rig = NewRig();
        rig.Conditions.IsMetered = true;
        rig.Ledger.AddIn(10_000);
        rig.Ledger.AddRequest();
        Hold(5000);
        rig.DeviceLimits = new BlindDeviceLimits(MaxStoredBytes: 1, MaxTrafficBytesPerDay: 1);
        rig.Policy.Current = Policy(stored: 1, traffic: 1, requests: 1, unmetered: true, paused: true);

        var order = new List<string>();
        while (rig.Guard.Check() is { } pause)
        {
            order.Add(pause.Reason);
            rig.Policy.Current = pause.Reason switch
            {
                BlindPauseReasons.PausedByYou => rig.Policy.Current with { PausedByUser = false },
                BlindPauseReasons.MeteredNetwork => rig.Policy.Current with { UnmeteredOnly = false },
                BlindPauseReasons.Traffic => rig.Policy.Current with { Limits = rig.Policy.Current.Limits with { MaxTrafficBytesPerDay = null } },
                BlindPauseReasons.Requests => rig.Policy.Current with { Limits = rig.Policy.Current.Limits with { MaxRequestsPerDay = null } },
                BlindPauseReasons.Disk => rig.Policy.Current with { Limits = rig.Policy.Current.Limits with { MaxStoredBytes = null } },
                BlindPauseReasons.DeviceTraffic => Clear(rig, traffic: true),
                _ => Clear(rig, traffic: false),
            };
        }

        order.Should().Equal(BlindPauseReasons.PausedByYou, BlindPauseReasons.MeteredNetwork, BlindPauseReasons.Traffic, BlindPauseReasons.Requests,
            BlindPauseReasons.Disk, BlindPauseReasons.DeviceTraffic, BlindPauseReasons.DeviceDisk);
    }

    private static BlindAccountPolicy Clear(Rig rig, bool traffic)
    {
        rig.DeviceLimits = traffic ? rig.DeviceLimits with { MaxTrafficBytesPerDay = null } : rig.DeviceLimits with { MaxStoredBytes = null };
        return rig.Policy.Current;
    }

    [Fact]
    public void AChangedSetting_TakesEffectAtOnce_NothingIsCached_ButTheStoredBytes()
    {
        var rig = NewRig();
        rig.Ledger.AddIn(500);
        rig.Policy.Current = Policy(traffic: 400);
        rig.Guard.Check()!.Reason.Should().Be(BlindPauseReasons.Traffic);

        rig.Policy.Current = Policy(traffic: 4000);

        rig.Guard.Check().Should().BeNull("a raised limit lifts the pause on the next question");
    }

    [Fact]
    public void ADailyPause_EndsAtMidnightInTheDevicesZone_AcrossADstDay()
    {
        var rig = NewRig(TestZones.Dst);
        rig.Clock.Set(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero));   // the 25-hour day
        rig.Policy.Current = Policy(requests: 1);
        rig.Ledger.AddRequest();

        rig.Guard.Check()!.Until.Should().Be(new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero), "00:00 local on the 26th is 23:00 UTC after the clocks went back");
    }
}
