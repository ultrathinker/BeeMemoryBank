using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>The one loop of the device: accounts one at a time, the one that synced longest ago first, a pause skipped until it ends (and never a failure), the settings that end a skip.</summary>
public sealed class BlindDeviceSchedulerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    // ---- fakes ----------------------------------------------------------------------------------------------------------------------------

    private sealed class FakeApp(string id, FakeClock clock, List<string> log) : IBlindAppController
    {
        public BlindAppStatus Status { get; set; } = StatusOf(isPaired: true, loaded: true, lastSync: null);
        public Func<Task<string>>? SyncBehaviour { get; set; }
        /// <summary>A round that honours the token it is given (the cooperative kind); the plain SyncBehaviour ignores it.</summary>
        public Func<CancellationToken, Task<string>>? SyncWithToken { get; set; }
        /// <summary>A long job that takes over completely (and may ignore its token).</summary>
        public Func<CancellationToken, Task<string>>? HeavyBehaviour { get; set; }
        public Func<bool, string> HeavyResult { get; set; } = _ => "Nothing due.";
        public int Syncs;
        public int Heavies;

        public event Action? Changed { add { } remove { } }

        public async Task<string> RequestSyncAsync(CancellationToken ct = default)
        {
            log.Add("sync:" + id);
            Interlocked.Increment(ref Syncs);
            var result = SyncWithToken is { } cooperative ? await cooperative(ct) : SyncBehaviour is { } behave ? await behave() : "Synced.";
            if (result == "Synced.") Status = Status with { LastSyncAt = clock.GetUtcNow() };
            return result;
        }

        public Task<string> RunHeavyAsync(bool forceBackup, CancellationToken ct = default)
        {
            log.Add($"heavy:{id}:{forceBackup}");
            Interlocked.Increment(ref Heavies);
            if (HeavyBehaviour is { } takeOver) return takeOver(ct);
            var result = HeavyResult(forceBackup);
            if (result == "First load finished.") Status = Status with { InitialLoadDone = true };
            return Task.FromResult(result);
        }

        public BlindAppStatus GetStatus() => Status;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public string? PairingCode() => null;
        public string? AcceptCallCode(string text) => null;
        public void StartRePair() { }
        public void SetSchedule(BlindBackupSchedule schedule) { }
        public Task<long> ExportBackupAsync(string backupName, CancellationToken ct = default) => Task.FromResult(0L);
        public Task DisconnectAndWipeAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static BlindAppStatus StatusOf(bool isPaired, bool loaded, DateTimeOffset? lastSync) =>
        new(Guid.NewGuid(), "device", isPaired, loaded, lastSync, null, BlindBackupSchedule.Off, BackupKeyLost: false, ActiveJob: null, JobProgress: null,
            Backups: [], RecentLog: []);

    private sealed class FakeAccounts(FakeClock clock) : IBlindAccountsController
    {
        public List<string> Log { get; } = [];
        public Dictionary<string, FakeApp> Apps { get; } = [];
        public List<BlindAccountEntry> Entries { get; } = [];
        public Dictionary<string, BlindPause?> Pauses { get; } = [];
        public BlindDeviceLimits Device { get; set; } = BlindDeviceLimits.None;
        public string? RegistryProblem { get; set; }

        public event Action? Changed;

        public int MaxAccounts => 8;

        public FakeApp Add(string id, DateTimeOffset? lastSync = null, bool paired = true, bool loaded = true, string phase = BlindAccountPhases.Active, int order = 0)
        {
            var app = new FakeApp(id, clock, Log) { Status = StatusOf(paired, loaded, lastSync) };
            Apps[id] = app;
            Entries.Add(new BlindAccountEntry(id == "legacy" ? "legacy" : id.PadRight(32, '0'), id == "legacy" ? BlindAccountLayouts.Root : BlindAccountLayouts.Dir, id, phase, Order: order));
            return app;
        }

        private string IdOf(string name) => Entries.Single(e => e.Name == name).Id;

        public BlindAccountsSnapshot GetSnapshot() => new(
            Entries.Select(e => new BlindAccountSnapshot(e, Apps[e.Name].Status, null, null, Pauses.GetValueOrDefault(e.Name))).ToList(),
            Device, RegistryProblem, true, 8);

        public IBlindAppController For(string accountId) =>
            Apps.TryGetValue(NameOf(accountId), out var app) ? app : throw new KeyNotFoundException();

        public BlindPause? PauseOf(string accountId) => Pauses.GetValueOrDefault(NameOf(accountId));

        private string NameOf(string accountId) => Entries.FirstOrDefault(e => e.Id == accountId)?.Name ?? throw new KeyNotFoundException();

        public void ChangeLimits(string name, BlindAccountLimits limits)
        {
            var i = Entries.FindIndex(e => e.Name == name);
            Entries[i] = Entries[i] with { Limits = limits };
            Changed?.Invoke();
        }

        public void Remove(string name) => Entries.RemoveAll(e => e.Name == name);

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        /// <summary>How many times the scheduler asked about invitations that wait, and whether it forced the ask.</summary>
        public List<bool> InvitationPolls { get; } = [];
        public Exception? InvitationPollFails { get; set; }

        public Task<int> PollInvitationsAsync(bool force = false, CancellationToken ct = default)
        {
            InvitationPolls.Add(force);
            return InvitationPollFails is { } failure ? Task.FromException<int>(failure) : Task.FromResult(0);
        }

        public BlindInvitationPreview PreviewInvitation(string? text) => throw new NotSupportedException();
        public Task<BlindAddResult> AddFromInvitationAsync(string? text, BlindAccountSettings? settings = null, CancellationToken ct = default) => throw new NotSupportedException();
        public string? Reconnect(string accountId) => throw new NotSupportedException();
        public IServiceProvider ServicesFor(string accountId) => throw new NotSupportedException();
        public Task<BlindAddResult> AddAsync(string? name, CancellationToken ct = default) => throw new NotSupportedException();
        public string? AcceptCallCode(string text) => throw new NotSupportedException();
        public string? Rename(string accountId, string name) => throw new NotSupportedException();
        public string? SetLimits(string accountId, BlindAccountLimits limits) => throw new NotSupportedException();
        public string? SetSwitches(string accountId, bool unmeteredOnly, bool pauseOnPowerSaving) => throw new NotSupportedException();
        public string? SetPaused(string accountId, bool paused) => throw new NotSupportedException();
        public string? SetDeviceLimits(BlindDeviceLimits limits) => throw new NotSupportedException();
        public Task RemoveAsync(string accountId, CancellationToken ct = default) => throw new NotSupportedException();
        public string? StartNewUsageLedger(string accountId) => throw new NotSupportedException();
        public string? StartNewDeviceUsageLedger() => throw new NotSupportedException();
        public Task<string?> AdoptLegacyRootAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> AdoptFolderAsync(string accountId, string? name = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> ForgetMissingAccountAsync(string accountId, CancellationToken ct = default) => throw new NotSupportedException();
        public string AccountId(string name) => IdOf(name);
    }

    private static BlindPause Daily(DateTimeOffset until) => new(BlindPauseReasons.Traffic, until, 1000);

    private static (FakeAccounts Accounts, BlindDeviceScheduler Scheduler, FakeClock Clock) Rig(BlindDeviceSchedulerOptions? options = null)
    {
        var clock = new FakeClock(Noon);
        var accounts = new FakeAccounts(clock);
        return (accounts, new BlindDeviceScheduler(accounts, clock, options), clock);
    }

    private static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        condition().Should().BeTrue(what);
    }

    // ---- invitations that wait ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryPass_AsksAboutInvitationsThatWait_WithoutForcingAnAnswer()
    {
        var (accounts, scheduler, _) = Rig();
        accounts.Add("a", Noon.AddMinutes(-20));

        await scheduler.TickAsync();
        await scheduler.TickAsync();

        accounts.InvitationPolls.Should().Equal(false, false);
    }

    [Fact]
    public async Task AFailedAskAboutAnInvitation_DoesNotStopTheRoundsOfTheAccounts()
    {
        var (accounts, scheduler, _) = Rig();
        accounts.Add("a", Noon.AddMinutes(-20));
        accounts.InvitationPollFails = new IOException("the hub could not be reached");

        await scheduler.TickAsync();

        accounts.Log.Should().Contain("sync:a");
        scheduler.LastError.Should().Contain("invitation");
    }

    [Theory]
    [MemberData(nameof(Adversarial))]
    public async Task AProblemOfTheScheduler_IsToldByTheKindOfFailure_AndNoWordOfTheExceptionReachesLastErrorOrTheDeviceLog(string name)
    {
        var told = new List<string>();
        var clock = new FakeClock(Noon);
        var accounts = new FakeAccounts(clock);
        var scheduler = new BlindDeviceScheduler(accounts, clock, null, told.Add);
        accounts.Add("a", Noon.AddMinutes(-20));
        accounts.InvitationPollFails = FailureCanaries.Make(name);

        await scheduler.TickAsync();

        scheduler.LastError.Should().Contain("invitation");
        FailureCanaries.AssertClean(scheduler.LastError, "LastError (a head shows it, and writes it to the device's log)");
        FailureCanaries.AssertClean(told, "the sentence handed to the device's log");
    }

    public static TheoryData<string> Adversarial => FailureCanaries.Names;

    // ---- order -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAccountThatSyncedLongestAgoGoesFirst_ANeverSyncedOneBeforeAll()
    {
        var (accounts, scheduler, clock) = Rig();
        accounts.Add("newest", Noon.AddMinutes(-5));
        accounts.Add("oldest", Noon.AddDays(-2));
        accounts.Add("never", lastSync: null);
        accounts.Add("middle", Noon.AddHours(-3));

        await scheduler.TickAsync();

        accounts.Log.Where(l => l.StartsWith("sync:")).Should().Equal("sync:never", "sync:oldest", "sync:middle", "sync:newest");
        _ = clock;
    }

    [Fact]
    public async Task OneAccountAtATime_TheNextWaitsUntilTheFirstRoundHasEnded()
    {
        var (accounts, scheduler, _) = Rig();
        var first = accounts.Add("first", Noon.AddDays(-1));
        accounts.Add("second", Noon.AddHours(-1));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.SyncBehaviour = async () =>
        {
            entered.SetResult();
            await gate.Task;
            return "Synced.";
        };

        var tick = scheduler.TickAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(150);

        accounts.Log.Should().Equal("sync:first");   // the second account has not begun
        gate.SetResult();
        await tick.WaitAsync(TimeSpan.FromSeconds(10));
        accounts.Log.Where(l => l.StartsWith("sync:")).Should().Equal("sync:first", "sync:second");
    }

    [Fact]
    public async Task APassAlreadyRunning_IsNotStartedTwice()
    {
        var (accounts, scheduler, _) = Rig();
        var only = accounts.Add("only", Noon.AddDays(-1));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        only.SyncBehaviour = async () =>
        {
            await gate.Task;
            return "Synced.";
        };

        var first = scheduler.TickAsync();
        await scheduler.TickAsync();   // returns at once
        gate.SetResult();
        await first;

        only.Syncs.Should().Be(1);
    }

    // ---- timing ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARoundEvery15Minutes_TheLongJobEveryHour_PerAccount()
    {
        var (accounts, scheduler, clock) = Rig();
        var app = accounts.Add("a");

        await scheduler.TickAsync();
        app.Syncs.Should().Be(1);
        app.Heavies.Should().Be(1, "the long job is also due at the first pass");

        clock.Advance(TimeSpan.FromMinutes(14));
        await scheduler.TickAsync();
        app.Syncs.Should().Be(1, "not yet");
        clock.Advance(TimeSpan.FromMinutes(1.1));
        await scheduler.TickAsync();
        app.Syncs.Should().Be(2);
        app.Heavies.Should().Be(1);

        clock.Advance(TimeSpan.FromMinutes(46));
        await scheduler.TickAsync();
        app.Heavies.Should().Be(2, "an hour after the first");
    }

    // ---- two schedulers over one host (Android: a sync worker and a foreground heavy worker) ----------------------------------------------

    [Fact]
    public async Task ASchedulerThatRunsNoLongJobs_SyncsEveryAccountAndStartsNoLongJob_AtAnyPass()
    {
        var (accounts, scheduler, clock) = Rig(new BlindDeviceSchedulerOptions { RunHeavy = false, SyncEvery = TimeSpan.Zero });
        var a = accounts.Add("a");
        var b = accounts.Add("b", paired: false, loaded: false);

        await scheduler.TickAsync();
        clock.Advance(TimeSpan.FromHours(2));
        await scheduler.TickAsync();

        a.Syncs.Should().Be(2, "with a zero rhythm every pass is a round: the platform job's period is the rhythm");
        a.Heavies.Should().Be(0);
        b.Heavies.Should().Be(0, "the first load is the other scheduler's job");
        scheduler.ScheduleOf(accounts.AccountId("a"))!.NextHeavyAt.Should().NotBeNull("the plan exists");
    }

    [Fact]
    public async Task ASchedulerThatRunsNoSyncRounds_RunsTheLongJobsAndNothingElse()
    {
        var (accounts, scheduler, clock) = Rig(new BlindDeviceSchedulerOptions { RunSync = false, HeavyEvery = TimeSpan.Zero });
        var a = accounts.Add("a");
        var b = accounts.Add("b", paired: true, loaded: false);
        b.HeavyResult = _ => "First load finished.";

        scheduler.RequestSync();
        await scheduler.TickAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        await scheduler.TickAsync();

        a.Syncs.Should().Be(0);
        b.Syncs.Should().Be(0);
        a.Heavies.Should().Be(2, "every pass is a long-job look when the rhythm is zero");
        b.Heavies.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TheLongJobWaitsForPairing_AndASyncRequestByThePersonIsReported()
    {
        var (accounts, scheduler, _) = Rig();
        var app = accounts.Add("a", paired: false, loaded: false);
        var told = new List<(string, string)>();
        scheduler.UserJobReported += (id, sentence) => told.Add((id, sentence));

        await scheduler.TickAsync();
        app.Heavies.Should().Be(0, "nothing to load before pairing");

        scheduler.RequestSync(accounts.AccountId("a"));
        await scheduler.TickAsync();
        told.Should().ContainSingle().Which.Item2.Should().Be("Synced.");
    }

    // ---- pauses ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APausedAccount_IsLeftAloneUntilItsPauseEnds_WhileTheOthersGoOn()
    {
        var (accounts, scheduler, clock) = Rig();
        var paused = accounts.Add("paused", Noon.AddDays(-1));
        var other = accounts.Add("other", Noon.AddDays(-2));
        var midnight = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        paused.SyncBehaviour = () => Task.FromResult("Paused: daily traffic limit reached (1000 B). Resumes at 00:00.");
        accounts.Pauses["paused"] = Daily(midnight);

        await scheduler.TickAsync();
        paused.Syncs.Should().Be(1, "the first round is what finds the pause (and tells the source)");
        scheduler.ScheduleOf(accounts.AccountId("paused"))!.SkipUntil.Should().Be(midnight);

        for (var i = 0; i < 6; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(16));
            await scheduler.TickAsync();
        }
        paused.Syncs.Should().Be(1, "left alone until the pause ends: no round, no request, no retry");
        paused.Heavies.Should().Be(0);
        other.Syncs.Should().BeGreaterThan(5, "the other account is not held by it");

        clock.Set(midnight.AddMinutes(1));
        accounts.Pauses["paused"] = null;
        paused.SyncBehaviour = null;
        await scheduler.TickAsync();
        paused.Syncs.Should().Be(2, "the pause ended");
        scheduler.ScheduleOf(accounts.AccountId("paused"))!.SkipUntil.Should().BeNull();
    }

    [Fact]
    public async Task APauseWithNoEnd_IsLookedAtOnceADay_AndASettingChangeLooksSooner()
    {
        var (accounts, scheduler, clock) = Rig();
        var app = accounts.Add("a");
        accounts.Pauses["a"] = new BlindPause(BlindPauseReasons.PausedByYou);
        app.SyncBehaviour = () => Task.FromResult("Paused: paused by you.");

        await scheduler.TickAsync();
        app.Syncs.Should().Be(1);
        clock.Advance(TimeSpan.FromHours(23));
        await scheduler.TickAsync();
        app.Syncs.Should().Be(1, "23 hours: still left alone");

        // The person lifts it: the entry's switches change, which ends the skip at the next pass.
        accounts.ChangeLimits("a", new BlindAccountLimits(MaxTrafficBytesPerDay: 500L << 20));
        accounts.Pauses["a"] = null;
        app.SyncBehaviour = null;
        await scheduler.TickAsync();
        app.Syncs.Should().Be(2, "a changed setting ends the wait");
    }

    [Fact]
    public async Task APauseForTheNetworkOrTheBattery_IsNotSkipped_TheNextRoundIsFifteenMinutesAway_AnyWay()
    {
        var (accounts, scheduler, clock) = Rig();
        var app = accounts.Add("a");
        accounts.Pauses["a"] = new BlindPause(BlindPauseReasons.MeteredNetwork, ConditionUnknown: false);
        app.SyncBehaviour = () => Task.FromResult("Waiting for an unmetered network (Wi-Fi).");

        await scheduler.TickAsync();
        scheduler.ScheduleOf(accounts.AccountId("a"))!.SkipUntil.Should().BeNull();

        accounts.Pauses["a"] = null;   // the person walked into Wi-Fi
        app.SyncBehaviour = null;
        clock.Advance(TimeSpan.FromMinutes(16));
        await scheduler.TickAsync();

        app.Syncs.Should().Be(2);
        app.Status.LastSyncAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ASettingChangeOnAnotherAccount_DoesNotEndThisOnesSkip_ButARequestFromThePersonDoes()
    {
        var (accounts, scheduler, clock) = Rig();
        var paused = accounts.Add("paused", Noon.AddDays(-1));
        accounts.Add("other", Noon.AddDays(-2));
        accounts.Pauses["paused"] = Daily(new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero));
        paused.SyncBehaviour = () => Task.FromResult("Paused: daily traffic limit reached (1000 B). Resumes at 00:00.");
        await scheduler.TickAsync();

        accounts.ChangeLimits("other", new BlindAccountLimits(MaxRequestsPerDay: 1000));
        clock.Advance(TimeSpan.FromMinutes(16));
        await scheduler.TickAsync();
        paused.Syncs.Should().Be(1);

        scheduler.RequestSync(accounts.AccountId("paused"));
        await scheduler.TickAsync();
        paused.Syncs.Should().Be(2, "\"Sync now\" is the person asking: the skip does not stand in its way");
    }

    [Fact]
    public async Task APauseIsNotAFailedFirstLoad_NoRetryIsPlanned_AndTheBackOffDoesNotGrow()
    {
        var (accounts, scheduler, clock) = Rig();
        var app = accounts.Add("a", loaded: false);
        var id = accounts.AccountId("a");

        // A real failure: the next try comes soon and the pause between tries doubles.
        app.HeavyResult = _ => "First load failed: no answer: connection refused (nothing listens at that address)";
        await scheduler.TickAsync();
        var first = scheduler.ScheduleOf(id)!;
        first.RetryAttempts.Should().Be(1);
        first.HeavyRetryAt.Should().Be(clock.GetUtcNow() + TimeSpan.FromSeconds(30));
        first.RetryDelay.Should().Be(TimeSpan.FromSeconds(30), "a screen says 'it tries every N minutes' from the wait that led to the next try");
        clock.Advance(TimeSpan.FromSeconds(31));
        await scheduler.TickAsync();
        scheduler.ScheduleOf(id)!.RetryAttempts.Should().Be(2);
        scheduler.ScheduleOf(id)!.RetryDelay.Should().Be(TimeSpan.FromSeconds(60), "the wait doubles");

        // A pause: nothing is planned, nothing is growing.
        accounts.Pauses["a"] = Daily(new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero));
        app.HeavyResult = _ => "Paused: daily traffic limit reached (1000 B). Resumes at 00:00.";
        clock.Advance(TimeSpan.FromMinutes(2));
        await scheduler.TickAsync();

        var paused = scheduler.ScheduleOf(id)!;
        paused.HeavyRetryAt.Should().BeNull();
        paused.RetryDelay.Should().BeNull("no retry is planned during a pause");
        paused.RetryAttempts.Should().Be(0, "a pause is not a failure");
        paused.SkipUntil.Should().NotBeNull();
    }

    // ---- who is looked at --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyActiveAccountsThatStartedAreLookedAt_AndAGoneAccountsPlanIsForgotten()
    {
        var (accounts, scheduler, _) = Rig();
        var good = accounts.Add("good");
        var awaiting = accounts.Add("awaiting", phase: BlindAccountPhases.AwaitingConfirmation);
        var deleting = accounts.Add("deleting", phase: BlindAccountPhases.Deleting);
        var broken = accounts.Add("broken");
        broken.Status = broken.Status with { StartError = "database is locked" };
        var keysLost = accounts.Add("keys-lost");
        keysLost.Status = keysLost.Status with { BackupKeyLost = true };

        await scheduler.TickAsync();

        good.Syncs.Should().Be(1);
        new[] { awaiting.Syncs, deleting.Syncs, broken.Syncs, keysLost.Syncs }.Should().OnlyContain(n => n == 0);
        var goodId = accounts.AccountId("good");
        scheduler.ScheduleOf(goodId).Should().NotBeNull();

        accounts.Remove("good");
        await scheduler.TickAsync();
        scheduler.ScheduleOf(goodId).Should().BeNull("the plan of an account that is gone is forgotten");
    }

    [Fact]
    public async Task ADamagedRegistry_MeansNoAccountIsTouched()
    {
        var (accounts, scheduler, _) = Rig();
        var app = accounts.Add("a");
        accounts.RegistryProblem = "accounts.json is damaged.";

        await scheduler.TickAsync();

        app.Syncs.Should().Be(0);
    }

    [Fact]
    public async Task AnAccountRemovedBetweenTheSnapshotAndTheRound_IsSkippedQuietly()
    {
        var (accounts, scheduler, _) = Rig();
        accounts.Add("a", Noon.AddMinutes(-1));
        accounts.Add("b", Noon.AddDays(-1));   // b is older, so b goes first
        var first = accounts.Apps["b"];
        first.SyncBehaviour = () =>
        {
            accounts.Remove("a");   // gone while b is syncing
            return Task.FromResult("Synced.");
        };

        var act = () => scheduler.TickAsync();

        await act.Should().NotThrowAsync();
        accounts.Apps["a"].Syncs.Should().Be(0);
    }

    // ---- the loop -----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheLoop_RunsAPassAtStart_AndStops()
    {
        var accounts = new FakeAccounts(new FakeClock(Noon));
        var app = accounts.Add("a");
        await using var scheduler = new BlindDeviceScheduler(accounts, TimeProvider.System, new BlindDeviceSchedulerOptions { Tick = TimeSpan.FromMilliseconds(20) });

        scheduler.EnsureScheduled();
        scheduler.EnsureScheduled();   // a second start is a no-op
        scheduler.IsRunning.Should().BeTrue();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (app.Syncs == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);

        app.Syncs.Should().Be(1);
        await scheduler.StopAsync();
        scheduler.IsRunning.Should().BeFalse();
    }

    // ---- one account that hangs, and a stop that takes long (fix round 2) -----------------------------------------------------------------

    private static readonly BlindDeviceSchedulerOptions Quick = new()
    {
        SyncTimeout = TimeSpan.FromMilliseconds(150),
        HeavyTimeout = TimeSpan.FromMilliseconds(150),
        StopGrace = TimeSpan.FromMilliseconds(100),
    };

    [Fact]
    public async Task AnAccountThatHangsAndIgnoresItsToken_DoesNotStarveTheOthers_AndIsNotStartedAgainWhileItHangs()
    {
        var (accounts, scheduler, clock) = Rig(Quick);
        var hung = accounts.Add("hung", Noon.AddDays(-2));   // the oldest: it goes first
        var other = accounts.Add("other", Noon.AddHours(-1));
        var hang = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        hung.SyncBehaviour = () => hang.Task;   // does not look at any token

        await scheduler.TickAsync().WaitAsync(TimeSpan.FromSeconds(10));

        other.Syncs.Should().Be(1, "the account behind the hung one still gets its round in the same pass");
        hung.Syncs.Should().Be(1);

        clock.Advance(TimeSpan.FromMinutes(16));
        await scheduler.TickAsync().WaitAsync(TimeSpan.FromSeconds(10));

        other.Syncs.Should().Be(2, "its next round is not held up either");
        hung.Syncs.Should().Be(1, "nothing new is started for an account whose old round is still running");

        hang.SetResult("Synced.");
        for (var i = 0; i < 200 && hung.Syncs < 2; i++)
        {
            await scheduler.TickAsync();
            await Task.Delay(10);
        }
        hung.Syncs.Should().Be(2, "once the old round has really ended, the account is served again");
    }

    [Fact]
    public async Task ARoundThatHonoursItsToken_EndsAtItsTimeout_AsAPauseAndNotAFailure_AndTheOthersAreServed()
    {
        var (accounts, scheduler, clock) = Rig(new BlindDeviceSchedulerOptions { SyncTimeout = TimeSpan.FromMilliseconds(150), StopGrace = TimeSpan.FromSeconds(30) });
        var slow = accounts.Add("slow", Noon.AddDays(-2));
        var other = accounts.Add("other", Noon.AddHours(-1));
        slow.SyncWithToken = async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return "Synced.";
        };
        var told = new List<string>();
        scheduler.UserJobReported += (_, sentence) => told.Add(sentence);
        scheduler.RequestSync(accounts.AccountId("slow"));

        await scheduler.TickAsync().WaitAsync(TimeSpan.FromSeconds(10));

        other.Syncs.Should().Be(1);
        scheduler.LastError.Should().BeNull("a round that stopped when it was asked is not a problem of the scheduler");
        told.Should().Equal("Paused; it continues where it stopped.");

        clock.Advance(TimeSpan.FromMinutes(16));
        await scheduler.TickAsync().WaitAsync(TimeSpan.FromSeconds(10));
        slow.Syncs.Should().Be(2, "it was not abandoned: it stopped, so it is started again when it is due");
    }

    [Fact]
    public async Task ALongJobThatHangs_IsBoundedToo_AndTheLoopGoesOn()
    {
        var (accounts, scheduler, _) = Rig(Quick);
        var loading = accounts.Add("loading", Noon.AddDays(-2), loaded: false);
        var other = accounts.Add("other", Noon.AddHours(-1));
        var hang = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        loading.HeavyBehaviour = _ => hang.Task;   // a first load that never answers and never looks at its token

        await scheduler.TickAsync().WaitAsync(TimeSpan.FromSeconds(10));

        loading.Heavies.Should().Be(1);
        other.Syncs.Should().Be(1);
        other.Heavies.Should().Be(1, "the other account's own long job ran in the same pass");
        scheduler.ScheduleOf(accounts.AccountId("loading"))!.RetryAttempts.Should().Be(0, "an abandoned job is not a failed load: no back-off grows");
        hang.SetResult("First load finished.");
    }

    [Fact]
    public async Task AStopThatTimesOut_LeavesTheLoopWhereItIs_AndNoSecondLoopStartsBesideIt()
    {
        var options = new BlindDeviceSchedulerOptions { Tick = TimeSpan.FromMilliseconds(30), StopTimeout = TimeSpan.FromMilliseconds(100), StopGrace = TimeSpan.FromSeconds(30) };
        var accounts = new FakeAccounts(new FakeClock(Noon));
        var hung = accounts.Add("hung", Noon.AddDays(-2));
        var other = accounts.Add("other", Noon.AddHours(-1));
        var hang = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        hung.SyncBehaviour = () => hang.Task;
        await using var scheduler = new BlindDeviceScheduler(accounts, TimeProvider.System, options);
        scheduler.EnsureScheduled();
        await EventuallyAsync(() => hung.Syncs == 1, "the first round started");

        await scheduler.StopAsync();   // the round ignores the stop: this gives up waiting
        scheduler.IsRunning.Should().BeTrue("the loop has not ended: it is still the scheduler's loop, and it is draining");
        scheduler.EnsureScheduled();   // must not put a second loop beside it
        hang.SetResult("Synced.");
        await EventuallyAsync(() => !scheduler.IsRunning, "the loop that was stopped ends once its round ends");
        await Task.Delay(300);

        other.Syncs.Should().Be(0, "the stopped loop did not go on to the next account, and no other loop was there to do it");
    }

    [Fact]
    public async Task AfterATimedOutStop_TheDrainedLoopCanBeReplaced_AndALaterStopOwnsTheLiveLoop()
    {
        var options = new BlindDeviceSchedulerOptions { Tick = TimeSpan.FromMilliseconds(30), StopTimeout = TimeSpan.FromMilliseconds(100), StopGrace = TimeSpan.FromSeconds(30) };
        var accounts = new FakeAccounts(new FakeClock(Noon));
        var hung = accounts.Add("hung", Noon.AddDays(-2));
        var other = accounts.Add("other", Noon.AddHours(-1));
        var hang = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        hung.SyncBehaviour = () => hang.Task;
        await using var scheduler = new BlindDeviceScheduler(accounts, TimeProvider.System, options);
        scheduler.EnsureScheduled();
        await EventuallyAsync(() => hung.Syncs == 1, "the first round started");
        await scheduler.StopAsync();
        hang.SetResult("Synced.");
        await EventuallyAsync(() => !scheduler.IsRunning, "the old loop ended");

        scheduler.EnsureScheduled();
        await EventuallyAsync(() => other.Syncs == 1, "the new loop serves the next account");
        await scheduler.StopAsync();

        scheduler.IsRunning.Should().BeFalse("the stop owned the live loop and waited for it");
        var synced = other.Syncs;
        await Task.Delay(300);
        other.Syncs.Should().Be(synced);
        hung.Syncs.Should().Be(1);
    }

    [Fact]
    public async Task AStopThatIsGivenTime_EndsTheLoop_EvenWhenARoundThatWillNotStopIsInTheWay()
    {
        var options = new BlindDeviceSchedulerOptions { Tick = TimeSpan.FromMilliseconds(30), StopTimeout = TimeSpan.FromSeconds(10), StopGrace = TimeSpan.FromMilliseconds(100) };
        var accounts = new FakeAccounts(new FakeClock(Noon));
        var hung = accounts.Add("hung", Noon.AddDays(-2));
        var hang = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        hung.SyncBehaviour = () => hang.Task;   // never answers
        await using var scheduler = new BlindDeviceScheduler(accounts, TimeProvider.System, options);
        scheduler.EnsureScheduled();
        await EventuallyAsync(() => hung.Syncs == 1, "the round started");

        await scheduler.StopAsync().WaitAsync(TimeSpan.FromSeconds(8));

        scheduler.IsRunning.Should().BeFalse("a round that will not stop is let go of after the grace, and the loop ends");
        hang.SetResult("Synced.");
    }

    [Fact]
    public void ThePassOrder_IsTheOrderOfTheLoop_ForAHostThatTakesOneAccountPerWake()
    {
        // The iPhone's background tasks take the accounts in this order and stop where the time ends: the one that synced longest ago first, an account that never synced as the oldest,
        // ties by the order of the list; only active accounts that have started.
        BlindAccountSnapshot Card(string name, int order, BlindAppStatus? status, string phase = BlindAccountPhases.Active) =>
            new(new BlindAccountEntry(name.PadRight(32, '0'), BlindAccountLayouts.Dir, name, phase, Order: order), status);
        var snapshot = new BlindAccountsSnapshot(
        [
            Card("recent", 0, StatusOf(true, true, Noon.AddMinutes(-5))),
            Card("old", 1, StatusOf(true, true, Noon.AddDays(-2))),
            Card("never", 2, StatusOf(true, false, null)),
            Card("tie-b", 4, StatusOf(true, true, Noon.AddHours(-3))),
            Card("tie-a", 3, StatusOf(true, true, Noon.AddHours(-3))),
            Card("awaiting", 5, StatusOf(false, false, null), BlindAccountPhases.AwaitingConfirmation),
            Card("no-runtime", 6, null),
            Card("broken", 7, StatusOf(true, true, Noon.AddDays(-9)) with { StartError = "could not start" }),
        ], BlindDeviceLimits.None, null, true, 8);

        BlindDeviceScheduler.PassOrder(snapshot).Select(c => c.Entry.Name).Should().Equal("never", "old", "tie-a", "tie-b", "recent");
    }
}
