using System.Buffers.Text;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// The controller with the real AppCore composition (real SQLite in a temp folder) and fake host seams: single flight,
/// the failure log, the status contents and the progress that reaches the status (review notes of stage 1).
/// The heavy job's one-at-a-time lock is static, so every test that runs it is in this one class (xunit runs a class serially).
/// </summary>
public sealed class BlindAppControllerBehaviorTests
{
    [Fact]
    public async Task Initialize_MakesTheIdentityWithTheHostsName_AndStatusShowsItAwaitingAnAnswer()
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();

        var status = rig.Controller.GetStatus();
        status.StartError.Should().BeNull();
        status.NodeId.Should().NotBeNull();
        status.DisplayName.Should().Be("Test desktop");
        status.IsPaired.Should().BeFalse();
        status.AwaitingAnswer.Should().BeTrue("a pairing code is shown until the computer answers");
        status.Endpoint.Should().BeNull();
        status.InitialLoadDone.Should().BeFalse();
        status.Backups.Should().BeEmpty();
        status.ActiveJob.Should().BeNull();
        status.JobProgress.Should().BeNull();
        status.RecentLog.Should().Contain(e => e.Kind == "pairing", "making the identity is logged");
        rig.Controller.PairingCode().Should().StartWith("bmb-blind-phone:");
        rig.Secrets.IdentitySeed.Should().NotBeNull("the keys went to the host's secret store, not to the database");
    }

    [Fact]
    public async Task Initialize_WhenTheKeysAreGone_ReportsTheStartError_InsteadOfThrowing_AndNeverMakesNewKeys()
    {
        var first = new Rig();
        await first.Controller.InitializeAsync();
        first.Secrets.IdentitySeed.Should().NotBeNull();

        // The same database, but the host's secret store and state are empty (a lost DPAPI profile).
        var second = new Rig(first.Dir);
        await second.Controller.InitializeAsync();

        var status = second.Controller.GetStatus();
        status.StartError.Should().Contain("Disconnect and wipe required");
        second.Secrets.IdentitySeed.Should().BeNull("a missing key never mints a new one over existing data");
        status.RecentLog.Should().Contain(e => e.Kind == "start" && e.Message.Contains("Could not set the app up"));
    }

    [Fact]
    public async Task JobProgress_ReachesTheStatus_WhileTheFirstLoadRuns()
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Pair();
        rig.Controller.SetSchedule(BlindBackupSchedule.Off);

        var run = rig.Controller.RunHeavyAsync(false);
        try
        {
            await rig.Replica.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            rig.Controller.GetStatus().ActiveJob.Should().Be("First load", "it is named before the first progress report");
            rig.Replica.ReportProgress(0.4);

            var running = rig.Controller.GetStatus();
            running.ActiveJob.Should().Be("First load");
            running.JobProgress.Should().Be(0.4);
        }
        finally
        {
            rig.Replica.Finish.TrySetResult();
        }
        (await run).Should().Be("Nothing due.");
        var done = rig.Controller.GetStatus();
        done.ActiveJob.Should().BeNull();
        done.JobProgress.Should().BeNull();
        done.InitialLoadDone.Should().BeTrue();
    }

    [Fact]
    public async Task ASyncRequestWhileTheFirstLoadRuns_WaitsForIt_ThenRuns()
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Pair();
        rig.Controller.SetSchedule(BlindBackupSchedule.Off);

        var load = rig.Controller.RunHeavyAsync(false);
        Task<string> syncRequest;
        try
        {
            await rig.Replica.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            syncRequest = rig.Controller.RequestSyncAsync();

            await Task.Delay(300);
            syncRequest.IsCompleted.Should().BeFalse("one job at a time: the sync waits for the load");
            rig.Sync.Calls.Should().Be(0);
            rig.Controller.GetStatus().ActiveJob.Should().Be("First load");
        }
        finally
        {
            rig.Replica.Finish.TrySetResult();
        }
        await load;
        (await syncRequest.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("Synced.");
        rig.Sync.Calls.Should().Be(1);
        rig.Controller.GetStatus().LastSyncAt.Should().Be(rig.Clock.GetUtcNow());
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NullReferenceException))]
    public async Task AFailedSync_IsLogged_WhateverTheExceptionType(Type exceptionType)
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Pair();
        rig.MarkFirstLoadDone();
        rig.Sync.Failure = (Exception)Activator.CreateInstance(exceptionType, "link went down")!;

        var result = await rig.Controller.RequestSyncAsync();

        result.Should().Be("Sync failed: link went down");
        rig.Controller.GetStatus().RecentLog.Should().Contain(e => e.Kind == "sync" && e.Message == "Sync failed: link went down");
        rig.Controller.GetStatus().LastSyncAt.Should().BeNull("a failed round is not a sync");
        rig.Controller.GetStatus().ActiveJob.Should().BeNull("the gate is released after a failure");
        (await rig.Controller.RequestSyncAsync()).Should().Be("Sync failed: link went down", "the next request is not stuck behind the failure");
    }

    [Fact]
    public async Task ACancelledSync_IsNotAFailure_AndNotLogged()
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Pair();
        rig.MarkFirstLoadDone();
        using var cts = new CancellationTokenSource();
        rig.Sync.Failure = new OperationCanceledException(cts.Token);
        await cts.CancelAsync();

        var act = () => rig.Controller.RequestSyncAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        rig.Controller.GetStatus().RecentLog.Should().NotContain(e => e.Message.StartsWith("Sync failed"));
    }

    [Fact]
    public async Task ExportBackup_OnlyExportsFilesOfTheBackupFolder_AndNeedsAnExporter()
    {
        var withoutExporter = new Rig();
        var noExporter = () => withoutExporter.Controller.ExportBackupAsync("x.bmbbackup");
        await noExporter.Should().ThrowAsync<InvalidOperationException>();

        var rig = new Rig(exporter: new MemoryExporter());
        await rig.Controller.InitializeAsync();
        var outside = () => rig.Controller.ExportBackupAsync(Path.Combine("..", "beememorybank.db"));
        await outside.Should().ThrowAsync<FileNotFoundException>("a path outside the backup list is not exported");
    }

    [Fact]
    public async Task Wipe_ThroughTheController_StopsTheHost_ForgetsKeysAndState_AndRestarts()
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Pair();
        rig.State.Values.Should().NotBeEmpty();

        await rig.Controller.DisconnectAndWipeAsync();

        rig.Lifecycle.Calls.Should().Equal("StopBackgroundWork", "StopBackupService", "RestartAfterWipe");
        rig.Secrets.IdentitySeed.Should().BeNull();
        rig.Secrets.BackupKey.Should().BeNull();
        rig.State.Values.Values.Should().OnlyContain(v => v == null);
        rig.State.Erased.Should().BeTrue("the host is told to remove its state file too, not only to blank the values (equal on every host)");
        File.Exists(Path.Combine(rig.Dir, "beememorybank.db")).Should().BeFalse("the replica database is removed");
    }

    [Fact]
    public async Task Wipe_WhileTheFirstLoadRuns_CancelsIt_WaitsUntilItReallyEnded_AndOnlyThenWipes()
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Pair();
        rig.Replica.IgnoreCancel = true; // a job that is slow to react: the wipe must not run over it
        var job = rig.Controller.RunHeavyAsync(false);
        await rig.Replica.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var wipe = rig.Controller.DisconnectAndWipeAsync();
        await Task.Delay(300);

        wipe.IsCompleted.Should().BeFalse("the first load is still running: nothing may be deleted beneath it");
        rig.Secrets.IdentitySeed.Should().NotBeNull("the keys stay while a job may still use them");
        rig.Lifecycle.Calls.Should().NotContain("RestartAfterWipe");
        File.Exists(Path.Combine(rig.Dir, "beememorybank.db")).Should().BeTrue();
        rig.Lifecycle.Calls.Should().Contain("StopBackgroundWork", "the host is told to stop first");

        rig.Replica.Finish.TrySetResult();
        await wipe.WaitAsync(TimeSpan.FromSeconds(20));
        await job.WaitAsync(TimeSpan.FromSeconds(10));

        rig.Secrets.IdentitySeed.Should().BeNull();
        rig.Lifecycle.Calls.Should().Equal("StopBackgroundWork", "StopBackupService", "RestartAfterWipe");
    }

    [Fact]
    public async Task Wipe_WhileASyncRuns_CancelsItsToken_AndRefusesNewWorkUntilTheWipeIsOver()
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Pair();
        rig.MarkFirstLoadDone();
        rig.Sync.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = rig.Controller.RequestSyncAsync();
        await rig.Sync.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var wipe = rig.Controller.DisconnectAndWipeAsync();
        await rig.Sync.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the wipe fired the round's token
        var act = async () => await sync.WaitAsync(TimeSpan.FromSeconds(10));
        await act.Should().ThrowAsync<OperationCanceledException>();
        await wipe.WaitAsync(TimeSpan.FromSeconds(20));

        (await rig.Controller.RequestSyncAsync()).Should().Contain("wiped", "no round starts while or after the wipe");
        rig.Secrets.IdentitySeed.Should().BeNull();
    }

    [Fact]
    public async Task ASyncRoundOfTheHostsOwnWorker_ShowsAsSyncInTheStatus()
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Controller.GetStatus().ActiveJob.Should().BeNull();

        // the Android sync worker registers in BlindActivity itself; the controller did not start this round
        var activity = rig.Provider.GetRequiredService<BlindActivity>();
        using (activity.TryBegin(BlindActivity.Sync))
            rig.Controller.GetStatus().ActiveJob.Should().Be("Sync");

        rig.Controller.GetStatus().ActiveJob.Should().BeNull("the round ended");
    }

    [Fact]
    public async Task Wipe_ThatCannotStopTheWork_InTime_DeletesNothing_AndAllowsAnotherTry()
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Pair();
        rig.Replica.IgnoreCancel = true;
        var job = rig.Controller.RunHeavyAsync(false);
        await rig.Replica.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var act = () => BlindPhoneReset.WipeAndRestartAsync(rig.Provider, rig.Dir, TimeSpan.FromMilliseconds(200));
        var failure = (await act.Should().ThrowAsync<AggregateException>()).Which;

        failure.Message.Should().Contain("did not stop in time").And.Contain("Nothing was deleted");
        rig.Lifecycle.Calls.Should().Contain("ResumeBackgroundWork", "the schedule the stop cancelled is given back");
        rig.Secrets.IdentitySeed.Should().NotBeNull();
        rig.Lifecycle.Calls.Should().NotContain("RestartAfterWipe");
        File.Exists(Path.Combine(rig.Dir, "beememorybank.db")).Should().BeTrue();

        rig.Replica.Finish.TrySetResult();
        await job.WaitAsync(TimeSpan.FromSeconds(10));
        await BlindPhoneReset.WipeAndRestartAsync(rig.Provider, rig.Dir, TimeSpan.FromSeconds(10));
        rig.Secrets.IdentitySeed.Should().BeNull("the second try finishes the wipe");
    }

    [Fact]
    public async Task ASecretStoreThatThrows_IsNotALostKey_StatusDoesNotThrow_AndRecoversWhenItAnswersAgain()
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Controller.GetStatus().AwaitingAnswer.Should().BeTrue();

        rig.Secrets.ReadFailure = new UnauthorizedAccessException("The keychain is locked.");
        var act = () => rig.Controller.GetStatus();
        var status = act.Should().NotThrow().Subject;

        status.KeyStoreUnavailable.Should().Be("UnauthorizedAccessException: The keychain is locked.");
        status.BackupKeyLost.Should().BeFalse("a store that does not answer is not a lost key: nobody is pushed towards a wipe");
        status.AwaitingAnswer.Should().BeFalse("not known, so not claimed");
        status.StartError.Should().BeNull();
        rig.Controller.PairingCode().Should().BeNull("the code needs the store; no throw");

        rig.Secrets.ReadFailure = null;
        var back = rig.Controller.GetStatus();
        back.KeyStoreUnavailable.Should().BeNull();
        back.AwaitingAnswer.Should().BeTrue();
        rig.Controller.PairingCode().Should().StartWith("bmb-blind-phone:");
    }

    [Fact]
    public async Task ASecretStoreThatThrowsALongMessage_GivesAShortReason()
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Secrets.ReadFailure = new InvalidOperationException(new string('x', 400) + "\r\nsecond line");

        var reason = rig.Controller.GetStatus().KeyStoreUnavailable!;

        reason.Should().StartWith("InvalidOperationException: xxx").And.EndWith("...");
        reason.Length.Should().BeLessThan(170);
        reason.Should().NotContain("\n");
    }

    [Fact]
    public async Task ARepeatingFirstLoadFailure_IsOneLogLine_ThenOnlyAtDoublingAttempts_AndTheStatusCountsAttempts()
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Pair();
        rig.Controller.SetSchedule(BlindBackupSchedule.Off);
        rig.Replica.Failure = new HttpRequestException("The server answered 401 (Unauthorized).");

        for (var attempt = 1; attempt <= 9; attempt++)
        {
            (await rig.Controller.RunHeavyAsync(false)).Should().Be("First load failed: The server answered 401 (Unauthorized).");
            var failure = rig.Controller.GetStatus().LastFailure!;
            failure.Title.Should().Be("First load");
            failure.Message.Should().Be("The server answered 401 (Unauthorized).");
            failure.Attempts.Should().Be(attempt);
            failure.At.Should().Be(rig.Clock.GetUtcNow());
            rig.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        // The log lists the newest line first.
        var lines = rig.Controller.GetStatus().RecentLog.Where(e => e.Message.Contains("First load failed")).Select(e => e.Message).Reverse().ToList();
        lines.Should().Equal(
            "First load failed: The server answered 401 (Unauthorized).",
            "First load failed: The server answered 401 (Unauthorized). (attempt 2)",
            "First load failed: The server answered 401 (Unauthorized). (attempt 4)",
            "First load failed: The server answered 401 (Unauthorized). (attempt 8)");
        rig.Replica.Starts.Should().Be(9);

        // Another reason is a new failure and a new line; a success clears it.
        rig.Replica.Failure = new HttpRequestException("The connection was refused.");
        await rig.Controller.RunHeavyAsync(false);
        rig.Controller.GetStatus().LastFailure!.Attempts.Should().Be(1);
        rig.Controller.GetStatus().RecentLog.Should().Contain(e => e.Message == "First load failed: The connection was refused.");

        rig.Replica.Failure = null;
        rig.Replica.Finish.TrySetResult();
        (await rig.Controller.RunHeavyAsync(false)).Should().Be("Nothing due.");
        var done = rig.Controller.GetStatus();
        done.LastFailure.Should().BeNull();
        done.InitialLoadDone.Should().BeTrue();
    }

    [Fact]
    public async Task AChangedHandlerThatThrows_DoesNotBreakAJob_NorLeaveTheGateHeld()
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Pair();
        rig.MarkFirstLoadDone();
        rig.Controller.Changed += () => throw new InvalidOperationException("the screen is gone");

        (await rig.Controller.RequestSyncAsync().WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("Synced.");
        (await rig.Controller.RequestSyncAsync().WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("Synced.", "the gate was released");
        rig.Controller.SetSchedule(BlindBackupSchedule.Daily);
        rig.Controller.GetStatus().Schedule.Should().Be(BlindBackupSchedule.Daily);
    }

    // ---- rig --------------------------------------------------------------------------------------------------------------

    private sealed class Rig
    {
        public string Dir { get; }
        public MemoryState State { get; } = new();
        public MemorySecrets Secrets { get; } = new();
        public Lifecycle Lifecycle { get; } = new();
        public BlockingReplica Replica { get; } = new();
        public FakeSync Sync { get; } = new();
        public ManualClock Clock { get; } = new();
        public BlindAppController Controller { get; }
        private readonly ServiceProvider _services;
        public IServiceProvider Provider => _services;

        public Rig(string? dir = null, IBlindBackupExporter? exporter = null)
        {
            Dir = dir ?? Path.Combine(Path.GetTempPath(), "bmb-appcore-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            var services = new ServiceCollection();
            services.AddSingleton<IBlindStateStore>(State);
            services.AddSingleton<IBlindSecretStore>(Secrets);
            services.AddSingleton<IBlindLifecycle>(Lifecycle);
            if (exporter is not null) services.AddSingleton(exporter);
            BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(
                Dir, Path.Combine(Dir, "beememorybank.db"), Clock, () => "  Test desktop "));
            // The network side is replaced by fakes; everything else is the real composition.
            services.AddSingleton<IBlindReplicaSource>(Replica);
            services.AddSingleton<IBlindPhoneSync>(Sync);
            _services = services.BuildServiceProvider();
            Controller = _services.GetRequiredService<BlindAppController>();
        }

        /// <summary>A call code the identity's own pairing secret accepted, as after the computer's answer.</summary>
        public void Pair()
        {
            var state = _services.GetRequiredService<BlindPhoneState>();
            var secret = Secrets.PairingSecret ?? throw new InvalidOperationException("initialize first");
            var key = new byte[32];
            key[0] = 1;
            state.CallCode = BlindCallCode.Create("https://127.0.0.1:5610", Guid.NewGuid(), Base64Url.EncodeToString(new byte[32]), key, secret);
            Secrets.PairingSecret = null;
        }

        public void MarkFirstLoadDone() => _services.GetRequiredService<BlindPhoneState>().InitialLoadDone = true;
    }

    private sealed class MemoryState : IBlindStateStore
    {
        public Dictionary<string, string?> Values { get; } = [];
        public bool Erased { get; private set; }
        public string? Get(string key) => Values.GetValueOrDefault(key);
        public void Set(string key, string? value) => Values[key] = value;
        public void Erase() => Erased = true;
    }

    private sealed class MemorySecrets : IBlindSecretStore
    {
        public byte[]? IdentitySeed { get; private set; }
        public byte[]? BackupKey { get; private set; }
        public byte[]? PairingSecret { get; set; }

        /// <summary>When set, reading a secret throws it: a keychain that is locked, denied or busy.</summary>
        public Exception? ReadFailure { get; set; }

        public void SaveIdentitySeed(byte[] seed) => IdentitySeed = [.. seed];
        public byte[]? LoadIdentitySeed() => ReadFailure is { } f ? throw f : IdentitySeed is null ? null : [.. IdentitySeed];
        public void SaveBackupKey(byte[] key) => BackupKey = [.. key];
        public byte[]? LoadBackupKey() => ReadFailure is { } f ? throw f : BackupKey is null ? null : [.. BackupKey];
        public void SavePairingSecret(byte[] secret) => PairingSecret = [.. secret];
        public byte[]? LoadPairingSecret() => ReadFailure is { } f ? throw f : PairingSecret is null ? null : [.. PairingSecret];
        public void ClearPairingSecret() => PairingSecret = null;
        public void Clear() { IdentitySeed = null; BackupKey = null; PairingSecret = null; }
    }

    private sealed class Lifecycle : IBlindLifecycle
    {
        public List<string> Calls { get; } = [];
        public void StopBackgroundWork() => Calls.Add("StopBackgroundWork");
        public void StopBackupService() => Calls.Add("StopBackupService");
        public void ResumeBackgroundWork() => Calls.Add("ResumeBackgroundWork");
        public void RestartAfterWipe() => Calls.Add("RestartAfterWipe");
    }

    /// <summary>A first load that starts, reports progress when told to and finishes when the test says so.</summary>
    private sealed class BlockingReplica : IBlindReplicaSource
    {
        private IProgress<double>? _progress;
        public int Starts;

        /// <summary>A job slow to react to its cancellation: it ends only when the test says so.</summary>
        public bool IgnoreCancel { get; set; }

        /// <summary>When set, a start fails with it at once (a refused or broken first load).</summary>
        public Exception? Failure { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task FetchAndInstallAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct)
        {
            Interlocked.Increment(ref Starts);
            if (Failure is { } failure) throw failure;
            _progress = progress;
            Started.TrySetResult();
            if (IgnoreCancel) await Finish.Task;
            else await Finish.Task.WaitAsync(ct);
        }

        public void ReportProgress(double value) => _progress!.Report(value);
    }

    private sealed class FakeSync : IBlindPhoneSync
    {
        public int Calls;
        public Exception? Failure { get; set; }

        /// <summary>When set, a round waits here (and notes its token firing in <see cref="Cancelled"/>).</summary>
        public TaskCompletionSource? Hold { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SyncOnceAsync(BlindCallCode target, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Hold is { } hold)
            {
                ct.Register(() => Cancelled.TrySetResult());
                Entered.TrySetResult();
                await hold.Task.WaitAsync(ct);
            }
            if (Failure is { } failure) throw failure;
        }
    }

    private sealed class MemoryExporter : IBlindBackupExporter
    {
        public Task<Stream> CreateAsync(string suggestedName, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream());
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
