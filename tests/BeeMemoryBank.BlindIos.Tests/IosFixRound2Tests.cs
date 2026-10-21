using System.Text.RegularExpressions;
using BeeMemoryBank.BlindIos.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Boundary;

namespace BeeMemoryBank.BlindIos.Tests;

/// <summary>
/// Fix round 2 for the iPhone head (review revfixios): the real loop after a rapid sleep and resume, app-wide errors in the device log, the bookkeeping of the notification callbacks, and the
/// source guard that keeps an exception's message and the raw failure fields of the status away from every page.
/// </summary>
public sealed class IosFixRound2Tests : IDisposable
{
    private const string Canary = "https://canary-host.invalid/redirect?token=CANARY-TOKEN-9d2f";

    private readonly IosFixRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static async Task<T> Within<T>(Task<T> task, int seconds = 60) => await task.WaitAsync(TimeSpan.FromSeconds(seconds));

    private static Task Within(Task task, int seconds = 60) => task.WaitAsync(TimeSpan.FromSeconds(seconds));

    // ---- F1: the real loop through a rapid sleep and resume ------------------------------------------------------------------------------------

    [Fact]
    public async Task OnSleepThenARapidOnResume_LeavesTheRealLoopRunning_EvenWhenTheStopHasToWaitForAJobThatWillNotStop()
    {
        var options = new BlindDeviceSchedulerOptions { StopGrace = TimeSpan.FromMilliseconds(300), StopTimeout = TimeSpan.FromSeconds(20) };
        await using var runtime = await _rig.StartedAsync(options);
        await _rig.PairedAsync(runtime, "Anna", "a.test");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _rig.Respond = async (request, ct) =>
        {
            // the first request never looks at its cancellation: it is a job the loop has to let go of
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                await release.Task;
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
        };
        await Within(runtime.StartForegroundAsync());
        await Within(entered.Task);
        runtime.Scheduler.IsRunning.Should().BeTrue();

        // OnSleep, then OnResume a moment later - before the stop has ended
        var sleep = runtime.StopForegroundAsync();
        var resume = runtime.StartForegroundAsync();
        await Within(Task.WhenAll(sleep, resume));

        runtime.InForeground.Should().BeTrue();
        runtime.Scheduler.IsRunning.Should().BeTrue("the app is in front: the stale stop must not have stopped the loop of the newer start");
        release.SetResult();
    }

    [Fact]
    public async Task OnResumeThenARapidOnSleep_LeavesTheRealLoopStopped_AndAWakeMayRun()
    {
        await using var runtime = await _rig.StartedAsync();
        await _rig.PairedAsync(runtime, "Anna", "a.test");

        var resume = runtime.StartForegroundAsync();
        var sleep = runtime.StopForegroundAsync();
        await Within(Task.WhenAll(resume, sleep));

        runtime.InForeground.Should().BeFalse();
        runtime.Scheduler.IsRunning.Should().BeFalse("the app is in the background: the loop is stopped");
        (await Within(runtime.RunBackgroundAsync(work: false, CancellationToken.None))).Skipped.Should().BeNull();
    }

    [Fact]
    public async Task DisposingTheRuntime_InTheMiddleOfAResume_EndsWithNoLoopAndNoRound()
    {
        var runtime = await _rig.StartedAsync();
        await _rig.PairedAsync(runtime, "Anna", "a.test");
        var resume = runtime.StartForegroundAsync();

        await Within(runtime.DisposeAsync().AsTask());
        await Within(resume.ContinueWith(_ => { }));

        runtime.Scheduler.IsRunning.Should().BeFalse("nothing is left running after the runtime is disposed");
        (await Within(runtime.RunBackgroundAsync(work: false, CancellationToken.None))).Skipped.Should().Contain("closing");
    }

    // ---- F2: an app-wide error is the device's, not the first account's ------------------------------------------------------------------------

    [Fact]
    public async Task AnAppWideError_GoesToTheDeviceLog_ByItsKind_AndToNeitherAccountsLog()
    {
        await using var runtime = await _rig.StartedAsync();
        var anna = await _rig.PairedAsync(runtime, "Anna", "a.test");
        var ben = await _rig.PairedAsync(runtime, "Ben", "b.test");

        IosAppErrors.Report(runtime, "Opening a link failed", new InvalidOperationException("Anna's and Ben's link " + Canary, new HttpRequestException(Canary)));
        IosAppErrors.Report(runtime, "Going to the background", new IOException("/private/var/mobile/" + Canary));
        IosAppErrors.Report(runtime, "Starting the copy failed", new NotSupportedException(Canary));

        var device = IosFixRig.DeviceLogOf(runtime);
        device.Should().Contain("Opening a link failed").And.Contain("Going to the background").And.Contain("Starting the copy failed");
        device.Should().NotContain("CANARY").And.NotContain("canary-host").And.NotContain("/private/var").And.NotContain("Anna").And.NotContain("Ben");
        foreach (var account in new[] { anna, ben })
        {
            IosFixRig.LogOf(runtime, account).Should().NotContain("Opening a link failed").And.NotContain("Going to the background")
                .And.NotContain("Starting the copy failed").And.NotContain("CANARY", "an app-wide error is told in neither account's log");
        }
    }

    [Fact]
    public void TheAppClass_ChoosesNoAccountToTellAnError_AndHandsEveryLifecycleCallToTheRuntimeInOrder()
    {
        var app = Code("App.xaml.cs");
        app.Should().NotContain("ServicesFor(").And.NotContain("BlindPhoneLog").And.NotContain(".Add(\"app\"", "no account's log is chosen to hold an app-wide error");
        app.Should().Contain("IosAppErrors.Report(");
        // the stop is asked for on the caller's thread (so that it keeps its place behind OnResume's start), not from a task started on another thread
        var sleep = app[app.IndexOf("protected override void OnSleep", StringComparison.Ordinal)..];
        sleep = sleep[..sleep.IndexOf("TryHandleLink", StringComparison.Ordinal)];
        sleep.IndexOf("StopForegroundAsync()", StringComparison.Ordinal).Should().BeGreaterThan(0);
        sleep.IndexOf("StopForegroundAsync()", StringComparison.Ordinal).Should().BeLessThan(sleep.IndexOf("Task.Run", StringComparison.Ordinal),
            "StopForegroundAsync is called before any Task.Run: the request is made in the order OnSleep and OnResume come in");
    }

    // ---- notification callbacks: only the newest add of a request counts ---------------------------------------------------------------------------

    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private const string AnnaId = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";
    private static readonly string RequestId = BlindSilenceAlarm.RequestId + "." + AnnaId;

    private static BlindAccountSnapshot SilentAnna(DateTimeOffset lastSync) =>
        new(new BlindAccountEntry(AnnaId, BlindAccountLayouts.Dir, "Anna", InviteId: "i1"),
            new BlindAppStatus(Guid.NewGuid(), "iPhone", true, true, lastSync, null, BlindBackupSchedule.Off, false, null, null, [], [], Endpoint: "https://a.test:5610"));

    private static BlindAccountsSnapshot Snapshot(params BlindAccountSnapshot[] accounts) => new(accounts, BlindDeviceLimits.None, null, true, 8);

    [Fact]
    public void AFailedAdd_FollowedByAnUnchangedSnapshot_IsPlannedAgain()
    {
        var book = new SilenceRequestBook();
        var snapshot = Snapshot(SilentAnna(Now.AddDays(-1)));
        var first = book.Plan(snapshot, Now).Adds.Should().ContainSingle().Which;
        book.Plan(snapshot, Now.AddSeconds(5)).IsEmpty.Should().BeTrue("while the add is outstanding nothing is added twice");

        book.Answered(RequestId, first.Generation, success: false, Now).Should().BeTrue("the answer to the newest add counts");

        book.ShowsAt(RequestId).Should().BeNull();
        var retry = book.Plan(snapshot, Now.AddSeconds(10)).Adds.Should().ContainSingle("the failure withdrew the plan: an unchanged snapshot plans the request again").Which;
        retry.Generation.Should().BeGreaterThan(first.Generation);
    }

    [Fact]
    public void AnOlderSuccess_ArrivingLate_CannotOverwriteTheTimeOfANewerAdd()
    {
        var book = new SilenceRequestBook();
        var older = book.Plan(Snapshot(SilentAnna(Now.AddDays(-1))), Now).Adds.Single();
        var newer = book.Plan(Snapshot(SilentAnna(Now.AddHours(-1))), Now.AddMinutes(1)).Adds.Single();
        newer.Generation.Should().BeGreaterThan(older.Generation);

        book.Answered(RequestId, newer.Generation, success: true, Now.AddDays(3)).Should().BeTrue();
        book.Answered(RequestId, older.Generation, success: true, Now.AddDays(1)).Should().BeFalse("it answers an add that was replaced");

        book.ShowsAt(RequestId).Should().Be(Now.AddDays(3));
    }

    [Fact]
    public void AnOlderError_ArrivingLate_CannotEraseANewerSuccess_NorMakeTheNextUpdateAddAgain()
    {
        var book = new SilenceRequestBook();
        var older = book.Plan(Snapshot(SilentAnna(Now.AddDays(-1))), Now).Adds.Single();
        var snapshot = Snapshot(SilentAnna(Now.AddHours(-1)));
        var newer = book.Plan(snapshot, Now.AddMinutes(1)).Adds.Single();
        book.Answered(RequestId, newer.Generation, success: true, Now.AddDays(3));

        book.Answered(RequestId, older.Generation, success: false, Now).Should().BeFalse();

        book.ShowsAt(RequestId).Should().Be(Now.AddDays(3), "the record of the newer success stands");
        book.Plan(snapshot, Now.AddMinutes(2)).IsEmpty.Should().BeTrue("the older error did not invalidate the plan");
    }

    [Fact]
    public void ANewerError_ThenAnOlderSuccessArrivingLate_LeavesNothingPending_AndTheNextUpdateRetries()
    {
        var book = new SilenceRequestBook();
        var older = book.Plan(Snapshot(SilentAnna(Now.AddDays(-1))), Now).Adds.Single();
        var snapshot = Snapshot(SilentAnna(Now.AddHours(-1)));
        var newer = book.Plan(snapshot, Now.AddMinutes(1)).Adds.Single();

        book.Answered(RequestId, newer.Generation, success: false, Now).Should().BeTrue();
        book.Answered(RequestId, older.Generation, success: true, Now.AddDays(1)).Should().BeFalse("the older add was replaced by the one that failed");

        book.ShowsAt(RequestId).Should().BeNull("a request whose newest add failed is not known to be pending");
        book.Plan(snapshot, Now.AddMinutes(2)).Adds.Should().ContainSingle("the current failure invalidated the plan, so an unchanged snapshot retries");
    }

    [Fact]
    public void ARemovedRequest_ForgetsItsAnswers()
    {
        var book = new SilenceRequestBook();
        var add = book.Plan(Snapshot(SilentAnna(Now.AddDays(-1))), Now).Adds.Single();
        book.Answered(RequestId, add.Generation, success: true, Now.AddDays(1));

        var plan = book.Plan(Snapshot(), Now.AddMinutes(1));

        plan.Removes.Should().Equal(RequestId);
        book.ShowsAt(RequestId).Should().BeNull();
        book.Answered(RequestId, add.Generation, success: true, Now.AddDays(1)).Should().BeFalse("the account is gone: a late answer for it is nothing");
        book.ShowsAt(RequestId).Should().BeNull();
    }

    [Fact]
    public void AFiredRequest_ForgetsItsTime_AndTheNextUpdateAddsTheNextReminder()
    {
        var book = new SilenceRequestBook();
        var snapshot = Snapshot(SilentAnna(Now.AddDays(-5)));
        var add = book.Plan(snapshot, Now).Adds.Single();
        book.Answered(RequestId, add.Generation, success: true, Now + BlindSilenceAlarm.RemindAfter);

        var later = Now + BlindSilenceAlarm.RemindAfter + TimeSpan.FromMinutes(1);
        book.NoteFired([], later).Should().Equal(RequestId);

        book.ShowsAt(RequestId).Should().BeNull();
        book.Plan(snapshot, later).Adds.Should().ContainSingle().Which.Notice.FireAt.Should().Be(later + BlindSilenceAlarm.RemindAfter);
    }

    [Fact]
    public void WhenAlertsAreAllowed_AndNothingIsPending_EverythingIsPlannedAgain()
    {
        var book = new SilenceRequestBook();
        var snapshot = Snapshot(SilentAnna(Now.AddDays(-1)));
        var first = book.Plan(snapshot, Now).Adds.Single();
        book.Answered(RequestId, first.Generation, success: false, Now);
        book.Plan(snapshot, Now).Adds.Should().ContainSingle("the failure alone already planned it again");
        book.Plan(snapshot, Now).IsEmpty.Should().BeTrue("the add that was planned again is outstanding");

        book.AlertsAllowed();

        book.Plan(snapshot, Now.AddSeconds(5)).Adds.Should().ContainSingle("nothing is known to be pending: allowed alerts plan everything again");
    }

    [Fact]
    public void TheNotifier_HoldsNoPlanOrTimeOfItsOwn_TheReconcilerTakesEveryAnswerThroughTheBooksGeneration()
    {
        var head = Path.Combine(VaultBoundary.RepoRoot(), "mobile", "BeeMemoryBank.BlindIos");
        var notifier = File.ReadAllText(Path.Combine(head, "Platforms", "iOS", "IosSilenceNotifier.cs"));
        notifier.Should().NotContain("_next").And.NotContain("new BlindSilencePlanner", "the bookkeeping is the portable book's, which is tested");
        var reconciler = File.ReadAllText(Path.Combine(head, "Services", "SilenceReconciler.cs"));
        reconciler.Should().Contain("Book.Answered(add.RequestId, add.Generation", "an answer counts only for the newest add of its request");
    }

    // ---- the guard: no page renders an exception's message or a raw failure field -------------------------------------------------------------------

    private static string Head() => Path.Combine(VaultBoundary.RepoRoot(), "mobile", "BeeMemoryBank.BlindIos");

    private static string Code(params string[] parts) =>
        string.Join(Environment.NewLine, File.ReadAllLines(Path.Combine([Head(), .. parts])).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static IEnumerable<(string File, int Line, string Text)> CodeLines(string directory, SearchOption option = SearchOption.AllDirectories) =>
        Directory.GetFiles(Path.Combine(Head(), directory), "*.cs", option)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal));

    private static readonly Regex MessageAccess = new(@"\b(?<receiver>[A-Za-z_][A-Za-z0-9_]*)\.Message\b", RegexOptions.Compiled);
    private static readonly Regex RawFields = new(@"\b(LastFailure|StartError|KeyStoreUnavailable|RecentLog|LastError)\b", RegexOptions.Compiled);
    private static readonly Regex ExceptionText = new(@"\b(ex|exception|error|inner|failure)\.(ToString|StackTrace|Source)\b|\{(ex|exception|error|inner|failure)(:[^}]*)?\}|\bex\.InnerException\b", RegexOptions.Compiled);

    /// <summary>Why a line of a screen's code is refused, or nothing.</summary>
    private static IEnumerable<string> Offences(string text)
    {
        foreach (Match m in MessageAccess.Matches(text))
        {
            var receiver = m.Groups["receiver"].Value;
            // 'ask' is a confirmation record's dialog text; 'outcome' carries a fixed sentence only when the job did not report
            var allowed = receiver == "ask" || (receiver == "outcome" && text.Contains("BlindJobPhase.Unreported", StringComparison.Ordinal));
            if (!allowed) yield return $".Message of '{receiver}'";
        }
        if (RawFields.IsMatch(text)) yield return "a raw failure field of the status";
        if (ExceptionText.IsMatch(text)) yield return "the text of an exception";
    }

    /// <summary>
    /// What a screen of the head may show of a failure, by the names of today's shared records: <c>BlindHomeView.StartErrorText</c>, <c>Log</c>, <c>Problem</c> and the others of the view record,
    /// <c>BlindHomeView.StatusFailedText</c>, <see cref="IosFailureText.Say"/>, the fixed sentences of <c>BlindJobWait</c> (an "unreported" job) and the dialog text of a confirmation record. Everything else
    /// that carries the text of an exception or of the status's raw failure fields is refused. (Written against the names in this branch; the shared fix of the failure text changes what the view
    /// record's fields hold, not their names.)
    /// </summary>
    [Fact]
    public void NoPageOrScreenModel_RendersAnExceptionMessage_OrARawFailureFieldOfTheStatus()
    {
        // every file a person sees through: the pages, the application class, the iOS-only classes and the portable lines of the screens
        var screens = CodeLines("Pages").Concat(CodeLines("Platforms")).Concat(CodeLines(".", SearchOption.TopDirectoryOnly))
            .Concat(CodeLines("Services", SearchOption.TopDirectoryOnly).Where(l => l.File.StartsWith("Ios", StringComparison.Ordinal) && l.File.Contains("Lines", StringComparison.Ordinal)))
            .ToList();
        screens.Should().NotBeEmpty();

        var offences = screens.SelectMany(l => Offences(l.Text).Select(why => $"{l.File}:{l.Line}: {why}: {l.Text.Trim()}")).ToList();

        offences.Should().BeEmpty("a screen shows a failure only through the closed view-record fields or IosFailureText/BlindHomeView.StatusFailedText");
    }

    [Fact]
    public void TheGuard_ReallyRefuses_TheShapesItIsMeantToRefuse_AndLetsTheCleanOnesThrough()
    {
        // a guard that matches nothing is no guard: the same rule, run over lines that ARE the offences
        foreach (var offence in new[]
                 {
                     "StatusErrorLabel.Text = ex.Message;",
                     "FailureLabel.Text = status.LastFailure.Message;",
                     "Notice($\"The first load did not finish: {outcome.Message}\");",
                     "StartErrorLabel.Text = status.StartError;",
                     "LogLabel.Text = string.Join(\"\n\", status.RecentLog.Select(e => e.Message));",
                     "Notice($\"failed: {ex}\");",
                     "Notice(ex.ToString());",
                     "StatusErrorLabel.Text = error.Message;",
                 })
            Offences(offence).Should().NotBeEmpty("the guard must refuse: " + offence);

        foreach (var clean in new[]
                 {
                     "StartErrorLabel.Text = view.StartErrorText;",
                     "LogLabel.Text = view.Log;",
                     "FailureLabel.Text = view.Problem;",
                     "StatusErrorLabel.Text = BlindHomeView.StatusFailedText(ex);",
                     "if (!await DisplayAlertAsync(ask.Title, ask.Message, ask.ConfirmText, ask.CancelText)) return;",
                     "else if (outcome.Phase == BlindJobPhase.Unreported) Notice(outcome.Message ?? \"\");",
                     "IosAppErrors.Report(_runtime, \"Opening a link failed\", ex);",
                 })
            Offences(clean).Should().BeEmpty("the guard must let the clean shape through: " + clean);
    }
}
