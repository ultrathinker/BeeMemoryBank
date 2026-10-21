using System.Collections.Concurrent;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Limits through the whole account: a request limit that stops a sync round and resumes tomorrow, a load far bigger than a day's allowance that stops and goes on by byte range, a
/// device limit that two accounts share, the switches with a device that cannot say, a clock that is set back - and that a pause is never a failure and is told to the source once.
/// </summary>
public sealed class BlindLimitsFlowTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private readonly AccountRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private sealed class Router(params (string Authority, HttpMessageHandler Handler)[] routes) : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpMessageInvoker> _routes = routes.ToDictionary(r => r.Authority, r => new HttpMessageInvoker(r.Handler, disposeHandler: false), StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            _routes.TryGetValue(request.RequestUri!.Authority, out var invoker)
                ? invoker.SendAsync(request, ct)
                : Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private sealed class Forward(HttpMessageHandler target) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(target, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _invoker.SendAsync(request, ct);
    }

    private sealed record Flow(BlindAccountHost Host, string A, FakeClock Clock, FakeBlindHub Hub, FakeBlindDeviceConditions Conditions) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Host.DisposeAsync();
        public IBlindAppController App => Host.For(A);
    }

    /// <summary>One account paired with a fake hub through the real pull client; the clock is the test's.</summary>
    private async Task<Flow> HubFlowAsync(TimeZoneInfo? zone = null)
    {
        var hub = new FakeBlindHub("hub-a.test");
        var clock = new FakeClock(Noon, zone);
        var conditions = new FakeBlindDeviceConditions { IsMetered = false, IsPowerSaving = false };
        var host = _rig.Host(handler: _ => () => new Forward(new Router(("hub-a.test:5610", hub))), time: clock, conditions: conditions);
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        await host.PairAsync(_rig, a, hub);
        return new Flow(host, a, clock, hub, conditions);
    }

    private static IEnumerable<FakeBlindHub.Seen> Reports(FakeBlindHub hub) => hub.Requests.Where(r => r.Url.Contains("/api/sync/report-position", StringComparison.Ordinal));

    private static IEnumerable<FakeBlindHub.Seen> PausedReports(FakeBlindHub hub) => Reports(hub).Where(r => r.Url.Contains("paused=", StringComparison.Ordinal));

    // ---- the request limit -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARequestLimit_StopsTheRound_IsNotAFailure_IsToldToTheSourceOnce_AndResumesTomorrow()
    {
        await using var flow = await HubFlowAsync();
        flow.Host.SetLimits(flow.A, new BlindAccountLimits(MaxRequestsPerDay: 10)).Should().BeNull();

        // A round is challenge, authenticate, events, report: 4 requests. Two rounds are 8; the third meets the limit in the middle of it.
        (await flow.App.RequestSyncAsync()).Should().Be("Synced.");
        (await flow.App.RequestSyncAsync()).Should().Be("Synced.");
        var lastSynced = flow.App.GetStatus().LastSyncAt;
        var third = await flow.App.RequestSyncAsync();

        third.Should().Be("Paused: daily request limit reached (10 requests). Resumes at 00:00.");
        var status = flow.App.GetStatus();
        status.LastFailure.Should().BeNull("a pause is not a failure");
        status.LastSyncAt.Should().Be(lastSynced, "a stopped round is not a sync");
        status.RecentLog.Select(e => e.Message).Should().NotContain(m => m.Contains("Sync failed"));
        flow.Host.PauseOf(flow.A)!.Reason.Should().Be(BlindPauseReasons.Requests);

        // The source was told, once, through a pipeline that counts and does not block (the limit was already reached).
        var told = PausedReports(flow.Hub).Should().ContainSingle().Subject;
        told.Url.Should().Contain("paused=requests").And.Contain("pausedUntil=2026-10-22T00%3A00%3A00Z");
        var hubCalls = flow.Hub.Requests.Count;

        // Asking again the same day sends nothing at all: not a retry storm, not another report.
        for (var i = 0; i < 4; i++) (await flow.App.RequestSyncAsync()).Should().StartWith("Paused: daily request limit");
        flow.Hub.Requests.Count.Should().Be(hubCalls);
        flow.App.GetStatus().RecentLog.Count(e => e.Kind == "paused").Should().Be(1, "the same sentence twice in a row is one log line");

        // Tomorrow it goes on, and the next report has no pause in it.
        flow.Clock.NextDay();
        (await flow.App.RequestSyncAsync()).Should().Be("Synced.");
        flow.Host.PauseOf(flow.A).Should().BeNull();
        Reports(flow.Hub).Last().Url.Should().NotContain("paused=");
    }

    [Fact]
    public async Task ZeroRequests_IsAPause_WithNoEnd_NotUnlimited()
    {
        await using var flow = await HubFlowAsync();
        flow.Host.SetLimits(flow.A, new BlindAccountLimits(MaxRequestsPerDay: 0));

        (await flow.App.RequestSyncAsync()).Should().Be("Paused: the daily request limit is set to 0.");
        flow.Clock.NextDay();
        (await flow.App.RequestSyncAsync()).Should().StartWith("Paused:", "tomorrow does not lift a limit of zero");

        flow.Host.SetLimits(flow.A, BlindAccountLimits.None);
        (await flow.App.RequestSyncAsync()).Should().Be("Synced.");
    }

    [Fact]
    public async Task ARaisedLimit_LiftsThePauseAtOnce()
    {
        await using var flow = await HubFlowAsync();
        flow.Host.SetLimits(flow.A, new BlindAccountLimits(MaxRequestsPerDay: 4));
        (await flow.App.RequestSyncAsync()).Should().Be("Synced.");
        (await flow.App.RequestSyncAsync()).Should().StartWith("Paused:");

        flow.Host.SetLimits(flow.A, new BlindAccountLimits(MaxRequestsPerDay: 5000));

        (await flow.App.RequestSyncAsync()).Should().Be("Synced.");
    }

    [Fact]
    public async Task APausedByYou_Account_StopsAtOnce_AndIsToldToTheSource()
    {
        await using var flow = await HubFlowAsync();
        flow.Host.SetPaused(flow.A, true);

        (await flow.App.RequestSyncAsync()).Should().Be("Paused: paused by you.");

        var told = PausedReports(flow.Hub).Should().ContainSingle().Subject;
        told.Url.Should().Contain("paused=paused-by-you").And.NotContain("pausedUntil", "it has no end but the person's");
        flow.Host.SetPaused(flow.A, false);
        (await flow.App.RequestSyncAsync()).Should().Be("Synced.");
    }

    // ---- what a clock can and cannot do ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AClockSetBack_DoesNotRefillTheDay()
    {
        await using var flow = await HubFlowAsync();
        flow.Host.SetLimits(flow.A, new BlindAccountLimits(MaxRequestsPerDay: 4));
        (await flow.App.RequestSyncAsync()).Should().Be("Synced.");
        (await flow.App.RequestSyncAsync()).Should().StartWith("Paused:");

        flow.Clock.Advance(TimeSpan.FromHours(-5));
        (await flow.App.RequestSyncAsync()).Should().StartWith("Paused:", "earlier the same day");
        flow.Clock.Advance(TimeSpan.FromDays(-3));
        (await flow.App.RequestSyncAsync()).Should().StartWith("Paused:", "an earlier date does not open a new day");

        flow.Clock.Set(Noon);
        flow.Clock.NextDay();
        (await flow.App.RequestSyncAsync()).Should().Be("Synced.");
    }

    [Fact]
    public async Task AGermanWinterNight_AnOverallDstDay_AndAZoneThatSkipsMidnight_AllResumeAtTheirOwnMidnight()
    {
        foreach (var (zone, now, resumes) in new[]
        {
            (TestZones.Dst, new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero)),
            (TestZones.Dst, new DateTimeOffset(2026, 3, 29, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 29, 22, 0, 0, TimeSpan.Zero)),
            // no 00:00 on 29 March: the day begins at 01:00 local, which is 00:00 UTC (the instant the clocks jump)
            (TestZones.SkipsMidnight, new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero)),
        })
        {
            using var rig = new AccountRig();
            var hub = new FakeBlindHub("hub-a.test");
            var clock = new FakeClock(now, zone);
            await using var host = rig.Host(handler: _ => () => new Forward(new Router(("hub-a.test:5610", hub))), time: clock);
            await host.StartAsync();
            var a = (await host.AddAsync("A")).AccountId!;
            await host.PairAsync(rig, a, hub);
            host.SetLimits(a, new BlindAccountLimits(MaxRequestsPerDay: 4));
            (await host.For(a).RequestSyncAsync()).Should().Be("Synced.");

            (await host.For(a).RequestSyncAsync()).Should().StartWith("Paused:");
            host.PauseOf(a)!.Until.Should().Be(resumes, $"in {zone.Id} on {now:yyyy-MM-dd}");

            clock.Set(resumes.AddMinutes(1));
            (await host.For(a).RequestSyncAsync()).Should().Be("Synced.", "the day after begins right then");
        }
    }

    // ---- a load far bigger than the day's allowance ------------------------------------------------------------------------------------

    private sealed class LoadRig(BlindAccountHost host, string id, RangeServer server, FakeClock clock, RecordingPullClient pull) : IAsyncDisposable
    {
        public BlindAccountHost Host { get; } = host;
        public string Id { get; } = id;
        public RangeServer Server { get; } = server;
        public FakeClock Clock { get; } = clock;
        public RecordingPullClient Pull { get; } = pull;
        public IBlindAppController App => Host.For(Id);
        public string Partial => Path.Combine(_rigRoot, "accounts", Id, "blind-replica", "replica.part");
        public string _rigRoot = "";
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    /// <summary>
    /// The first load of 14 MB with a daily allowance of 2 MB: the scale of "a 1.4 GB load under 200 MB a day" divided by a hundred, so that no test moves gigabytes. Everything that
    /// matters is the same: the limit is checked every 256 KB, the partial file stays, the next day resumes by byte range.
    /// </summary>
    private async Task<LoadRig> LoadFlowAsync(long fileLength, BlindAccountLimits limits, Action<BlindAccountHostOptions>? configure = null)
    {
        var server = new RangeServer(fileLength);
        var clock = new FakeClock(Noon);
        var pull = new RecordingPullClient();
        var host = _rig.Host(
            handler: _ => () => new Forward(new Router(("hub-a.test:5610", server))),
            time: clock,
            overrides: (services, entry) =>
            {
                services.AddSingleton<IBlindPhonePullClient>(pull);
                services.AddSingleton<IBlindReplicaSource>(sp => new RangeDownloadSource(sp.GetRequiredService<BlindHttpClientProvider>(), "https://hub-a.test:5610/file"));
            });
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        await host.PairAsync(_rig, a, new FakeBlindHub("hub-a.test"), loaded: false);
        host.For(a).SetSchedule(BlindBackupSchedule.Off);
        host.SetLimits(a, limits).Should().BeNull();
        return new LoadRig(host, a, server, clock, pull) { _rigRoot = _rig.Root };
    }

    [Fact]
    public async Task ALoadFarBiggerThanTheDaysAllowance_StopsAtTheLimit_KeepsItsPartialFile_AndResumesByRangeEachNextDay()
    {
        const long file = 14L << 20, daily = 2L << 20;
        await using var load = await LoadFlowAsync(file, new BlindAccountLimits(MaxTrafficBytesPerDay: daily));
        var days = 0;
        var partialSizes = new List<long>();

        while (!load.App.GetStatus().InitialLoadDone)
        {
            days++;
            days.Should().BeLessThan(12, "the load finishes in about seven days");
            var result = await load.App.RunHeavyAsync(false);
            var status = load.App.GetStatus();
            status.LastFailure.Should().BeNull("a pause is not a failure");
            if (!status.InitialLoadDone)
            {
                result.Should().StartWith("Paused: daily traffic limit reached (2 MB). Resumes at 00:00.");
                var held = new FileInfo(load.Partial).Length;
                partialSizes.Add(held);
                held.Should().BeGreaterThan(0, "the bytes that came are kept");
                // Within one check interval of the day's allowance.
                var usedToday = load.Host.ServicesFor(load.Id).GetRequiredService<BlindUsageLedger>().UsageToday().Traffic;
                usedToday.Should().BeInRange(daily, daily + BlindMeteringTap.CheckEveryBytes + 80 * 1024 + 2 * BlindMeteringTap.OverheadBytes);

                // The same day, again and again: nothing is asked of the server.
                var requests = load.Server.Requests;
                for (var i = 0; i < 3; i++) (await load.App.RunHeavyAsync(false)).Should().StartWith("Paused:");
                load.Server.Requests.Should().Be(requests);
                load.Clock.NextDay();
            }
        }

        days.Should().BeInRange(7, 9);
        partialSizes.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems("each day added to the partial file");
        new FileInfo(load.Partial).Length.Should().Be(file, "every byte is there, none twice");

        // Every request after the first asked for exactly what the partial file already held.
        load.Server.RangeHeaders[0].Should().BeNull();
        for (var i = 1; i < load.Server.RangeHeaders.Count; i++)
            load.Server.RangeHeaders[i].Should().Be($"bytes={partialSizes[i - 1]}-", "resumed where the file ended");

        // The numbers: what the ledger counted is what the server really handed out, plus 1 KB of headers per request, within 2 %.
        var ledger = load.Host.ServicesFor(load.Id).GetRequiredService<BlindUsageLedger>();
        var counted = ledger.History().Sum(d => d.BytesIn);
        var expected = load.Server.BytesServed + load.Server.Requests * (long)BlindMeteringTap.OverheadBytes;
        counted.Should().BeInRange((long)(expected * 0.98), (long)(expected * 1.02));
        ledger.History().Should().OnlyContain(d => d.Traffic <= daily + BlindMeteringTap.CheckEveryBytes + 80 * 1024 + 2 * BlindMeteringTap.OverheadBytes, "no day went over its allowance by more than a check interval");
    }

    [Fact]
    public async Task ADownloadThatCannotFitTheStorageLimit_IsRefusedBeforeItStarts_AndIsToldToTheSource()
    {
        await using var load = await LoadFlowAsync(5L << 20, new BlindAccountLimits(MaxStoredBytes: 4L << 20));

        var result = await load.App.RunHeavyAsync(false);

        result.Should().StartWith("Paused: not enough room: this copy needs about ").And.Contain(" and the limit is 4 MB.");
        result.Should().MatchRegex(@"needs about [5-6](\.\d)? MB", "the announced 5 MB plus what the account already holds");
        load.Server.BytesServed.Should().Be(0, "not one byte was pulled");
        File.Exists(load.Partial).Should().BeFalse("nothing was written");
        load.App.GetStatus().LastFailure.Should().BeNull();
        load.Pull.Reports.Should().ContainSingle().Which.Reason.Should().Be(BlindPauseReasons.Disk);

        // Raising the limit lets the load start.
        load.Host.SetLimits(load.Id, new BlindAccountLimits(MaxStoredBytes: 8L << 20));
        (await load.App.RunHeavyAsync(false)).Should().Be("Nothing due.");
        load.App.GetStatus().InitialLoadDone.Should().BeTrue();
    }

    // ---- BlindHeavyWork.Started: the moment a head may name the account in a foreground notification ------------------------------------

    [Fact]
    public async Task AJobThatRuns_RaisesStartedOnce_BeforeItsFirstProgress()
    {
        await using var load = await LoadFlowAsync(2L << 20, new BlindAccountLimits());
        var work = load.Host.ServicesFor(load.Id).GetRequiredService<BlindHeavyWork>();
        var events = new List<string>();
        work.Started += () => events.Add("started");
        work.Progress += (_, _) => events.Add("progress");

        await load.App.RunHeavyAsync(false);

        events.Count(e => e == "started").Should().Be(1);
        events[0].Should().Be("started", "the head publishes its notification before the first percentage arrives");
        load.App.GetStatus().InitialLoadDone.Should().BeTrue();
    }

    [Fact]
    public async Task APassTheGuardRefusesBeforeItStarts_RaisesNothing_ButOneThatStartsAndThenStopsDoes()
    {
        await using var load = await LoadFlowAsync(14L << 20, new BlindAccountLimits(MaxTrafficBytesPerDay: 2L << 20));
        var work = load.Host.ServicesFor(load.Id).GetRequiredService<BlindHeavyWork>();
        var started = 0;
        work.Started += () => started++;

        (await load.App.RunHeavyAsync(false)).Should().StartWith("Paused:");
        var afterFirst = started;
        (await load.App.RunHeavyAsync(false)).Should().StartWith("Paused:");

        afterFirst.Should().Be(1, "the job began (and stopped at the limit)");
        started.Should().Be(1, "the same day's second pass is refused by the guard before any work: nothing is announced");
    }

    [Fact]
    public async Task AnAccountThatIsNotPaired_RaisesNothing()
    {
        await using var host = _rig.Host();
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var work = host.ServicesFor(a).GetRequiredService<BlindHeavyWork>();
        var started = 0;
        work.Started += () => started++;

        (await host.For(a).RunHeavyAsync(false)).Should().Be("Not paired yet.");

        started.Should().Be(0, "no job, no notification");
    }

    [Fact]
    public async Task AListenerThatThrows_DoesNotStopTheJob()
    {
        await using var load = await LoadFlowAsync(2L << 20, new BlindAccountLimits());
        var called = 0;
        load.Host.ServicesFor(load.Id).GetRequiredService<BlindHeavyWork>().Started += () =>
        {
            called++;
            throw new InvalidOperationException("the notification could not be shown");
        };

        await load.App.RunHeavyAsync(false);

        called.Should().Be(1, "the listener that throws WAS called (without this the test would pass if the event were never raised)");
        load.App.GetStatus().InitialLoadDone.Should().BeTrue("a head that cannot show its notification still gets its copy");
    }

    [Fact]
    public async Task APausedLoad_DoesNotHoldTheDevicesLongJobLock()
    {
        await using var load = await LoadFlowAsync(14L << 20, new BlindAccountLimits(MaxTrafficBytesPerDay: 2L << 20));
        await load.App.RunHeavyAsync(false);

        load.Host.ServicesFor(load.Id).GetRequiredService<BlindHeavyWork>().DeviceGate.IsBusy.Should().BeFalse("the gate was released when the job paused");
    }

    // ---- the device ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADeviceLimit_IsSharedByTwoAccounts_AndPausesTheOneThatComesAfter()
    {
        var hubA = new FakeBlindHub("hub-a.test");
        var hubB = new FakeBlindHub("hub-b.test");
        var clock = new FakeClock(Noon);
        var router = new Router(("hub-a.test:5610", hubA), ("hub-b.test:5610", hubB));
        await using var host = _rig.Host(handler: _ => () => new Forward(router), time: clock);
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        var b = (await host.AddAsync("B")).AccountId!;
        await host.PairAsync(_rig, a, hubA);
        await host.PairAsync(_rig, b, hubB);
        host.SetLimits(a, new BlindAccountLimits(MaxTrafficBytesPerDay: 1L << 30));   // plenty of its own
        host.SetLimits(b, new BlindAccountLimits(MaxTrafficBytesPerDay: 1L << 30));
        host.SetDeviceLimits(new BlindDeviceLimits(MaxTrafficBytesPerDay: 10 * 1024)).Should().BeNull();

        (await host.For(a).RequestSyncAsync()).Should().Be("Synced.", "a round is about 8.6 KB: the device allowance of 10 KB is not yet used up");
        var second = await host.For(b).RequestSyncAsync();

        second.Should().StartWith("Paused: the device's daily traffic limit is reached (10 KB). Resumes at 00:00.");
        host.PauseOf(b)!.Reason.Should().Be(BlindPauseReasons.DeviceTraffic);
        host.PauseOf(a)!.Reason.Should().Be(BlindPauseReasons.DeviceTraffic, "it is the device that is out of its day, whichever account asks");
        host.GetSnapshot().DeviceUsage!.TrafficToday.Should().BeGreaterThanOrEqualTo(10 * 1024);
        PausedReports(hubB).Should().ContainSingle().Which.Url.Should().Contain("paused=device-traffic");

        // A new day, a new allowance for the whole device: B takes it first, and then it is A's turn to find the device's day used up.
        clock.NextDay();
        (await host.For(b).RequestSyncAsync()).Should().Be("Synced.");
        (await host.For(a).RequestSyncAsync()).Should().StartWith("Paused: the device's daily traffic limit is reached");
        clock.NextDay();
        (await host.For(a).RequestSyncAsync()).Should().Be("Synced.");
    }

    // ---- the switches ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnmeteredOnly_WaitsWhenTheNetworkIsMetered_OrTheDeviceCannotSay_AndGoesWhenItSaysItIsNot_AndTheSourceIsNotTold()
    {
        await using var flow = await HubFlowAsync();
        flow.Host.SetSwitches(flow.A, unmeteredOnly: true, pauseOnPowerSaving: false);

        flow.Conditions.IsMetered = null;
        var unknown = await flow.App.RequestSyncAsync();
        unknown.Should().Be("Waiting: this device does not say whether the network is metered.");

        flow.Conditions.IsMetered = true;
        (await flow.App.RequestSyncAsync()).Should().Be("Waiting for an unmetered network (Wi-Fi).");

        var calls = flow.Hub.Requests.Count;
        calls.Should().Be(0, "not one request went out on a network the person did not want used");
        flow.App.GetStatus().LastFailure.Should().BeNull();

        flow.Conditions.IsMetered = false;
        (await flow.App.RequestSyncAsync()).Should().Be("Synced.");
        PausedReports(flow.Hub).Should().BeEmpty("nothing is told about the network or the battery");
    }

    [Fact]
    public async Task PauseOnBatterySaver_WaitsWhileTheDeviceIsSavingPower()
    {
        await using var flow = await HubFlowAsync();
        flow.Host.SetSwitches(flow.A, unmeteredOnly: false, pauseOnPowerSaving: true);
        flow.Conditions.IsPowerSaving = true;

        (await flow.App.RequestSyncAsync()).Should().Be("Waiting: the battery saver is on.");
        flow.Hub.Requests.Should().BeEmpty();

        flow.Conditions.IsPowerSaving = false;
        (await flow.App.RequestSyncAsync()).Should().Be("Synced.");
        PausedReports(flow.Hub).Should().BeEmpty();
    }

    [Fact]
    public async Task AHostWithNoAdapter_CannotSay_SoAnAccountWithASwitchOnWaitsAndOneWithoutSyncs()
    {
        var hub = new FakeBlindHub("hub-a.test");
        await using var host = _rig.Host(handler: _ => () => new Forward(new Router(("hub-a.test:5610", hub))));
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        await host.PairAsync(_rig, a, hub);

        (await host.For(a).RequestSyncAsync()).Should().Be("Synced.", "the switches are off: the device is not asked");
        host.SetSwitches(a, unmeteredOnly: true, pauseOnPowerSaving: false);
        (await host.For(a).RequestSyncAsync()).Should().StartWith("Waiting: this device does not say");
    }

    // ---- telling the source -----------------------------------------------------------------------------------------------------------------

    private async Task<(BlindAccountHost Host, string A, FakeClock Clock, RecordingPullClient Pull, FakeBlindDeviceConditions Conditions)> RecordingAsync()
    {
        var clock = new FakeClock(Noon);
        var pull = new RecordingPullClient();
        var conditions = new FakeBlindDeviceConditions { IsMetered = false, IsPowerSaving = false };
        var host = _rig.Host(time: clock, conditions: conditions, overrides: (services, _) => services.AddSingleton<IBlindPhonePullClient>(pull));
        await host.StartAsync();
        var a = (await host.AddAsync("A")).AccountId!;
        await host.PairAsync(_rig, a, new FakeBlindHub("hub-a.test"));
        return (host, a, clock, pull, conditions);
    }

    [Fact]
    public async Task ALastingPause_IsToldAtOnce_ThenOnceEvery24Hours_AChangedReasonAtOnce_AndANewEpisodeAtOnce()
    {
        var (host, a, clock, pull, _) = await RecordingAsync();
        await using var _ = host;
        host.SetPaused(a, true);

        for (var i = 0; i < 4; i++) (await host.For(a).RequestSyncAsync()).Should().Be("Paused: paused by you.");
        pull.Reports.Select(r => r.Reason).Should().Equal(BlindPauseReasons.PausedByYou);
        pull.Rounds.Should().Be(0, "no round was run while paused");

        clock.Advance(TimeSpan.FromHours(23));
        await host.For(a).RequestSyncAsync();
        pull.Reports.Should().HaveCount(1, "23 hours is not 24");
        clock.Advance(TimeSpan.FromHours(1.1));
        await host.For(a).RequestSyncAsync();
        pull.Reports.Should().HaveCount(2, "a lasting pause is told once every 24 hours");

        host.SetPaused(a, false);
        host.SetLimits(a, new BlindAccountLimits(MaxTrafficBytesPerDay: 0));
        await host.For(a).RequestSyncAsync();
        pull.Reports.Select(r => r.Reason).Should().Equal(BlindPauseReasons.PausedByYou, BlindPauseReasons.PausedByYou, BlindPauseReasons.Traffic);

        host.SetLimits(a, BlindAccountLimits.None);
        (await host.For(a).RequestSyncAsync()).Should().Be("Synced.", "the round ran: the episode is over");
        host.SetPaused(a, true);
        await host.For(a).RequestSyncAsync();
        pull.Reports.Should().HaveCount(4, "a new pause is told at once, not after another 24 hours");
    }

    [Fact]
    public async Task AReportThatCannotBeDelivered_IsTriedAgainAfterAnHour_NotAtEveryPass_AndNeverBreaksTheRound()
    {
        var (host, a, clock, pull, _) = await RecordingAsync();
        await using var _ = host;
        pull.ReportFailure = new HttpRequestException("the source is down");
        host.SetPaused(a, true);

        (await host.For(a).RequestSyncAsync()).Should().Be("Paused: paused by you.");
        for (var i = 0; i < 5; i++) await host.For(a).RequestSyncAsync();
        pull.Reports.Should().HaveCount(1, "a failed delivery is not retried at every pass");
        host.For(a).GetStatus().RecentLog.Should().Contain(e => e.Kind == "paused" && e.Message.StartsWith("The source could not be told about the pause"));

        clock.Advance(TimeSpan.FromMinutes(61));
        pull.ReportFailure = null;
        await host.For(a).RequestSyncAsync();
        pull.Reports.Should().HaveCount(2);
        clock.Advance(TimeSpan.FromHours(2));
        await host.For(a).RequestSyncAsync();
        pull.Reports.Should().HaveCount(2, "delivered: the next one is 24 hours later");
    }

    [Fact]
    public async Task TheHeavyJobToo_ReportsAPause_AndNeverCountsItAsAFailure()
    {
        var (host, a, _, pull, _) = await RecordingAsync();
        await using var _ = host;
        host.For(a).SetSchedule(BlindBackupSchedule.Off);
        host.SetLimits(a, new BlindAccountLimits(MaxStoredBytes: 0));

        var result = await host.For(a).RunHeavyAsync(true);

        result.Should().Be("Paused: the storage limit is set to 0.");
        host.For(a).GetStatus().LastFailure.Should().BeNull();
        pull.Reports.Should().ContainSingle().Which.Reason.Should().Be(BlindPauseReasons.Disk);
        host.Get<BlindHeavyWork>(a).LastFailure.Should().BeNull();
    }

    // ---- counting, saving ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhatARoundCounted_IsSavedWhenItEnds_AndTheCardShowsIt()
    {
        await using var flow = await HubFlowAsync();
        await flow.App.RequestSyncAsync();

        var file = Path.Combine(_rig.Root, "accounts", flow.A, "usage.json");
        File.Exists(file).Should().BeTrue();
        File.ReadAllText(file).Should().Contain("\"requests\": 4");

        var card = flow.Host.GetSnapshot().Accounts.Single();
        card.Usage!.RequestsToday.Should().Be(4);
        card.Usage.TrafficToday.Should().BeGreaterThan(8 * 1024);
        card.Usage.StoredBytes.Should().BeGreaterThan(0, "the database is on disk");
        var view = BlindAccountCard.Build(card, flow.Clock.LocalTimeZone, flow.Clock.GetUtcNow());
        view.Today.Should().StartWith("4 requests, about ");
        view.State.Should().Be("Synced just now");
    }

    [Fact]
    public async Task UsageIsKeptByTheDeviceAcrossARestart_AndAnAccountThatWasRemovedLeavesNoUsageFile()
    {
        string a;
        var hub = new FakeBlindHub("hub-a.test");
        var clock = new FakeClock(Noon);
        await using (var host = _rig.Host(handler: _ => () => new Forward(new Router(("hub-a.test:5610", hub))), time: clock))
        {
            await host.StartAsync();
            a = (await host.AddAsync("A")).AccountId!;
            await host.PairAsync(_rig, a, hub);
            await host.For(a).RequestSyncAsync();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        await using var restarted = _rig.Host(handler: _ => () => new Forward(new Router(("hub-a.test:5610", hub))), time: clock);
        await restarted.StartAsync();
        restarted.GetSnapshot().Accounts.Single().Usage!.RequestsToday.Should().Be(4, "the day's count survived the restart");

        await restarted.RemoveAsync(a);
        Directory.Exists(Path.Combine(_rig.Root, "accounts", a)).Should().BeFalse();
        restarted.DeviceUsage.AccountCount.Should().Be(0, "a removed account no longer counts towards the device");
    }
}
