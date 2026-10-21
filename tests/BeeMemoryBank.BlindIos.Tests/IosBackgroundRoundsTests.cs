using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>
/// What a background wake does with several accounts: the order of the in-app loop (the one that synced longest ago first), one at a time, each through its own guard, a slice of the time each when
/// there are several and the whole wake for one, an account's trouble its own, and the wake ending when iOS says so without losing what was done.
/// </summary>
public class IosBackgroundRoundsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(100);

    private sealed class FakeApp(string name, List<string> log) : IBlindAppController
    {
        public bool Paired { get; set; } = true;
        public bool Loaded { get; set; } = true;
        public DateTimeOffset? LastSync { get; set; }
        public Func<CancellationToken, Task<string>>? Sync { get; set; }
        public Func<CancellationToken, Task<string>>? Heavy { get; set; }
        public CancellationToken LastSyncToken { get; private set; }

        public event Action? Changed { add { } remove { } }

        public Task InitializeAsync(CancellationToken ct = default) { log.Add("init:" + name); return Task.CompletedTask; }

        public BlindAppStatus GetStatus() =>
            new(Guid.NewGuid(), "iPhone", Paired, Loaded, LastSync, null, BlindBackupSchedule.Off, false, null, null, [], []);

        public Task<string> RequestSyncAsync(CancellationToken ct = default)
        {
            log.Add("sync:" + name);
            LastSyncToken = ct;
            return Sync is { } behave ? behave(ct) : Task.FromResult("Synced.");
        }

        public Task<string> RunHeavyAsync(bool forceBackup, CancellationToken ct = default)
        {
            log.Add("heavy:" + name);
            if (Heavy is { } behave) return behave(ct);
            Loaded = true;
            return Task.FromResult("First load finished.");
        }

        public string? PairingCode() => null;
        public string? AcceptCallCode(string text) => null;
        public void StartRePair() { }
        public void SetSchedule(BlindBackupSchedule schedule) { }
        public Task<long> ExportBackupAsync(string backupName, CancellationToken ct = default) => Task.FromResult(0L);
        public Task DisconnectAndWipeAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeAccounts : IBlindAccountsController
    {
        private readonly List<string> _log;
        public FakeAccounts(List<string> log) => _log = log;

        public Dictionary<string, FakeApp> Apps { get; } = [];
        public List<BlindAccountEntry> Entries { get; } = [];
        public Dictionary<string, BlindPause?> Pauses { get; } = [];
        /// <summary>Accounts that are in the snapshot but were removed after it was taken: asking for them throws, as the host does.</summary>
        public HashSet<string> Removed { get; } = [];
        public string? RegistryProblem { get; set; }
        public Exception? StartFailure { get; set; }
        public int Polls { get; private set; }
        public int Starts { get; private set; }

        public event Action? Changed { add { } remove { } }

        public int MaxAccounts => 8;

        public FakeApp Add(string name, DateTimeOffset? lastSync = null, string phase = BlindAccountPhases.Active)
        {
            var app = new FakeApp(name, _log) { LastSync = lastSync };
            Apps[name] = app;
            Entries.Add(new BlindAccountEntry(name.PadRight(32, '0'), BlindAccountLayouts.Dir, name, phase, Order: Entries.Count));
            return app;
        }

        private string NameOf(string id) => Entries.Single(e => e.Id == id).Name;

        public Task StartAsync(CancellationToken ct = default)
        {
            Starts++;
            _log.Add("start");
            return StartFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
        }

        public BlindAccountsSnapshot GetSnapshot() => new(
            Entries.Select(e => new BlindAccountSnapshot(e, Apps[e.Name].GetStatus(), null, null, Pauses.GetValueOrDefault(e.Name))).ToList(),
            BlindDeviceLimits.None, RegistryProblem, true, 8);

        public IBlindAppController For(string accountId) => Removed.Contains(NameOf(accountId)) ? throw new KeyNotFoundException() : Apps[NameOf(accountId)];

        public BlindPause? PauseOf(string accountId)
        {
            if (Removed.Contains(NameOf(accountId))) throw new KeyNotFoundException();
            _log.Add("pause?:" + NameOf(accountId));
            return Pauses.GetValueOrDefault(NameOf(accountId));
        }

        public Task<int> PollInvitationsAsync(bool force = false, CancellationToken ct = default) { Polls++; _log.Add("poll"); return Task.FromResult(0); }

        public IServiceProvider ServicesFor(string accountId) => throw new NotSupportedException();
        public Task<BlindAddResult> AddAsync(string? name, CancellationToken ct = default) => throw new NotSupportedException();
        public BlindInvitationPreview PreviewInvitation(string? text) => throw new NotSupportedException();
        public Task<BlindAddResult> AddFromInvitationAsync(string? text, BlindAccountSettings? settings = null, CancellationToken ct = default) => throw new NotSupportedException();
        public string? Reconnect(string accountId) => throw new NotSupportedException();
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
    }

    private static Task<IosRoundOutcome> Wake(FakeAccounts accounts, bool work = false, CancellationToken ct = default) =>
        IosBackgroundRounds.RunAsync(accounts, work, TimeProvider.System, ct, Budget, Slice);

    [Fact]
    public async Task TheAppRefreshWake_StartsTheAccounts_AsksAboutInvitations_ThenTakesTheLongestSilentFirst()
    {
        var log = new List<string>();
        var accounts = new FakeAccounts(log);
        accounts.Add("recent", Noon.AddMinutes(-5));
        accounts.Add("old", Noon.AddDays(-2));
        accounts.Add("middle", Noon.AddHours(-3));

        var outcome = await IosBackgroundRounds.RunSyncAsync(accounts, CancellationToken.None);

        log.Should().Equal("start", "poll",
            "pause?:old", "init:old", "sync:old",
            "pause?:middle", "init:middle", "sync:middle",
            "pause?:recent", "init:recent", "sync:recent");
        outcome.Accounts.Select(a => a.Name).Should().Equal("old", "middle", "recent");
        outcome.Accounts.Should().OnlyContain(a => a.Result == "Synced." && a.Ran);
        outcome.EndedByIos.Should().BeFalse();
        outcome.NotReached.Should().Be(0);
    }

    [Fact]
    public async Task APausedAccount_CostsNoRequest_AndTheOthersStillGoOn()
    {
        var log = new List<string>();
        var accounts = new FakeAccounts(log);
        accounts.Add("held", Noon.AddDays(-3));
        accounts.Add("free", Noon.AddDays(-1));
        accounts.Pauses["held"] = new BlindPause(BlindPauseReasons.Traffic, Noon.AddHours(5), Limit: 200L << 20);

        var outcome = await Wake(accounts);

        log.Should().NotContain("init:held").And.NotContain("sync:held").And.Contain("sync:free");
        var held = outcome.Accounts.Single(a => a.Name == "held");
        held.Result.Should().StartWith("Paused:").And.Contain("daily traffic limit");
        held.Ran.Should().BeFalse();
        outcome.Accounts.Single(a => a.Name == "free").Result.Should().Be("Synced.");
    }

    [Fact]
    public async Task AWaitForAnUnmeteredNetwork_IsAlsoNoRequest_AndNeverAFailure()
    {
        var log = new List<string>();
        var accounts = new FakeAccounts(log);
        accounts.Add("careful");
        accounts.Pauses["careful"] = new BlindPause(BlindPauseReasons.MeteredNetwork);

        var outcome = await Wake(accounts);

        log.Should().NotContain("sync:careful");
        outcome.Accounts.Single().Result.Should().Be("Waiting for an unmetered network (Wi-Fi).");
    }

    [Fact]
    public async Task AccountsThatAreNotPairedOrNotLoaded_AreHandledAsTheSingleAccountCopyDid()
    {
        var log = new List<string>();
        var accounts = new FakeAccounts(log);
        accounts.Add("unpaired").Paired = false;
        accounts.Add("loading").Loaded = false;
        accounts.Add("ready");

        var outcome = await Wake(accounts);

        outcome.Accounts.Single(a => a.Name == "unpaired").Result.Should().Be("Not paired yet.");
        log.Should().NotContain("sync:unpaired").And.NotContain("heavy:unpaired");
        log.Should().Contain("heavy:loading").And.NotContain("sync:loading", "a first load that is not done is a piece of the load, not a sync round");
        log.Should().Contain("sync:ready");
    }

    [Fact]
    public async Task TheProcessingWake_DoesNotSyncAnAccountWhoseLongJobJustRemovedIt()
    {
        // The long job's refusal can be the second one: the account is removed by its owner when the job returns, and the round after it would ask a source that has just said no.
        var log = new List<string>();
        var accounts = new FakeAccounts(log);
        accounts.Add("gone", Noon.AddDays(-2));
        accounts.Add("kept", Noon.AddDays(-1));
        accounts.Apps["gone"].Heavy = _ =>
        {
            accounts.Entries[0] = accounts.Entries[0] with { Phase = BlindAccountPhases.RemovedByOwner };
            return Task.FromResult(BlindSourceRemoval.RemovedSentence);
        };

        var outcome = await Wake(accounts, work: true);

        log.Where(l => l.StartsWith("heavy:", StringComparison.Ordinal) || l.StartsWith("sync:", StringComparison.Ordinal))
            .Should().Equal("heavy:gone", "heavy:kept", "sync:kept");
        outcome.Accounts.Single(a => a.Name == "gone").Result.Should().Be(BlindSourceRemoval.RemovedSentence);
    }

    [Fact]
    public async Task TheProcessingWake_RunsTheLongJobThenSyncs_ForEachAccount_AndOnlyAfterALoadThatFinished()
    {
        var log = new List<string>();
        var accounts = new FakeAccounts(log);
        accounts.Add("a", Noon.AddDays(-2));
        accounts.Add("b", Noon.AddDays(-1)).Loaded = false;
        accounts.Apps["b"].Heavy = _ => Task.FromResult("The first load is still running.");

        await Wake(accounts, work: true);

        log.Where(l => l.StartsWith("heavy:", StringComparison.Ordinal) || l.StartsWith("sync:", StringComparison.Ordinal))
            .Should().Equal("heavy:a", "sync:a", "heavy:b");
    }

    [Fact]
    public async Task OneAccount_HasTheWholeWake_AndIsNeverCutShort()
    {
        var accounts = new FakeAccounts([]);
        var app = accounts.Add("only");
        using var ios = new CancellationTokenSource();
        app.Sync = async ct => { await Task.Delay(TimeSpan.FromMilliseconds(300), ct); return "Synced."; };   // longer than a slice would be

        var outcome = await IosBackgroundRounds.RunAsync(accounts, false, TimeProvider.System, ios.Token, Budget, Slice);

        outcome.Accounts.Single().Result.Should().Be("Synced.", "a slice is for sharing; one account has no one to share with");
        app.LastSyncToken.Should().Be(ios.Token, "the account is given iOS's own token, not a slice of it");
    }

    [Fact]
    public async Task SeveralAccounts_ShareTheWake_AnAccountThatUsesItsSliceIsStopped_AndTheNextOneGetsItsTurn()
    {
        var log = new List<string>();
        var accounts = new FakeAccounts(log);
        var slow = accounts.Add("slow", Noon.AddDays(-3));
        accounts.Add("quick", Noon.AddDays(-1));
        slow.Sync = async ct => { await Task.Delay(TimeSpan.FromMinutes(5), ct); return "Synced."; };

        var outcome = await Wake(accounts);

        outcome.Accounts.Select(a => a.Name).Should().Equal("slow", "quick");
        outcome.Accounts[0].Result.Should().StartWith("Stopped to give the next account its time");
        outcome.Accounts[1].Result.Should().Be("Synced.");
        outcome.EndedByIos.Should().BeFalse("the slice ended the first, iOS did not end the wake");
    }

    [Fact]
    public async Task WhenIosEndsTheWake_WhatWasDoneIsKept_AndTheRestWaitsForTheNextWake()
    {
        var log = new List<string>();
        var accounts = new FakeAccounts(log);
        accounts.Add("done", Noon.AddDays(-4));
        var running = accounts.Add("running", Noon.AddDays(-3));
        accounts.Add("waiting-1", Noon.AddDays(-2));
        accounts.Add("waiting-2", Noon.AddDays(-1));
        using var ios = new CancellationTokenSource();
        running.Sync = async ct =>
        {
            ios.Cancel();   // the expiration handler of the background task
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
            return "Synced.";
        };

        var outcome = await Wake(accounts, ct: ios.Token);

        outcome.EndedByIos.Should().BeTrue();
        outcome.NotReached.Should().Be(2);
        outcome.Accounts.Select(a => a.Name).Should().Equal("done", "running");
        outcome.Accounts[0].Result.Should().Be("Synced.");
        outcome.Accounts[1].Result.Should().StartWith("Stopped by iOS");
        log.Should().NotContain("sync:waiting-1").And.NotContain("sync:waiting-2");
        outcome.DeviceSummary.Should().Contain("2 more wait for the next wake").And.Contain("Ended by iOS").And.NotContain("running").And.NotContain("waiting-1", "the device summary names no account");
    }

    [Fact]
    public async Task AnAccountsTrouble_IsItsOwn_TheOthersStillGetTheirTurn()
    {
        var log = new List<string>();
        var accounts = new FakeAccounts(log);
        accounts.Add("broken", Noon.AddDays(-2)).Sync = _ => throw new BlindSentenceException("the database is locked");
        accounts.Add("fine", Noon.AddDays(-1));

        var outcome = await Wake(accounts);

        outcome.Accounts.Single(a => a.Name == "broken").Result.Should().Be("Failed: the database is locked", "a sentence this code threw on purpose is said as it is");
        outcome.Accounts.Single(a => a.Name == "broken").Kind.Should().Be(IosRoundKind.Failed);
        outcome.Accounts.Single(a => a.Name == "fine").Result.Should().Be("Synced.");
    }

    [Fact]
    public async Task AnAccountsTrouble_ThatIsNotASentenceOfThisCode_IsToldByItsKind_WhateverItsTypeAndWords()
    {
        var accounts = new FakeAccounts([]);
        accounts.Add("broken", Noon.AddDays(-2)).Sync = _ => throw new InvalidOperationException("the database is locked at https://x.example/?token=canary-123");
        accounts.Add("fine", Noon.AddDays(-1));

        var outcome = await Wake(accounts);

        outcome.Accounts.Single(a => a.Name == "broken").Result.Should().Be("Failed: an unexpected problem (InvalidOperationException)");
        outcome.Accounts.Single(a => a.Name == "fine").Result.Should().Be("Synced.");
    }

    [Fact]
    public async Task AnAccountThatWasRemovedSinceTheSnapshot_IsSkipped_NotAFailure()
    {
        var log = new List<string>();
        var accounts = new FakeAccounts(log);
        accounts.Add("gone", Noon.AddDays(-2));
        accounts.Add("here", Noon.AddDays(-1));
        accounts.Removed.Add("gone");   // For() and PauseOf() now say the account is not there

        var outcome = await Wake(accounts);

        outcome.Accounts.Single(a => a.Name == "gone").Result.Should().Be("Removed.");
        outcome.Accounts.Single(a => a.Name == "here").Result.Should().Be("Synced.");
    }

    [Fact]
    public async Task ADamagedList_NoAccounts_AndAHostThatCannotStart_AreSaidInWords_AndNothingIsAsked()
    {
        var log = new List<string>();
        var damaged = new FakeAccounts(log) { RegistryProblem = "The list of accounts is damaged." };
        damaged.Add("a");
        var outcome = await Wake(damaged);
        outcome.Note.Should().Be("The list of accounts is damaged.");
        outcome.Accounts.Should().BeEmpty();
        log.Should().NotContain(l => l.StartsWith("sync:", StringComparison.Ordinal));

        (await Wake(new FakeAccounts([]))).Note.Should().Be("No account yet.");

        var broken = new FakeAccounts([]) { StartFailure = new IOException("the folder is locked by another copy") };
        (await Wake(broken)).Note.Should().Be("The accounts could not be started: a file or the storage of this device could not be read or written", "the kind of failure, never its message");
    }

    [Fact]
    public async Task AWakeThatIosEndedBeforeItBegan_StartsNothing()
    {
        var log = new List<string>();
        var accounts = new FakeAccounts(log);
        accounts.Add("a");
        using var ios = new CancellationTokenSource();
        await ios.CancelAsync();

        var outcome = await Wake(accounts, ct: ios.Token);

        outcome.EndedByIos.Should().BeTrue();
        log.Should().NotContain(l => l.StartsWith("sync:", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSlice_IsAnEvenShareOfWhatIsLeft_NeverLessThanTheMinimum()
    {
        var budget = TimeSpan.FromSeconds(25);
        var min = TimeSpan.FromSeconds(5);
        IosBackgroundRounds.SliceFor(budget, min, TimeSpan.Zero, 2).Should().Be(TimeSpan.FromSeconds(12.5));
        IosBackgroundRounds.SliceFor(budget, min, TimeSpan.FromSeconds(5), 2).Should().Be(TimeSpan.FromSeconds(10), "what the first did not use goes to the second");
        IosBackgroundRounds.SliceFor(budget, min, TimeSpan.Zero, 8).Should().Be(min, "eight accounts would get 3 s each: the minimum, so that a round can at least begin");
        IosBackgroundRounds.SliceFor(budget, min, TimeSpan.FromSeconds(40), 1).Should().Be(min, "a wake that is past its planned time still gives the last account the minimum");
    }

    [Fact]
    public void TheTimes_AreTheOtherCopiesRhythm_AndTheIdentifiersAreTheOnesInInfoPlist()
    {
        IosBackgroundRounds.SyncNotBefore.Should().Be(TimeSpan.FromMinutes(15));
        IosBackgroundRounds.WorkNotBefore.Should().Be(TimeSpan.FromHours(1));
        IosBackgroundRounds.SyncTaskId.Should().Be("com.beememorybank.blind.sync");
        IosBackgroundRounds.WorkTaskId.Should().Be("com.beememorybank.blind.work");
    }

    [Fact]
    public void ThePhoneAsksIosForBackgroundTime_OnlyWhileAnAccountNeedsIt()
    {
        BlindAccountSnapshot Card(string phase) => new(new BlindAccountEntry("a".PadRight(32, '0'), BlindAccountLayouts.Dir, "A", phase), null);
        BlindAccountsSnapshot Snapshot(string? problem, params string[] phases) => new(phases.Select(Card).ToList(), BlindDeviceLimits.None, problem, true, 8);

        IosBackgroundPlan.Wanted(Snapshot(null)).Should().BeFalse("no account, nothing to wake for");
        IosBackgroundPlan.Wanted(Snapshot(null, BlindAccountPhases.Active)).Should().BeTrue();
        IosBackgroundPlan.Wanted(Snapshot(null, BlindAccountPhases.AwaitingConfirmation)).Should().BeTrue("the owner's decision is learned at a wake");
        IosBackgroundPlan.Wanted(Snapshot(null, BlindAccountPhases.Pending)).Should().BeTrue();
        IosBackgroundPlan.Wanted(Snapshot(null, BlindAccountPhases.Refused, BlindAccountPhases.Expired, BlindAccountPhases.RemovedByOwner, BlindAccountPhases.Deleting)).Should().BeFalse();
        IosBackgroundPlan.Wanted(Snapshot("damaged", BlindAccountPhases.Active)).Should().BeFalse("a damaged list starts no account");
    }
}
