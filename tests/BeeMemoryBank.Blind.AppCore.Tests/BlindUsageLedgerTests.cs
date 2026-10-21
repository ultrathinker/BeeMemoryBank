using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>What an account used per local day: counted, saved, never refilled by a clock that is set back, and right across midnight and daylight-saving days.</summary>
public sealed class BlindUsageLedgerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb-ledger-tests", Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_dir, "usage.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private BlindUsageLedger Ledger(FakeClock clock) => new(File, clock);

    [Fact]
    public void ItCountsRequestsAndBytesOfTheDay()
    {
        var clock = new FakeClock(Noon);
        var ledger = Ledger(clock);

        ledger.AddRequest();
        ledger.AddRequest();
        ledger.AddOut(300);
        ledger.AddIn(5000);

        var today = ledger.UsageToday();
        today.Day.Should().Be(new DateOnly(2026, 10, 21));
        today.Requests.Should().Be(2);
        today.BytesOut.Should().Be(300);
        today.BytesIn.Should().Be(5000);
        today.Traffic.Should().Be(5300);
    }

    [Fact]
    public void WhatIsFlushed_IsThereAfterARestart_AndAFlushIsAtomic()
    {
        var clock = new FakeClock(Noon);
        var first = Ledger(clock);
        first.AddRequest();
        first.AddIn(1234);
        first.Flush();

        var second = Ledger(clock);

        second.UsageToday().Requests.Should().Be(1);
        second.UsageToday().BytesIn.Should().Be(1234);
        Directory.GetFiles(_dir).Select(Path.GetFileName).Should().Equal("usage.json");
    }

    [Fact]
    public void AnUnflushedCountOfASmallRound_IsNotWrittenUntilTheFlushOrTheTimeOrTheBytesPass()
    {
        var clock = new FakeClock(Noon);
        var ledger = Ledger(clock);
        ledger.UsageToday();                  // the day's high-water date is saved at once
        var written = System.IO.File.ReadAllText(File);

        ledger.AddRequest();
        ledger.AddIn(1000);
        System.IO.File.ReadAllText(File).Should().Be(written, "a few bytes wait for the end of the round");

        clock.Advance(BlindUsageLedger.FlushEvery + TimeSpan.FromSeconds(1));
        ledger.AddIn(1);
        System.IO.File.ReadAllText(File).Should().NotBe(written, "30 seconds passed: a crash loses at most that much");
        Ledger(clock).UsageToday().BytesIn.Should().Be(1001);
    }

    [Fact]
    public void FourMegabytes_ForceAFlush()
    {
        var clock = new FakeClock(Noon);
        var ledger = Ledger(clock);
        ledger.UsageToday();

        ledger.AddIn(BlindUsageLedger.FlushEveryBytes - 10);
        Ledger(clock).UsageToday().BytesIn.Should().Be(0, "not yet 4 MB");
        ledger.AddIn(10);

        Ledger(clock).UsageToday().BytesIn.Should().Be(BlindUsageLedger.FlushEveryBytes, "4 MB reached: written without waiting for the round to end");
    }

    [Fact]
    public void ANewLocalDay_StartsAtZero_AndTheOldDayIsKept()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 21, 23, 50, 0, TimeSpan.Zero));
        var ledger = Ledger(clock);
        ledger.AddIn(500);

        clock.Advance(TimeSpan.FromMinutes(20));

        ledger.Today().Should().Be(new DateOnly(2026, 10, 22));
        ledger.UsageToday().BytesIn.Should().Be(0);
        ledger.Usage(new DateOnly(2026, 10, 21)).BytesIn.Should().Be(500);
        ledger.History().Select(d => d.Day).Should().Equal(new DateOnly(2026, 10, 21));
    }

    [Fact]
    public void TheDay_IsTheDevicesLocalDay_NotUtcs()
    {
        // 03:00 UTC on 22 October is still the evening of 21 October at UTC-5.
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 22, 3, 0, 0, TimeSpan.Zero), TestZones.Minus5);
        var ledger = Ledger(clock);

        ledger.Today().Should().Be(new DateOnly(2026, 10, 21));
        ledger.NextLocalMidnight().Should().Be(new DateTimeOffset(2026, 10, 22, 5, 0, 0, TimeSpan.Zero), "00:00 at UTC-5 is 05:00 UTC");
    }

    [Fact]
    public void AClockSetBack_DoesNotRefillTheDay()
    {
        var clock = new FakeClock(Noon);
        var ledger = Ledger(clock);
        ledger.AddIn(9000);

        clock.Set(Noon.AddHours(-5));          // earlier the same day
        ledger.UsageToday().BytesIn.Should().Be(9000);

        clock.Set(Noon.AddDays(-3));           // an earlier date: the day does not move back
        ledger.Today().Should().Be(new DateOnly(2026, 10, 21));
        ledger.UsageToday().BytesIn.Should().Be(9000, "setting the clock back must not open a new, empty day");
        ledger.AddIn(1);
        ledger.UsageToday().BytesIn.Should().Be(9001, "what is counted while the clock is wrong still counts for the day it belongs to");
        ledger.NextLocalMidnight().Should().Be(new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero), "the lift is when the high-water day ends, not three days earlier");
    }

    [Fact]
    public void AClockSetBack_AcrossARestart_DoesNotRefillTheDayEither()
    {
        var clock = new FakeClock(Noon);
        var first = Ledger(clock);
        first.AddIn(9000);
        first.Flush();

        clock.Set(Noon.AddDays(-2));
        var second = Ledger(clock);

        second.Today().Should().Be(new DateOnly(2026, 10, 21), "the high-water date is saved with the file");
        second.UsageToday().BytesIn.Should().Be(9000);
    }

    [Fact]
    public void ATimeZoneChange_ThatMakesTheDateEarlier_DoesNotMoveTheDayBack()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 21, 2, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        var ledger = Ledger(clock);
        ledger.AddIn(700);
        ledger.Today().Should().Be(new DateOnly(2026, 10, 21));

        clock.SetZone(TestZones.Minus5);   // the same instant is now the evening of the 20th

        ledger.Today().Should().Be(new DateOnly(2026, 10, 21));
        ledger.UsageToday().BytesIn.Should().Be(700);
    }

    [Fact]
    public void ADaylightSavingDay_IsOneDay_WhateverItsLength()
    {
        // The 25-hour day: clocks go back at 03:00 (local summer time) on 25 October.
        var clock = new FakeClock(new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.Zero), TestZones.Dst);   // 00:30 local, 25 October (UTC+2)
        var ledger = Ledger(clock);
        ledger.AddIn(100);
        ledger.Today().Should().Be(new DateOnly(2026, 10, 25));

        clock.Advance(TimeSpan.FromHours(24));   // 24 hours later it is 23:30 local (UTC+1 by then): still the 25th, which had 25 hours
        ledger.Today().Should().Be(new DateOnly(2026, 10, 25));
        ledger.UsageToday().BytesIn.Should().Be(100, "the long day is not split");

        clock.Advance(TimeSpan.FromHours(1));    // 00:30 local on the 26th
        ledger.Today().Should().Be(new DateOnly(2026, 10, 26));
        ledger.UsageToday().BytesIn.Should().Be(0);

        // The 23-hour day: clocks go forward at 02:00 (local winter time) on 29 March.
        var spring = new FakeClock(new DateTimeOffset(2026, 3, 28, 23, 30, 0, TimeSpan.Zero), TestZones.Dst);   // 00:30 local, 29 March (UTC+1)
        var springLedger = new BlindUsageLedger(Path.Combine(_dir, "spring", "usage.json"), spring);
        springLedger.AddIn(5);
        spring.Advance(TimeSpan.FromHours(22));   // 22:30 local: still the 29th
        springLedger.Today().Should().Be(new DateOnly(2026, 3, 29));
        spring.Advance(TimeSpan.FromHours(1));    // 00:30 local on the 30th: the day was 23 hours long
        springLedger.Today().Should().Be(new DateOnly(2026, 3, 30));
    }

    [Fact]
    public void TheMidnightThatEndsADstDay_IsFoundInTheRightZone()
    {
        var fall = new FakeClock(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero), TestZones.Dst);
        // 26 October 00:00 local is UTC+1 by then: 23:00 UTC on the 25th (the day had 25 hours).
        Ledger(fall).NextLocalMidnight().Should().Be(new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero));

        var spring = new FakeClock(new DateTimeOffset(2026, 3, 29, 10, 0, 0, TimeSpan.Zero), TestZones.Dst);
        // 30 March 00:00 local is UTC+2: 22:00 UTC on the 29th (the day had 23 hours).
        new BlindUsageLedger(Path.Combine(_dir, "spring", "usage.json"), spring).NextLocalMidnight().Should().Be(new DateTimeOffset(2026, 3, 29, 22, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void AZoneThatSkipsItsMidnight_BeginsTheDayAnHourLater()
    {
        // On 29 March 2026 this zone goes from 00:00 straight to 01:00: "00:00" does not exist.
        var start = BlindDayMath.StartOf(new DateOnly(2026, 3, 29), TestZones.SkipsMidnight);
        TimeZoneInfo.ConvertTime(start, TestZones.SkipsMidnight).Hour.Should().Be(1);
        TimeZoneInfo.ConvertTime(start, TestZones.SkipsMidnight).Date.Should().Be(new DateTime(2026, 3, 29));

        var clock = new FakeClock(new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero), TestZones.SkipsMidnight);
        Ledger(clock).NextLocalMidnight().Should().Be(start);
    }

    [Fact]
    public void OnlyTheLast31DaysAreKept()
    {
        var clock = new FakeClock(Noon);
        var ledger = Ledger(clock);
        for (var day = 0; day < 40; day++)
        {
            ledger.AddRequest();
            ledger.AddIn(day + 1);
            clock.Advance(TimeSpan.FromDays(1));
        }
        ledger.Flush();

        var days = Ledger(clock).History();
        days.Should().HaveCountLessThanOrEqualTo(BlindUsageLedger.KeepDays);
        days.Select(d => d.Day).Should().BeInAscendingOrder();
        days.Min(d => d.Day).Should().BeOnOrAfter(Ledger(clock).Today().AddDays(-(BlindUsageLedger.KeepDays - 1)));
    }

    [Fact]
    public void TheAverage_IsOverTheDaysSinceTheFirstRecord_UpToSeven()
    {
        var clock = new FakeClock(Noon);
        var ledger = Ledger(clock);
        ledger.AverageTrafficPerDay().Should().BeNull("nothing was counted yet");

        ledger.AddIn(3000);
        clock.Advance(TimeSpan.FromDays(1));
        ledger.AddIn(1000);

        ledger.AverageTrafficPerDay().Should().Be(2000, "two days old: averaged over two, not seven");

        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromDays(1));
            ledger.AddIn(700);
        }
        ledger.AverageTrafficPerDay().Should().Be(700, "the last seven days are all 700");
    }

    [Fact]
    public void ADamagedFile_IsNotAFreshBudget_IsNeverOverwritten_AndTheLedgerSaysItIsUnavailable()
    {
        // Hardening part 2 replaced "a damaged file starts the ledger empty": an existing file that cannot be trusted is evidence, not a fresh install.
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, "{ not json");
        var clock = new FakeClock(Noon);

        var ledger = Ledger(clock);

        ledger.IsAvailable.Should().BeFalse();
        ledger.AddRequest();
        ledger.Flush();
        System.IO.File.ReadAllText(File).Should().Be("{ not json", "the damaged file is kept exactly as it was");
        Ledger(clock).IsAvailable.Should().BeFalse("a restart finds the same damage: it does not start fresh");
    }

    [Fact]
    public void ACountIsNeverNegative()
    {
        var ledger = Ledger(new FakeClock(Noon));
        var act = () => ledger.AddIn(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ManyThreadsCountingAtOnce_LoseNothing()
    {
        var ledger = Ledger(new FakeClock(Noon));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                ledger.AddRequest();
                ledger.AddIn(10);
                ledger.AddOut(5);
            }
        })));

        var today = ledger.UsageToday();
        today.Requests.Should().Be(4000);
        today.BytesIn.Should().Be(40000);
        today.BytesOut.Should().Be(20000);
    }

    // ---- the stored bytes and the device -----------------------------------------------------------------------------------------------

    [Fact]
    public void StoredBytes_AreWalkedAndCachedFor60Seconds_AndAJobRefreshesThem()
    {
        var folder = Path.Combine(_dir, "owned");
        Directory.CreateDirectory(Path.Combine(folder, "media"));
        System.IO.File.WriteAllBytes(Path.Combine(folder, "media", "a.bin"), new byte[1000]);
        System.IO.File.WriteAllBytes(Path.Combine(folder, "beememorybank.db"), new byte[2000]);
        System.IO.File.WriteAllBytes(Path.Combine(folder, "beememorybank.db-wal"), new byte[300]);
        var clock = new FakeClock(Noon);
        var stored = new BlindStoredBytes(() => [folder], clock);

        stored.Get().Should().Be(3300);

        System.IO.File.WriteAllBytes(Path.Combine(folder, "media", "b.bin"), new byte[500]);
        stored.Get().Should().Be(3300, "the number is cached for a minute");
        clock.Advance(TimeSpan.FromSeconds(59));
        stored.Get().Should().Be(3300);
        clock.Advance(TimeSpan.FromSeconds(2));
        stored.Get().Should().Be(3800, "after a minute it is walked again");

        System.IO.File.WriteAllBytes(Path.Combine(folder, "media", "c.bin"), new byte[200]);
        stored.Refresh().Should().Be(4000, "a finished job measures at once");
    }

    [Fact]
    public void StoredBytes_OfAnAccountInTheAppsRoot_LeaveOutTheOtherAccountsAndStrangers()
    {
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(Path.Combine(root, "accounts", "someone", "media"));
        System.IO.File.WriteAllBytes(Path.Combine(root, "accounts", "someone", "media", "other.bin"), new byte[10_000]);
        Directory.CreateDirectory(Path.Combine(root, "blind-backups"));
        System.IO.File.WriteAllBytes(Path.Combine(root, "blind-backups", "backup.bin"), new byte[400]);
        Directory.CreateDirectory(Path.Combine(root, "blind-replica"));
        System.IO.File.WriteAllBytes(Path.Combine(root, "blind-replica", "replica.part"), new byte[600]);
        Directory.CreateDirectory(Path.Combine(root, "baselines", "current"));
        System.IO.File.WriteAllBytes(Path.Combine(root, "baselines", "current", "baseline.bin"), new byte[200]);
        System.IO.File.WriteAllBytes(Path.Combine(root, "beememorybank.db"), new byte[1000]);
        System.IO.File.WriteAllBytes(Path.Combine(root, "unrelated.txt"), new byte[5000]);
        System.IO.File.WriteAllBytes(Path.Combine(root, "accounts.json"), new byte[77]);
        var stored = new BlindStoredBytes(() => BlindMobileServices.StoredRoots(root, Path.Combine(root, "beememorybank.db")), new FakeClock(Noon));

        stored.Get().Should().Be(400 + 600 + 200 + 1000, "backups, the replica work folder with its partial download, baselines and the database: not other accounts, not strangers");
    }

    [Fact]
    public void TheDevice_SumsEveryAccount_Today_AndItsDayOnlyMovesForward()
    {
        var clock = new FakeClock(Noon);
        var a = new BlindUsageLedger(Path.Combine(_dir, "a", "usage.json"), clock);
        var b = new BlindUsageLedger(Path.Combine(_dir, "b", "usage.json"), clock);
        var device = new BlindDeviceUsage(clock, Path.Combine(_dir, "device-usage.json"), () => new BlindDeviceLimits(MaxTrafficBytesPerDay: 1));
        device.Register("a", a, new BlindStoredBytes(() => [], clock));
        device.Register("b", b, new BlindStoredBytes(() => [], clock));
        a.AddIn(100); a.AddOut(10); a.AddRequest();
        b.AddIn(1000); b.AddRequest(); b.AddRequest();

        device.TrafficToday().Should().Be(1110);
        device.RequestsToday().Should().Be(3);
        device.Limits.MaxTrafficBytesPerDay.Should().Be(1);
        device.AccountCount.Should().Be(2);

        device.Unregister("a");
        device.TrafficToday().Should().Be(1000, "a removed account no longer counts");

        clock.Set(Noon.AddDays(-4));
        device.Today().Should().Be(new DateOnly(2026, 10, 21), "the device's day only moves forward too");
        new BlindDeviceUsage(clock, Path.Combine(_dir, "device-usage.json")).Today().Should().Be(new DateOnly(2026, 10, 21), "also across a restart");
    }
}
