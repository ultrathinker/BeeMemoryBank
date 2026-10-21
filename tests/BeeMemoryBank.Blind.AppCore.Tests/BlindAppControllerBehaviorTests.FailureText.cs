using System.Buffers.Text;
using System.Text.Json;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Fix round 2: ONE failure-text rule at every place where the shared core shows or stores an exception. An adversarial exception (a path, a key alias, a request address with a token, a library's
/// words, a canary) is pushed through the real controller, and nothing a head can show may hold any of it: the sentence a run returns, every string of the status and of the home view, the job's
/// outcome, the log, and the files the run leaves behind. A text written by 2.6.4 (the raw message) is cleaned when it is read.
/// (Part of <see cref="BlindAppControllerBehaviorTests"/>: the heavy job's one-at-a-time lock is static, so every test that runs it lives in that one class.)
/// </summary>
public sealed partial class BlindAppControllerBehaviorTests
{
    public static TheoryData<string> Adversarial => FailureCanaries.Names;

    private static void AssertNothingLeaks(Rig rig, string? returned, BlindJobAsk? ask = null)
    {
        var status = rig.Controller.GetStatus();
        FailureCanaries.AssertClean(returned, "the sentence the run returns");
        FailureCanaries.AssertClean(status, "the status (LastFailure, StartError, KeyStoreUnavailable, the log)");
        FailureCanaries.AssertClean(BlindHomeView.Build(status, rig.Controller.PairingCode(), backupRunning: false), "the home view");
        FailureCanaries.AssertClean(BlindHomeView.ProblemText(status), "the problem line");
        if (ask is not null)
            FailureCanaries.AssertClean(BlindJobWait.Judge(ask, status, running: false, rig.Clock.GetUtcNow()), "the job's outcome");
        FailureCanaries.AssertClean(rig.State.Values, "the state store");
        foreach (var text in FailureCanaries.StoredTexts(rig.Dir))
            FailureCanaries.Leaks(text).Should().BeFalse("the files a run leaves (the log, the state) must not hold the exception's words");
    }

    [Theory]
    [MemberData(nameof(Adversarial))]
    public async Task AFailedFirstLoad_ShowsNoWordOfTheException_InAnyStringAHeadCanShow_OrInTheFilesItLeaves(string name)
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Pair();
        rig.Controller.SetSchedule(BlindBackupSchedule.Off);
        var ask = BlindJobWait.Ask(BlindJobWait.FirstLoad, rig.Controller.GetStatus(), rig.Clock.GetUtcNow());
        rig.Replica.Failure = FailureCanaries.Make(name);

        var result = await rig.Controller.RunHeavyAsync(false);

        AssertNothingLeaks(rig, result, ask);
        result.Should().NotBeNullOrWhiteSpace();
        var status = rig.Controller.GetStatus();
        if (FailureCanaries.IsRecordedByTheJob(name))
        {
            // The job recorded its own failure: it is the stored LastFailure, the log line and the job's outcome, and all three are the kind of failure.
            status.LastFailure.Should().NotBeNull();
            status.LastFailure!.Message.Should().Be(BlindFailureText.Say(FailureCanaries.Make(name)));
            result.Should().Be("First load failed: " + status.LastFailure.Message);
            BlindJobWait.Judge(ask, status, running: false, rig.Clock.GetUtcNow()).Should().Be(new BlindJobOutcome(BlindJobPhase.Failed, status.LastFailure.Message));
        }
        else
        {
            result.Should().StartWith("Stopped by an unexpected error");
            status.RecentLog.Should().Contain(e => e.Message.StartsWith("Stopped by an unexpected error", StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(Adversarial))]
    public async Task AFailedSync_ShowsNoWordOfTheException_InTheResult_TheLog_TheViewOrTheFilesItLeaves(string name)
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Pair();
        rig.MarkFirstLoadDone();
        rig.Sync.Failure = FailureCanaries.Make(name);

        var result = await rig.Controller.RequestSyncAsync();

        AssertNothingLeaks(rig, result);
        result.Should().StartWith("Sync failed: ");
        rig.Controller.GetStatus().RecentLog.Should().Contain(e => e.Kind == "sync" && e.Message == result);
        BlindHomeView.Build(rig.Controller.GetStatus(), null, false).Problem.Should().NotBeNull().And.Contain("Sync failed");
    }

    [Theory]
    [MemberData(nameof(Adversarial))]
    public async Task AnAppThatCouldNotBeSetUp_SaysTheKindOfFailure_InTheStartError_TheLogAndTheView(string name)
    {
        var rig = new Rig(configure: services => services.AddSingleton(new BlindStartup(_ => Task.FromException(FailureCanaries.Make(name)))));

        await rig.Controller.InitializeAsync();

        var status = rig.Controller.GetStatus();
        status.StartError.Should().NotBeNullOrWhiteSpace("the failure of the first set-up is kept for the screen");
        status.StartError.Should().Contain(BlindFailureText.WithoutFullStop(BlindFailureText.Say(FailureCanaries.Make(name))));
        status.RecentLog.Should().Contain(e => e.Kind == "start" && e.Message.StartsWith("Could not set the app up: ", StringComparison.Ordinal));
        AssertNothingLeaks(rig, status.StartError);
        BlindHomeView.Build(status, null, false).StartErrorText.Should().StartWith("Could not set the phone up: ");
    }

    [Theory]
    [MemberData(nameof(Adversarial))]
    public async Task ASecretStoreThatDoesNotAnswer_IsToldByTheKindOfFailure_NeverByTheStoresOwnWords(string name)
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Secrets.ReadFailure = FailureCanaries.Make(name);

        var status = rig.Controller.GetStatus();

        status.KeyStoreUnavailable.Should().NotBeNullOrWhiteSpace();
        AssertNothingLeaks(rig, status.KeyStoreUnavailable);
        BlindHomeView.Build(status, null, false).KeyStoreText.Should().Contain("did not answer (");
    }

    // ---- what 2.6.4 stored: the raw message of an exception, written into the log file ----------------------------------------------------------

    /// <summary>A line as 2.6.4 wrote it: the entry of that version had no stamp (fix round 3: what is shown of a line is decided by that stamp).</summary>
    private static string RawLine(string at, string kind, string message) => JsonSerializer.Serialize(new { At = DateTimeOffset.Parse(at), Kind = kind, Message = message });

    [Fact]
    public async Task ALogWrittenBy264_WithRawMessages_ShowsOnlyTimeKindAndTheFixedSentence_AndALineOfThisVersionShowsItsText()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb-appcore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var node = Guid.NewGuid();
        var lines = new[]
        {
            RawLine("2026-10-04T10:00:00+00:00", "pairing", $"Paired: calls https://127.0.0.1:5610 (node {node})."),
            RawLine("2026-10-04T10:05:00+00:00", "load", $"First load failed: The process cannot access the file '{FailureCanaries.SecretPath}' because it is being used by another process. {FailureCanaries.Canary}"),
            RawLine("2026-10-04T10:06:00+00:00", "backup", $"Backup failed: Could not find file '/data/user/0/app/files/blind-backups/x.bmbbackup' {FailureCanaries.Canary}"),
            RawLine("2026-10-04T10:07:00+00:00", "start", "Could not set the app up: SQLite Error 14: 'unable to open database file'. C:\\data\\beememorybank.db"),
            RawLine("2026-10-04T10:08:00+00:00", "backup", "Backup made: bmb-phone-20261004-100800.bmbbackup (12 KiB)."),
            RawLine("2026-10-04T10:09:00+00:00", "sync", $"Sync failed: An error occurred while sending the request to https://canary-host.invalid/?token={FailureCanaries.Canary}"),
        };
        File.WriteAllLines(BlindPaths.Log(dir), lines);
        var rig = new Rig(dir);
        await rig.Controller.InitializeAsync();
        rig.Pair();

        var status = rig.Controller.GetStatus();
        var view = BlindHomeView.Build(status, null, false);

        FailureCanaries.AssertClean(status, "the status read from a log of 2.6.4");
        FailureCanaries.AssertClean(view, "the home view built from a log of 2.6.4");
        // Every line of 2.6.4 keeps its time and kind; none of them shows its text, not even the harmless ones and not the pairing line (the kind of a stored line proves nothing).
        foreach (var kind in new[] { "load", "sync", "backup", "start", "pairing" })
            status.RecentLog.Where(e => e.Kind == kind && e.At < DateTimeOffset.Parse("2026-10-04T11:00:00+00:00")).Should().NotBeEmpty().And.OnlyContain(e => e.Message == BlindFailureText.StoredTextHidden);
        status.RecentLog.Should().NotContain(e => e.Message.Contains("bmb-phone-20261004-100800") || e.Message.Contains("https://127.0.0.1:5610"));
        view.Log.Should().Contain(BlindFailureText.StoredTextHidden).And.NotContain("https://127.0.0.1:5610").And.NotContain("bmb-phone-20261004-100800");
        view.Problem.Should().Contain(BlindFailureText.StoredTextHidden, "the newest problem is the old sync line, shown as the fixed sentence");
        File.ReadAllText(BlindPaths.Log(dir)).Should().Contain(FailureCanaries.Canary, "what is stored is not rewritten: it is only not shown");

        // What this version writes is stamped and shows its text, in the same file.
        BlindRunReport.Record(rig.Provider.GetRequiredService<BlindPhoneLog>(), "sync", "Sync failed: no answer: connection refused");
        var after = rig.Controller.GetStatus();
        after.RecentLog.Should().Contain(e => e.Kind == "sync" && e.Message == "Sync failed: no answer: connection refused");
        after.RecentLog.Where(e => e.At < DateTimeOffset.Parse("2026-10-04T11:00:00+00:00")).Should().OnlyContain(e => e.Message == BlindFailureText.StoredTextHidden);
        BlindHomeView.Build(after, null, false).Problem.Should().Contain("no answer: connection refused");
        File.ReadAllLines(BlindPaths.Log(dir)).Last().Should().Contain("\"v\":" + BlindFailureText.StoredVersion);
    }

    [Theory]
    [InlineData("Invalid argument (Parameter 'filePath') token=hunter2 account 8f3a-canary-123")]
    [InlineData("token=hunter2")]
    [InlineData("account 8f3a-canary-123")]
    [InlineData("alias seed-alias-xq7 was rejected")]
    public async Task ALogOf264_WithAPlainHostileMessage_ReachesNoScreen_ThroughTheControllerTheHomeViewTheCardOrTheJobWait(string hostile)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb-appcore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var node = Guid.NewGuid();
        var written = new[]
        {
            RawLine("2026-10-04T10:00:00+00:00", "pairing", hostile),
            RawLine("2026-10-04T10:01:00+00:00", "pairing", $"Paired: calls https://127.0.0.1:5610 (node {node}). {hostile}"),
            RawLine("2026-10-04T10:05:00+00:00", "load", "First load failed: " + hostile),
            RawLine("2026-10-04T10:09:00+00:00", "sync", "Sync failed: " + hostile),
        };
        File.WriteAllLines(BlindPaths.Log(dir), written);
        var rig = new Rig(dir);
        await rig.Controller.InitializeAsync();
        rig.Pair();

        var status = rig.Controller.GetStatus();
        var now = rig.Clock.GetUtcNow();
        var entry = new BlindAccountEntry("0123456789abcdef0123456789abcdef", BlindAccountLayouts.Dir, "Friend", BlindAccountPhases.Active, SourceOrigin: "https://127.0.0.1:5610");
        var texts = new[]
        {
            string.Join("\n", FailureCanaries.Strings(status)),
            string.Join("\n", FailureCanaries.Strings(BlindHomeView.Build(status, null, false))),
            string.Join("\n", FailureCanaries.Strings(BlindAccountCard.Build(new BlindAccountSnapshot(entry, status), TimeZoneInfo.Utc, now))),
            string.Join("\n", FailureCanaries.Strings(BlindJobWait.Judge(BlindJobWait.Ask(BlindJobWait.Sync, status, now.AddMinutes(-1)), status, false, now))),
        };

        foreach (var text in texts)
            new[] { "filePath", "token=", "hunter2", "8f3a-canary-123", "seed-alias-xq7" }.Where(n => text.Contains(n, StringComparison.OrdinalIgnoreCase)).Should().BeEmpty();
        File.ReadAllLines(BlindPaths.Log(dir)).Take(written.Length).Should().Equal(written, "nothing is rewritten on disk");
    }

    [Fact]
    public async Task ALineThatWasHidden_DoesNotMakeTheSameNewLineBeLoggedAgainAndAgain()
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        var log = rig.Provider.GetRequiredService<BlindPhoneLog>();

        BlindRunReport.Record(log, "sync", "Sync failed: something/with a slash");
        BlindRunReport.Record(log, "sync", "Sync failed: something/with a slash");

        log.LatestStored(10).Count(e => e.Kind == "sync").Should().Be(1, "the same line twice in a row is one line, whatever the reader hides");
    }

    [Fact]
    public void AStatusMadeByHand_WithRawMessages_IsCleanedByTheViewsToo()
    {
        var raw = $"Could not find file '{FailureCanaries.SecretPath}' {FailureCanaries.Canary}";
        var at = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var status = new BlindAppStatus(Guid.NewGuid(), "device", true, false, null, null, BlindBackupSchedule.Off, false, null, null, [],
            [new BlindPhoneLog.Entry(at, "sync", "Sync failed: " + raw), new BlindPhoneLog.Entry(at, "pin", "Refused 10.0.0.1:5610: it answered with another key than the one pinned when this phone was paired. Nothing was sent.")],
            Endpoint: "https://127.0.0.1:5610", StartError: raw, KeyStoreUnavailable: "UnauthorizedAccessException: " + raw,
            LastFailure: new BlindAppFailure("First load", raw, at, 3));

        var view = BlindHomeView.Build(status, null, false);

        FailureCanaries.AssertClean(view, "the home view");
        view.Load.Should().Contain(BlindFailureText.StoredTextHidden).And.Contain("3 tries in a row");
        view.Problem.Should().Contain(BlindFailureText.StoredTextHidden);
        view.Log.Should().Contain("Refused 10.0.0.1:5610", "a line of the code's own is shown as it is");
        view.StartErrorText.Should().Contain(BlindFailureText.StoredTextHidden);
        FailureCanaries.AssertClean(BlindJobWait.Judge(BlindJobWait.Ask(BlindJobWait.FirstLoad, status with { LastFailure = null }, at.AddMinutes(-1)), status, false, at.AddMinutes(1)), "the job's outcome");
        BlindJobWait.Judge(BlindJobWait.Ask(BlindJobWait.FirstLoad, status with { LastFailure = null }, at.AddMinutes(-1)), status, false, at.AddMinutes(1))
            .Should().Be(new BlindJobOutcome(BlindJobPhase.Failed, BlindFailureText.StoredTextHidden));
    }

    // ---- the long job on its own: a failed baseline update, a failed backup ---------------------------------------------------------------------

    private sealed class ListStore : IBlindPhoneStore
    {
        private readonly Dictionary<string, string?> _values = [];
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public void Set(string key, string? value) => _values[key] = value;
    }

    private sealed class BackupKeys : IBlindPhoneKeys
    {
        public void SaveIdentitySeed(byte[] seed) { }
        public void SaveBackupKey(byte[] key) { }
        public byte[]? LoadBackupKey() => new byte[32];
        public void SavePairingSecret(byte[] secret) { }
        public byte[]? LoadPairingSecret() => null;
        public void ClearPairingSecret() { }
        public void Clear() { }
    }

    private sealed class SealedRecovery(Guid node) : IRecoverySetJsonSource
    {
        public Task<string> BuildJsonAsync(CancellationToken ct) => Task.FromResult(
            "{\"format\":\"bmb-recovery-set-v1\",\"boxes\":[],\"links\":[],\"anchors\":[],\"sealed_secrets\":[{\"name\":\"android-backup:" + node
            + "\",\"dek_fingerprint\":\"f\",\"wrapped\":\"AA==\",\"iv\":\"AA==\",\"updated_at\":\"x\"}],\"created_at\":\"x\"}");
    }

    private sealed class HeavyRig : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "bmb-appcore-tests", Guid.NewGuid().ToString("N"));
        public BlindPhoneLog Log { get; }
        public BlindHeavyWork Heavy { get; }
        public Exception? PackageFailure { get; set; }
        public Exception? UpdateFailure { get; set; }
        public string LogPath => Path.Combine(Dir, "log.jsonl");

        public HeavyRig()
        {
            Directory.CreateDirectory(Dir);
            var clock = new ManualClock();
            var state = new BlindPhoneState(new ListStore());
            Log = new BlindPhoneLog(LogPath, clock);
            var node = BlindNodeId.NewId();
            var key = new byte[32];
            key[0] = 1;
            state.NodeId = node;
            state.CallCode = BlindCallCode.Create("https://127.0.0.1:5610", Guid.NewGuid(), Base64Url.EncodeToString(new byte[32]), key, BlindPairingSecret.New());
            state.InitialLoadDone = true;
            var runner = new BlindPhoneBackupRunner(state, new BackupKeys(), new FailingPackage(this), new SealedRecovery(node), Log, Path.Combine(Dir, "backups"), clock);
            Heavy = new BlindHeavyWork(state, new FailingReplica(this), runner, Log, Path.Combine(Dir, "replica"), clock);
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed class FailingPackage(HeavyRig rig) : IBlindPackageSource
    {
        public Task CreateAsync(string destinationPath, CancellationToken ct) => rig.PackageFailure is { } failure ? throw failure : Task.CompletedTask;
    }

    private sealed class FailingReplica(HeavyRig rig) : IBlindReplicaSource
    {
        public Task FetchAndInstallAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> UpdateBaselineAsync(BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct) =>
            rig.UpdateFailure is { } failure ? throw failure : Task.FromResult(false);
    }

    [Theory]
    [InlineData("io-path")]
    [InlineData("io-inner-path")]
    [InlineData("unauthorized")]
    [InlineData("invalid-data")]
    public async Task AFailedBackup_IsToldByTheKindOfFailure_InTheJobsSentence_TheOutcomeAndTheLog(string name)
    {
        using var rig = new HeavyRig();
        rig.PackageFailure = FailureCanaries.Make(name);
        var kind = BlindFailureText.Say(FailureCanaries.Make(name));

        var result = await rig.Heavy.RunAsync(forceBackup: true, CancellationToken.None);

        result.Should().Be(kind, "the sentence the job returns is what a head and the controller's log show");
        rig.Log.Latest(20).Should().Contain(e => e.Kind == "backup" && e.Message == "Backup failed: " + kind);
        FailureCanaries.AssertClean(result, "the job's sentence");
        FailureCanaries.AssertClean(rig.Log.LatestStored(50), "the log as it is stored");
        FailureCanaries.AssertClean(rig.Heavy.LastFailure, "the failure the job is in");
        FailureCanaries.Leaks(File.ReadAllText(rig.LogPath)).Should().BeFalse("the log file");
    }

    [Theory]
    [InlineData("io-path")]
    [InlineData("http-uri-token")]
    [InlineData("http-wrapped-in-io")]
    [InlineData("invalid-data")]
    public async Task AFailedBaselineUpdate_IsTheStoredFailure_ByItsKind_NotByTheWordsOfTheException(string name)
    {
        using var rig = new HeavyRig();
        rig.UpdateFailure = FailureCanaries.Make(name);
        var kind = BlindFailureText.Say(FailureCanaries.Make(name));

        await rig.Heavy.RunAsync(forceBackup: false, CancellationToken.None);

        var failure = rig.Heavy.LastFailure.Should().NotBeNull().And.Subject.As<BlindHeavyFailure>();
        failure.Title.Should().Be("Baseline update");
        failure.Message.Should().Be(kind);
        failure.Result.Should().Be("Baseline update failed: " + kind);
        FailureCanaries.AssertClean(failure, "the stored failure");
        FailureCanaries.AssertClean(rig.Log.LatestStored(50), "the log as it is stored");
        FailureCanaries.Leaks(File.ReadAllText(rig.LogPath)).Should().BeFalse("the log file");
    }

    // ---- the wipe: what it throws to a head -----------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Adversarial))]
    public async Task AWipeThatCannotFinish_ThrowsNoPathOrAliasToAHead_NotEvenInsideTheExceptionsItCarries(string name)
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();

        var act = () => BlindPhoneReset.WipeAsync(rig.Provider, rig.Dir, deletePath: _ => throw FailureCanaries.Make(name));

        var failure = (await act.Should().ThrowAsync<AggregateException>()).Which;
        FailureCanaries.AssertClean(failure, "the exception a head gets (its message and everything inside it)");
        FailureCanaries.Leaks(BeeMemoryBank.Sync.FailureText.Story(failure)).Should().BeFalse("the whole story a head's busy helper tells");
        failure.InnerExceptions.Should().OnlyContain(e => e.InnerException == null);
        failure.Message.Should().Contain("The wipe is not complete");
    }

    // ---- a sentence of this code, thrown on purpose, is still said as it is: through every path above ----------------------------------------

    private const string Chosen = "Choose a smaller number.";

    [Fact]
    public async Task ASentenceThrownOnPurpose_IsSaidAsItIs_ByTheRunTheSyncAndTheStart()
    {
        var rig = new Rig();
        await rig.Controller.InitializeAsync();
        rig.Pair();
        rig.Controller.SetSchedule(BlindBackupSchedule.Off);
        rig.Replica.Failure = new BlindSentenceException(Chosen);
        (await rig.Controller.RunHeavyAsync(false)).Should().Be("Stopped by an unexpected error: " + Chosen);
        rig.Replica.Failure = null;
        rig.Replica.Finish.TrySetResult();
        (await rig.Controller.RunHeavyAsync(false)).Should().Be("Nothing due.");

        rig.Sync.Failure = new BlindSentenceException(Chosen);
        (await rig.Controller.RequestSyncAsync()).Should().Be("Sync failed: " + Chosen);
        rig.Controller.GetStatus().RecentLog.Should().Contain(e => e.Kind == "sync" && e.Message == "Sync failed: " + Chosen);

        var failing = new Rig(configure: services => services.AddSingleton(new BlindStartup(_ => Task.FromException(new BlindSentenceException(Chosen)))));
        await failing.Controller.InitializeAsync();
        failing.Controller.GetStatus().StartError.Should().Be(Chosen);
        BlindHomeView.Build(failing.Controller.GetStatus(), null, false).StartErrorText.Should().Contain("Could not set the phone up: " + Chosen);
    }
}
