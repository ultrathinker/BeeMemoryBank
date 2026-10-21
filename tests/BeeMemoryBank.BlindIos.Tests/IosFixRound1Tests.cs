using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Boundary;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>A runtime of the iPhone app over fakes (Keychain, BGTask, file attributes, conditions) and a network that the test answers for.</summary>
internal sealed class IosFixRig : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "bmb-ios-fix1-" + Guid.NewGuid().ToString("N"));
    public IosKeychainSecretStoreTests.FakeKeychain Keychain { get; } = new();
    public RigBackground Background { get; } = new();
    public FakeFileProtection Files { get; } = new();
    public RigConditions Conditions { get; } = new();

    /// <summary>What the network of every account answers. The default: 503 at once.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));

    public IosBlindPaths Paths => new(Root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public async Task<IosBlindRuntime> StartedAsync(BlindDeviceSchedulerOptions? scheduler = null)
    {
        var runtime = IosBlindRuntime.Create(new IosBlindRuntimeOptions(
            Paths, new IosKeychainSecretStoreFactory(Keychain), Background, Conditions, Files, DisplayName: () => "iPhone", Scheduler: scheduler)
        {
            PrimaryHandlerFactory = _ => () => new Forward(this),
            LeaveHook = () => null,
        });
        await runtime.StartAsync();
        return runtime;
    }

    /// <summary>An account added the old way, answered by a computer at <paramref name="host"/>, its first load marked done.</summary>
    public async Task<string> PairedAsync(IosBlindRuntime runtime, string name, string host)
    {
        var id = (await runtime.Accounts.AddAsync(name)).AccountId!;
        runtime.Accounts.AcceptCallCode(AnswerFor(runtime.Accounts.For(id).PairingCode()!, host)).Should().BeNull();
        new BlindPhoneState(runtime.Accounts.ServicesFor(id).GetRequiredService<IBlindStateStore>()).InitialLoadDone = true;
        return id;
    }

    public static string AnswerFor(string phoneCode, string host)
    {
        BlindPhoneCode.TryParse(phoneCode, out var phone).Should().BeTrue();
        return BlindCallCode.Create($"https://{host}:5610", BlindNodeId.NewId(), System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)),
            Ed25519Signer.GenerateKeyPair().publicKey, phone!.Secret.ToArray()).ToString();
    }

    /// <summary>Everything an account's log says, in one string.</summary>
    public static string LogOf(IosBlindRuntime runtime, string accountId) =>
        string.Join("\n", runtime.Accounts.ServicesFor(accountId).GetRequiredService<BlindPhoneLog>().Latest(500).Select(e => e.Kind + " " + e.Message));

    public static string DeviceLogOf(IosBlindRuntime runtime) => string.Join("\n", runtime.DeviceLog.Latest(500).Select(e => e.Kind + " " + e.Message));

    public sealed class RigBackground : IIosBackgroundRequests
    {
        public int Submitted { get; private set; }
        public int Cancelled { get; private set; }
        public void Submit() => Submitted++;
        public void CancelAll() => Cancelled++;
    }

    public sealed class RigConditions : IBlindDeviceConditions
    {
        public bool? IsMetered { get; set; } = false;
        public bool? IsPowerSaving { get; set; } = false;
    }

    private sealed class Forward(IosFixRig rig) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => rig.Respond(request, ct);
    }
}

/// <summary>
/// Fix round 1 for the iPhone head (review revios): one background round at a time for the whole device, no account's name or fate in another account's log, failures told by their kind and
/// never by their message, the account that took a computer's answer named by the host, the shared records bound as they are, the daily silence reminder that repeats, and no wake for accounts
/// that are paused for good.
/// </summary>
public sealed class IosFixRound1Tests : IDisposable
{
    private const string Canary = "https://canary-host.invalid/redirect?token=CANARY-TOKEN-7c1e";

    private readonly IosFixRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static async Task<T> Within<T>(Task<T> task, int seconds = 60) => await task.WaitAsync(TimeSpan.FromSeconds(seconds));

    private static Task Within(Task task, int seconds = 60) => task.WaitAsync(TimeSpan.FromSeconds(seconds));

    // ---- F1: one device loop -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTwoKindsOfBackgroundWake_Overlapped_NeverRunTwoAccountsAtOnce_AndTheLaterOnesDoNothing()
    {
        await using var runtime = await _rig.StartedAsync();
        await _rig.PairedAsync(runtime, "Anna", "a.test");
        await _rig.PairedAsync(runtime, "Ben", "b.test");
        var inFlight = 0;
        var maxInFlight = 0;
        var gate = new object();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Respond = async (request, ct) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            lock (gate) maxInFlight = Math.Max(maxInFlight, now);
            entered.TrySetResult();
            try { await release.Task.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref inFlight); }
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        };

        var first = runtime.RunBackgroundAsync(work: false, CancellationToken.None);
        await Within(entered.Task);
        // iOS grants the processing window (and a second app refresh) while the first round is inside its first account.
        var second = await Within(runtime.RunBackgroundAsync(work: true, CancellationToken.None), 10);
        var third = await Within(runtime.RunBackgroundAsync(work: false, CancellationToken.None), 10);

        second.Skipped.Should().Contain("Another background round");
        third.Skipped.Should().Contain("Another background round");
        second.Accounts.Should().BeEmpty("a second round never enters an account beside the one that is running");
        third.Accounts.Should().BeEmpty();
        release.SetResult();
        var outcome = await Within(first);

        outcome.Skipped.Should().BeNull();
        outcome.Accounts.Should().HaveCount(2, "the first round served both accounts, one after the other");
        maxInFlight.Should().Be(1, "at no moment were two accounts in a request at the same time");
    }

    [Fact]
    public async Task ASkippedWake_IsCompletedAsNothingToDo_NotAsAFailure()
    {
        await using var runtime = await _rig.StartedAsync();
        await _rig.PairedAsync(runtime, "Anna", "a.test");
        await runtime.StartForegroundAsync();

        var wake = await Within(runtime.RunBackgroundAsync(work: false, CancellationToken.None), 10);

        wake.Skipped.Should().Contain("app is open");
        wake.EndedByIos.Should().BeFalse();
        (wake.Skipped is not null || !wake.EndedByIos).Should().BeTrue("IosBlindBackground completes the task as successful");
        wake.DeviceSummary.Should().Be(wake.Skipped);
    }

    [Fact]
    public async Task TheOpenApp_TakesThePlaceOfARunningWake_AndNoWakeRunsBesideTheLoop()
    {
        await using var runtime = await _rig.StartedAsync();
        await _rig.PairedAsync(runtime, "Anna", "a.test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.Respond = async (request, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        };
        var wake = runtime.RunBackgroundAsync(work: false, CancellationToken.None);
        await Within(entered.Task);
        _rig.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));

        // the person opens the app: the round that is on is asked to stop, and the loop starts only after it has really ended
        await Within(runtime.StartForegroundAsync());

        wake.IsCompleted.Should().BeTrue("the loop did not start beside a running round");
        (await wake).EndedByIos.Should().BeTrue();
        runtime.InForeground.Should().BeTrue();
        runtime.Scheduler.IsRunning.Should().BeTrue();
        (await Within(runtime.RunBackgroundAsync(work: true, CancellationToken.None), 10)).Skipped.Should().Contain("app is open");

        // and back to the background: the loop is stopped, then a wake may run
        await Within(runtime.StopForegroundAsync());
        runtime.InForeground.Should().BeFalse();
        runtime.Scheduler.IsRunning.Should().BeFalse();
        var again = await Within(runtime.RunBackgroundAsync(work: false, CancellationToken.None));
        again.Skipped.Should().BeNull();
        again.Accounts.Should().ContainSingle();
    }

    // ---- F2: nothing of one account in another's log ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ATwoAccountWake_WhereOneFails_LeavesNeitherAccountsLogWithTheOthersNameResultNodeIdOrCanary()
    {
        await using var runtime = await _rig.StartedAsync();
        var anna = await _rig.PairedAsync(runtime, "Anna", "a.test");
        var ben = await _rig.PairedAsync(runtime, "Ben", "b.test");
        _rig.Respond = (request, ct) => request.RequestUri!.Host == "b.test"
            ? throw new HttpRequestException("Request to " + Canary + " was redirected (302) to " + Canary)
            : Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        var nodeOfAnna = runtime.Accounts.For(anna).GetStatus().NodeId!.Value.ToString("D");
        var nodeOfBen = runtime.Accounts.For(ben).GetStatus().NodeId!.Value.ToString("D");

        var outcome = await Within(runtime.RunBackgroundAsync(work: false, CancellationToken.None));
        IosBackgroundRecorder.Record(runtime, "app refresh", outcome);

        outcome.Accounts.Should().HaveCount(2);
        var annaLog = IosFixRig.LogOf(runtime, anna);
        var benLog = IosFixRig.LogOf(runtime, ben);
        var device = IosFixRig.DeviceLogOf(runtime);
        benLog.Should().Contain("Sync failed", "the control: the failing account's own log says its own failure");
        foreach (var (log, owner, others) in new[] { (annaLog, "Anna", new[] { "Ben", nodeOfBen, "b.test" }), (benLog, "Ben", new[] { "Anna", nodeOfAnna, "a.test" }) })
        {
            foreach (var foreign in others) log.Should().NotContain(foreign, owner + "'s log holds nothing of another account");
            log.Should().NotContain("CANARY").And.NotContain("canary-host");
        }
        device.Should().NotBeNullOrWhiteSpace();
        device.Should().NotContain("Anna").And.NotContain("Ben").And.NotContain("a.test").And.NotContain("b.test").And.NotContain(nodeOfAnna).And.NotContain(nodeOfBen)
            .And.NotContain("CANARY", "the device's own log is account-free");
        // each account's "last background round" line is its own
        var stateOfAnna = runtime.Accounts.ServicesFor(anna).GetRequiredService<IBlindStateStore>().Get(IosLastRound.StateKey);
        var stateOfBen = runtime.Accounts.ServicesFor(ben).GetRequiredService<IBlindStateStore>().Get(IosLastRound.StateKey);
        stateOfAnna.Should().NotBeNull().And.NotContain("Ben").And.NotContain("CANARY");
        stateOfBen.Should().NotBeNull().And.NotContain("Anna").And.NotContain("CANARY");
    }

    [Fact]
    public async Task AFailedAndAStoppedRound_GoToTheirOwnAccountsLogOnly_AndTheWakeToTheDeviceLogAsNumbers()
    {
        await using var runtime = await _rig.StartedAsync();
        var anna = await _rig.PairedAsync(runtime, "Anna", "a.test");
        var ben = await _rig.PairedAsync(runtime, "Ben", "b.test");
        var outcome = new IosRoundOutcome(
        [
            new IosAccountRound(anna, "Anna", "Stopped to give the next account its time; it continues where it stopped.", Ran: true, IosRoundKind.Stopped),
            new IosAccountRound(ben, "Ben", "Failed: no answer: connection refused (nothing listens at that address)", Ran: true, IosRoundKind.Failed),
        ], Note: null, EndedByIos: false, NotReached: 3);

        IosBackgroundRecorder.Record(runtime, "app refresh", outcome);

        var annaLog = IosFixRig.LogOf(runtime, anna);
        var benLog = IosFixRig.LogOf(runtime, ben);
        annaLog.Should().Contain("Stopped to give the next account").And.NotContain("Ben").And.NotContain("Failed: no answer");
        benLog.Should().Contain("Failed: no answer").And.NotContain("Anna").And.NotContain("Stopped to give");
        IosFixRig.DeviceLogOf(runtime).Should().Contain("2 accounts had a turn: 2 asked, 0 held, 1 failed.").And.Contain("3 more wait for the next wake")
            .And.NotContain("Anna").And.NotContain("Ben").And.NotContain("no answer");
    }

    [Fact]
    public async Task TheOutcomeOfAnAccountThatIsBeingWiped_IsNotWrittenBack_AndOneThatIsNotIs()
    {
        await using var runtime = await _rig.StartedAsync();
        var anna = await _rig.PairedAsync(runtime, "Anna", "a.test");
        var ben = await _rig.PairedAsync(runtime, "Ben", "b.test");
        var outcome = new IosRoundOutcome(
        [
            new IosAccountRound(anna, "Anna", "Failed: no answer: connection refused (nothing listens at that address)", Ran: true, IosRoundKind.Failed),
            new IosAccountRound(ben, "Ben", "Failed: no answer: connection refused (nothing listens at that address)", Ran: true, IosRoundKind.Failed),
        ], Note: null, EndedByIos: false, NotReached: 0);
        // "Disconnect and wipe" of Anna's account has begun: it refuses new work and has (or is about to) delete her state and log. A line the wake writes now would bring them back.
        runtime.Accounts.ServicesFor(anna).GetRequiredService<BlindActivity>().Close();
        var logBefore = IosFixRig.LogOf(runtime, anna);

        IosBackgroundRecorder.Record(runtime, "app refresh", outcome);

        runtime.Accounts.ServicesFor(anna).GetRequiredService<IBlindStateStore>().Get(IosLastRound.StateKey).Should().BeNull("nothing is written for an account that is being wiped");
        IosFixRig.LogOf(runtime, anna).Should().Be(logBefore);
        runtime.Accounts.ServicesFor(ben).GetRequiredService<IBlindStateStore>().Get(IosLastRound.StateKey).Should().NotBeNull("the other account's outcome is written as before");
        IosFixRig.LogOf(runtime, ben).Should().Contain("Failed: no answer");
    }

    [Fact]
    public async Task AFailureOfTheWakeItself_IsWrittenToTheDeviceLogByItsKind_NotToAnAccount()
    {
        await using var runtime = await _rig.StartedAsync();
        var anna = await _rig.PairedAsync(runtime, "Anna", "a.test");
        var ben = await _rig.PairedAsync(runtime, "Ben", "b.test");

        IosBackgroundRecorder.RecordFailure(runtime, "processing window",
            new InvalidOperationException("Anna and Ben failed at " + Canary, new HttpRequestException(Canary)));

        var device = IosFixRig.DeviceLogOf(runtime);
        device.Should().Contain("the wake failed").And.NotContain("Anna").And.NotContain("Ben").And.NotContain("CANARY").And.NotContain("canary-host");
        IosFixRig.LogOf(runtime, anna).Should().NotContain("wake failed").And.NotContain("CANARY");
        IosFixRig.LogOf(runtime, ben).Should().NotContain("wake failed").And.NotContain("CANARY");
    }

    // ---- F3: failures are told by their kind --------------------------------------------------------------------------------------------

    [Fact]
    public void TheHeadsFailureText_NeverCarriesACanaryOfARemoteOrWrappedException()
    {
        foreach (var ex in new Exception[]
                 {
                     new HttpRequestException("Request to " + Canary + " was redirected (302) to " + Canary + "; redirects are not followed."),
                     new HttpRequestException("Cleartext HTTP is forbidden for blind node communications: " + Canary),
                     new Exception("wrapper " + Canary, new HttpRequestException(Canary)),
                     new InvalidOperationException("outer " + Canary, new AggregateException(new HttpRequestException(Canary))),
                     new IOException("disk gone at /private/var/mobile/" + Canary),
                     new NotSupportedException(Canary),
                 })
        {
            var text = IosFailureText.Say(ex);
            text.Should().NotBeNullOrWhiteSpace();
            text.Should().NotContain("CANARY").And.NotContain("canary-host").And.NotContain("/private/var", ex.GetType().Name);
        }
        IosFailureText.Say(new IosKeychainException("The Keychain did not answer for identity-seed-v1 (OSStatus -25308).", -25308)).Should().Be("the Keychain did not answer (OSStatus -25308)");
    }

    [Fact]
    public void NoPageOrServiceOfTheHead_ReadsTheMessageOfAnException()
    {
        var head = Path.Combine(VaultBoundary.RepoRoot(), "mobile", "BeeMemoryBank.BlindIos");
        var sources = Directory.GetFiles(head, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToList();
        sources.Should().NotBeEmpty();
        var message = new Regex(@"\b(ex|exception|error|inner|failure)\.Message\b", RegexOptions.Compiled);

        var sites = sources
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => message.IsMatch(l.Text) && !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.Text.TrimStart().StartsWith("///", StringComparison.Ordinal))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        sites.Should().BeEmpty("a caught exception is told by BlindFailureText/IosFailureText, never by its message");
    }

    // ---- F4: the account that took the code ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoPairedAccountsThatRePairAtOnce_AndACodeForTheSecond_LandOnTheSecond()
    {
        await using var runtime = await _rig.StartedAsync();
        var anna = await _rig.PairedAsync(runtime, "Anna", "a1.test");
        var ben = await _rig.PairedAsync(runtime, "Ben", "b1.test");
        runtime.Accounts.For(anna).StartRePair();
        runtime.Accounts.For(ben).StartRePair();
        var waiting = runtime.Accounts.GetSnapshot().Accounts.Where(a => a.Status is { AwaitingAnswer: true }).Select(a => a.Entry.Id).ToList();
        waiting.Should().BeEquivalentTo([anna, ben]);
        // the old guess of the page - the first waiting account that is paired - is Anna, whoever the code was made for
        waiting.First(id => runtime.Accounts.For(id).GetStatus().IsPaired).Should().Be(anna);

        var accepted = runtime.Accounts.AcceptCallCodeWithAccount(IosFixRig.AnswerFor(runtime.Accounts.For(ben).PairingCode()!, "b2.test"));

        accepted.Ok.Should().BeTrue();
        accepted.AccountId.Should().Be(ben);
        runtime.Accounts.For(ben).GetStatus().Endpoint.Should().Be("https://b2.test:5610");
        runtime.Accounts.For(anna).GetStatus().Endpoint.Should().Be("https://a1.test:5610");
        runtime.Accounts.For(anna).GetStatus().AwaitingAnswer.Should().BeTrue("Anna still waits for her own answer");
    }

    // ---- F5 / F6: the pages bind the shared records ---------------------------------------------------------------------------------------

    /// <summary>A source file of the head without its comment lines (what the code says, not what its notes say).</summary>
    private static string Code(params string[] parts) =>
        string.Join(Environment.NewLine, File.ReadAllLines(Path.Combine([VaultBoundary.RepoRoot(), "mobile", "BeeMemoryBank.BlindIos", .. parts])).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Page(string name) => Code("Pages", name);

    [Fact]
    public void ThePages_BindTheSharedRecords_AndBuildNoLimitsLastContactProblemOrInvitationSentenceOfTheirOwn()
    {
        var account = Page("BlindHomePage.xaml.cs");
        account.Should().Contain("BlindLimitsForm.Explanation").And.Contain("view.Sync").And.Contain("view.Problem");
        account.Should().NotContain("A sync round runs every").And.NotContain("IosHomeLines.LastContact").And.NotContain("IosHomeLines.LastProblem")
            .And.NotContain("Last contact with the node");

        var accounts = Page("AccountsPage.xaml.cs");
        accounts.Should().Contain("BlindDeviceLimitsForm.Explanation").And.NotContain("Leave a box empty");

        var add = Page("AddAccountPage.xaml.cs");
        add.Should().Contain("preview.Description", "the shared sentence is shown as it is");
        add.Should().NotContain("not an encrypted one", "what the invitation is, is said once, by the shared record");
        add.Should().NotContain("DefaultPairingName", "the accounts name themselves");

        var source = Code("Services", "IosAccountsLines.cs");
        source.Should().NotContain("LimitsHint").And.NotContain("DefaultPairingName");
    }

    // ---- F7: the daily reminder repeats ---------------------------------------------------------------------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private const string AnnaId = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";

    private static BlindAccountSnapshot SilentAnna(DateTimeOffset lastSync) =>
        new(new BlindAccountEntry(AnnaId, BlindAccountLayouts.Dir, "Anna", InviteId: "i1"),
            new BlindAppStatus(Guid.NewGuid(), "iPhone", true, true, lastSync, null, BlindBackupSchedule.Off, false, null, null, [], [], Endpoint: "https://a.test:5610"));

    [Fact]
    public void AnOverdueSilence_IsSaidAgainEveryDay_OnceTheFirstReminderHasFired()
    {
        var planner = new BlindSilencePlanner();
        var snapshot = new BlindAccountsSnapshot([SilentAnna(Now.AddDays(-5))], BlindDeviceLimits.None, null, true, 8);
        var id = BlindSilenceAlarm.RequestId + "." + AnnaId;

        var first = planner.Update(snapshot, Now);
        first.Set.Should().ContainSingle().Which.Notice.FireAt.Should().Be(Now + BlindSilenceAlarm.RemindAfter);
        planner.Update(snapshot, Now.AddSeconds(5)).IsEmpty.Should().BeTrue("while the request is pending nothing is moved");
        planner.NoteFired([id], Now.AddHours(2)).Should().BeEmpty("a request whose time has not come and that is pending is left alone");
        planner.NoteFired([], Now.AddHours(2)).Should().BeEmpty("a request that is gone before its time was removed by someone, not shown");

        // iOS shows the request at its time and removes it from the pending ones
        var later = Now + BlindSilenceAlarm.RemindAfter + TimeSpan.FromMinutes(1);
        planner.NoteFired([], later).Should().Equal(id);
        var next = planner.Update(snapshot, later);

        next.Set.Should().ContainSingle("the fired reminder invalidated the planned key, so the next update schedules the next one");
        next.Set[0].Notice.FireAt.Should().Be(later + BlindSilenceAlarm.RemindAfter);
        planner.Update(snapshot, later.AddSeconds(5)).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void AFiredRequestOfAnAccountThatIsGone_DropsNothingElse()
    {
        var planner = new BlindSilencePlanner();
        planner.Update(new BlindAccountsSnapshot([SilentAnna(Now.AddDays(-1))], BlindDeviceLimits.None, null, true, 8), Now);

        planner.NoteFired(["something-else"], Now.AddDays(10)).Should().Equal(BlindSilenceAlarm.RequestId + "." + AnnaId);
        planner.NoteFired([], Now.AddDays(11)).Should().BeEmpty("it was dropped once");
    }

    // ---- F8: no wake for an account that is paused for good ----------------------------------------------------------------------------------

    private static BlindAccountSnapshot Card(string phase = BlindAccountPhases.Active, bool pausedByUser = false, BlindPause? pause = null) =>
        new(new BlindAccountEntry(AnnaId, BlindAccountLayouts.Dir, "Anna", phase, PausedByUser: pausedByUser), null, Pause: pause);

    [Fact]
    public void AnAccountNeedsAWake_UnlessItIsHeldForGood()
    {
        IosBackgroundPlan.NeedsWake(Card()).Should().BeTrue();
        IosBackgroundPlan.NeedsWake(Card(BlindAccountPhases.AwaitingConfirmation)).Should().BeTrue("the owner's decision is learned at a wake");
        IosBackgroundPlan.NeedsWake(Card(BlindAccountPhases.Pending)).Should().BeTrue();
        IosBackgroundPlan.NeedsWake(Card(BlindAccountPhases.Refused)).Should().BeFalse();
        IosBackgroundPlan.NeedsWake(Card(pausedByUser: true, pause: new BlindPause(BlindPauseReasons.PausedByYou))).Should().BeFalse("only the person lifts it, and the app is open then");
        IosBackgroundPlan.NeedsWake(Card(pause: new BlindPause(BlindPauseReasons.Disk, Limit: 1L << 30))).Should().BeFalse("a storage limit has no end of its own");
        IosBackgroundPlan.NeedsWake(Card(pause: new BlindPause(BlindPauseReasons.Traffic, Limit: 0))).Should().BeFalse("a limit of 0 is a pause until it is changed");
        IosBackgroundPlan.NeedsWake(Card(pause: new BlindPause(BlindPauseReasons.Traffic, Now.AddHours(5), Limit: 200L << 20))).Should().BeTrue("a daily limit ends at midnight; the wake after it resumes the account");
        IosBackgroundPlan.NeedsWake(Card(pause: new BlindPause(BlindPauseReasons.MeteredNetwork))).Should().BeTrue("Wi-Fi may come");
        IosBackgroundPlan.NeedsWake(Card(pause: new BlindPause(BlindPauseReasons.PowerSaving))).Should().BeTrue();
        IosBackgroundPlan.NeedsWake(Card(pause: new BlindPause(BlindPauseReasons.UsageUnavailable))).Should().BeTrue("a fault can be repaired and ends by itself at the next look");
    }

    [Fact]
    public async Task APhoneWhoseOnlyAccountIsPausedByItsPerson_AsksIosForNothing_AndAsksAgainWhenItIsResumed()
    {
        await using var runtime = await _rig.StartedAsync();
        var id = await _rig.PairedAsync(runtime, "Anna", "a.test");

        runtime.RefreshBackgroundRequests();
        runtime.BackgroundRequestsWanted.Should().BeTrue();
        _rig.Background.Submitted.Should().Be(1);

        runtime.Accounts.SetPaused(id, true).Should().BeNull();
        runtime.RefreshBackgroundRequests();
        runtime.BackgroundRequestsWanted.Should().BeFalse("every account is paused for good: a wake would find nothing to do");
        _rig.Background.Cancelled.Should().Be(1);

        runtime.Accounts.SetPaused(id, false).Should().BeNull();
        runtime.RefreshBackgroundRequests();
        runtime.BackgroundRequestsWanted.Should().BeTrue();
        _rig.Background.Submitted.Should().Be(2);
    }
}
