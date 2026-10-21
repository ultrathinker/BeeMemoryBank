using System.Text.RegularExpressions;
using BeeMemoryBank.Boundary;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// The Android head only compiles for Android, so (as in <see cref="BlindWorkPlanTests"/>) these tests read its real source: a regression of
/// the lifecycle and screen-safety fixes turns them red without a phone. The behaviour behind each rule is tested on the shared code
/// (<c>BlindSingleRunTests</c>, <c>BlindActivityTests</c>, <c>BlindPhoneResetTests</c>, <c>BlindHomeViewTests</c> in the AppCore tests).
/// </summary>
public sealed class BlindAndroidLifecycleSourceTests
{
    private static readonly string AppFolder = Path.Combine(VaultBoundary.RepoRoot(), "mobile", "BeeMemoryBank.BlindMobile");

    private static string Source(params string[] relative) => File.ReadAllText(Path.Combine([AppFolder, .. relative])).ReplaceLineEndings("\n");

    /// <summary>The text of one class of BlindWork.cs: from its declaration to the next top-level declaration.</summary>
    private static string ClassOf(string name)
    {
        var text = Source("Platforms", "Android", "BlindWork.cs");
        var start = text.IndexOf($"class {name}", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"{name} exists");
        var next = Regex.Match(text[(start + 1)..], @"\n(public|internal) (static |sealed )?class ");
        return next.Success ? text[start..(start + 1 + next.Index)] : text[start..];
    }

    // ---- F-10: repeated "Back up now" ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheBackupService_HasOneTaskAndOneCancellationHandle_AStartWhileItRunsReplacesNothing()
    {
        // The decisions moved out of the Android file into the portable core, one instance of it per service over ONE process-wide state (BlindManualBackupTests run them against fakes).
        var service = ClassOf("BlindBackupService");
        var core = Source("Services", "Blind", "BlindManualBackup.cs");

        service.Should().Contain("_core!.OnStart(", "every start is the core's decision");
        service.Should().NotContain("new CancellationTokenSource").And.NotContain("Task.Run(", "the service file starts no task");
        Regex.Matches(service, @"StopSelf\(").Count.Should().Be(1, "the one stop is the core's StopAfter: for the latest start id, when its own task ends");
        Regex.Matches(core, @"new CancellationTokenSource\(").Count.Should().Be(1, "one handle per run, made when the run is accepted");
        Regex.Matches(core, @"Task\.Run\(").Count.Should().Be(1, "one task, started in one place (StartRun) and only for a Started delivery");
        core.Should().Contain("_cts?.Cancel()", "OnDestroy reaches the one running task");
    }

    [Fact]
    public void BackUpNow_IsDisabledWhileTheBackupRuns_OnThePage()
    {
        var page = Source("Pages", "BlindHomePage.xaml.cs");

        // The tap turns the button off at once: through the one busy helper (BMB-311), which disables the target before anything starts and keeps the
        // pop-up up until the job's state says it has ended.
        var handler = page[page.IndexOf("async void OnBackupNowClicked", StringComparison.Ordinal)..];
        handler = handler[..handler.IndexOf("\n    }\n", StringComparison.Ordinal)];
        handler.Should().Contain("BusyAction.RunAsync(sender as Button");
        handler.Should().Contain("BlindJobWait.Ask(BlindJobWait.Backup", "the wait is judged from the state of the phone, not from a clock");
        page.Should().Contain("_activity.IsActive(BlindActivity.BackupService)");
        page.Should().Contain("BackupNowButton.Text = view.BackupNowText");
        page.Should().Contain("BackupNowButton.IsEnabled = view.CanBackupNow", "and the button stays off while the job runs, whoever started it");
    }

    // ---- F-03: the wipe stops and waits for every worker -----------------------------------------------------------------------------

    [Fact]
    public void TheSyncWorker_HasACancellationSourceFiredFromOnStopped_AndPassesItOn()
    {
        var worker = ClassOf("BlindSyncWorker");

        worker.Should().Contain("CancellationTokenSource _stop");
        worker.Should().Contain("override void OnStopped()");
        worker.Should().Contain("_stop.Cancel()");
        worker.Should().NotContain("CancellationToken.None", "the pass must be stoppable");
        worker.Should().Contain("var token = _stop.Token;");
        worker.Should().Contain("device.SyncScheduler.TickAsync(token)", "the device's scheduler runs the rounds, one account at a time, under this token");
    }

    [Fact]
    public void EveryLongRunningEntryPoint_RunsItsJobThroughTheAccountsController_WhichRegistersItInTheAccountsActivity()
    {
        // The worker no longer registers a job itself: the controller of the account does (RequestSyncAsync -> BlindActivity.Sync; BlindHeavyWork.RunAsync -> BlindActivity.Heavy), which is what
        // a removal of that account cancels and waits for (BlindActivityTests, BlindAccountRemovalTests in the AppCore tests). The head's entry points must go through them.
        ClassOf("BlindHeavyWorker").Should().Contain("device.HeavyScheduler.TickAsync(token)").And.Contain("_stop.Cancel()");
        ClassOf("BlindBackupService").Should().Contain("device.Accounts.For(accountId).RunHeavyAsync(forceBackup: true, token)");
        Source("Platforms", "Android", "BlindWork.cs").Should().NotContain("CancellationToken.None");
        Source("Platforms", "Android", "BlindWork.cs").Should().NotContain("BlindPhoneState", "a worker never reads an account's state itself");
    }

    [Fact]
    public void TheBackupService_NamesTheAccountItWorksFor_AndRefusesAnythingThatIsNotAnAccountId()
    {
        var service = ClassOf("BlindBackupService");

        service.Should().Contain("intent.PutExtra(ExtraAccount, accountId)");
        service.Should().Contain("BlindAccountEntry.IsValidId(accountId)", "the extra comes from an intent");
        service.Should().Contain("RunningFor", "the page does not start the backup of another account while one runs");
    }

    [Fact]
    public void TheRemovalOnThePage_GoesThroughTheHostsAsyncRemoval_OffTheUiThread_AfterTheSharedConfirmation()
    {
        var page = Source("Pages", "BlindHomePage.xaml.cs");

        page.Should().Contain("BlindRemoveConfirmation.Build(account)", "the words of the confirmation are the shared ones");
        page.Should().Contain("Task.Run(() => _device.Accounts.RemoveAsync(_accountId))");
        page.Should().NotContain("DisconnectAndWipeAsync", "one account of several is removed, not wiped and restarted");
        page.Should().NotContain("BlindPhoneReset.WipeAndRestart(", "the synchronous wipe would block the UI thread while it waits for the work");
        page.Should().Contain("catch (AggregateException)", "an incomplete removal is noted, and reported with 'press it again'");
        page.Should().Contain("Press Remove this account again to finish.");
    }

    [Fact]
    public void TheLifecycleOfAnAccount_StopsOnlyThatAccountsBackup_NeverTheDevicesJobs()
    {
        var lifecycle = Source("Platforms", "Android", "AndroidDeviceConditions.cs");

        lifecycle.Should().Contain("class AndroidAccountLifecycle : IBlindAccountLifecycle");
        lifecycle.Should().Contain("StopBackupService(BlindAccountEntry account) => BlindBackupService.StopFor(Platform.AppContext, account.Id);");
        lifecycle.Should().NotContain("StopBackgroundWork", "the periodic jobs are the device's: one set for every account");
        File.Exists(Path.Combine(AppFolder, "Platforms", "Android", "AndroidBlindLifecycle.cs")).Should().BeFalse("the single-account lifecycle (wipe and restart the process) is gone");
    }

    // ---- F-04: the page shows the controller's status and cannot throw ------------------------------------------------------------------

    [Fact]
    public void ThePage_IsBoundToTheSharedController_AndNeverReadsAKeyItself()
    {
        var page = Source("Pages", "BlindHomePage.xaml.cs");

        page.Should().Contain("IBlindAppController");
        page.Should().Contain("BlindHomeView.Build(");
        foreach (var forbidden in new[] { "BlindMobilePairing", "PhoneCodeText", "BackupKeyLost", "BlindPhoneState", "IBlindSecretStore", "IBlindNodeKeys", "LoadBackupKey" })
            page.Should().NotContain(forbidden, "the Keystore-reading calls throw on an unavailable or invalidated Keystore; only the controller guards them");
    }

    [Fact]
    public void EveryAsyncVoidHandlerOfThePage_CatchesEverything()
    {
        var page = Source("Pages", "BlindHomePage.xaml.cs");
        var handlers = Regex.Matches(page, @"async void (\w+)\(").Select(m => m.Index).ToList();
        handlers.Should().HaveCountGreaterThan(5);

        foreach (var start in handlers)
        {
            var end = page.IndexOf("\n    }\n", start, StringComparison.Ordinal);
            var body = page[start..end];
            var name = Regex.Match(body, @"async void (\w+)").Groups[1].Value;
            // The busy helper catches everything its work throws and says it (BMB-311); a handler that does not use it catches for itself.
            (body.Contains("catch (Exception") || body.Contains("BusyAction.RunAsync(")).Should().BeTrue($"{name} is an async void: an exception in it would end the app");
        }
    }

    [Fact]
    public void ThePagesRefresh_TheTimerAndTheChangedEventCallIt_NeverThrows()
    {
        var page = Source("Pages", "BlindHomePage.xaml.cs");
        var start = page.IndexOf("private void Refresh()", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1);
        var body = page[start..page.IndexOf("\n    }\n", start, StringComparison.Ordinal)];

        body.Should().Contain("catch (Exception)", "the failure is not kept: the page says a fixed sentence (BlindPageSentences), never the exception's text");
        body.Should().Contain("ShowProblem()");
        page.Should().Contain("_timer.Tick += (_, _) => Refresh();");
        page.Should().Contain("_app.Changed += OnAppChanged");
        page.Should().Contain("_app.Changed -= OnAppChanged", "the page does not stay subscribed after it disappears");
    }

    [Fact]
    public void TheKeystoreBlobs_AreSplitByTheValidatingLayout_NotSlicedByHand()
    {
        var keys = Source("Platforms", "Android", "AndroidKeystoreKeyWrap.cs");

        keys.Should().Contain("BlindKeystoreBlobLayout.Split(blob)");
        keys.Should().Contain("BlindKeystoreBlobLayout.Join(");
        keys.Should().NotContain("blob[..IvLength]");
        keys.Should().NotContain("blob.Length - IvLength");
        Source("Services", "Blind", "BlindKeystoreSecrets.cs").Should().NotContain("blob[..", "the store hands the whole blob to the wrap, which splits it with the layout");
    }

    [Fact]
    public void ASyncThatFails_IsLoggedWithItsInnerCauses_LikeTheOtherHostsDo()
    {
        // The HTTP stack's own message ("An error occurred while sending the request.") hides what failed; BlindRunReport.Reason adds the
        // inner causes. What it produces is tested with the shared code (BlindRunReportReasonTests); this holds that the Android worker uses it.
        var worker = ClassOf("BlindSyncWorker");
        // An account's round is logged by its controller ("Sync failed: {BlindRunReport.Reason(ex)}", BlindAppControllerTests); what the worker itself can fail on is the start of the device.
        worker.Should().Contain("The sync pass failed: {BlindRunReport.Reason(ex)}");
        worker.Should().NotContain("{ex.Message}");
    }

    [Fact]
    public void TheAppCore_IsComposedWithThePhonesDeviceName_SoThePageNeedsNoIdentityCodeOfItsOwn()
    {
        Source("MauiProgram.cs").Should().Contain("displayNameFactory: () => DeviceInfo.Current.Name");
        // The account host starts each account (it makes its identity); the page only shows what it finds and never makes one.
        Source("Pages", "BlindHomePage.xaml.cs").Should().NotContain("InitializeAsync");
        Source("Pages", "BlindHomePage.xaml.cs").Should().Contain("_device.StartAsync()");
    }
}
