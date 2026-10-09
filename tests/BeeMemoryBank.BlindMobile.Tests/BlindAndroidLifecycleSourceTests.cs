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
        var service = ClassOf("BlindBackupService");

        service.Should().Contain("BlindSingleRun", "one task, one handle, coalesced starts");
        service.Should().Contain("_run.TryStart(");
        service.Should().NotContain("new CancellationTokenSource", "a start must not replace the handle of the running task");
        service.Should().NotContain("Task.Run(", "an untracked task per start is the defect");
        service.Should().NotContain("StopSelf(startId)", "the service stops for the latest start id, when its own task ends");
        Regex.Matches(service, @"StopSelf\(").Count.Should().Be(1);
        service.Should().Contain("_run.Cancel()", "OnDestroy reaches the one running task");
    }

    [Fact]
    public void BackUpNow_IsDisabledWhileTheBackupRuns_OnThePage()
    {
        var page = Source("Pages", "BlindHomePage.xaml.cs");

        page.Should().Contain("BackupNowButton.IsEnabled = false", "the tap turns the button off at once");
        page.Should().Contain("_activity.IsActive(BlindActivity.BackupService)");
        page.Should().Contain("BackupNowButton.Text = view.BackupNowText");
    }

    // ---- F-03: the wipe stops and waits for every worker -----------------------------------------------------------------------------

    [Fact]
    public void TheSyncWorker_HasACancellationSourceFiredFromOnStopped_AndPassesItOn()
    {
        var worker = ClassOf("BlindSyncWorker");

        worker.Should().Contain("CancellationTokenSource _stop");
        worker.Should().Contain("override void OnStopped()");
        worker.Should().Contain("_stop.Cancel()");
        worker.Should().NotContain("CancellationToken.None", "the round must be stoppable");
        worker.Should().Contain("SyncOnceAsync(target, token)");
        worker.Should().Contain("TryBegin(BlindActivity.Sync, _stop.Token)", "the wipe waits for the round and refuses a new one");
    }

    [Fact]
    public void EveryLongRunningEntryPoint_IsRegisteredInTheActivity_SoTheWipeCanWaitForIt()
    {
        ClassOf("BlindHeavyWorker").Should().Contain("TryBegin(BlindActivity.Heavy, _stop.Token)");
        ClassOf("BlindHeavyWorker").Should().Contain("_stop.Cancel()");
        ClassOf("BlindBackupService").Should().Contain("TryBegin(BlindActivity.BackupService, token)");
        Source("Platforms", "Android", "BlindWork.cs").Should().NotContain("CancellationToken.None");
    }

    [Fact]
    public void TheWipeOnThePage_GoesThroughTheControllersAsyncWipe_OffTheUiThread()
    {
        var page = Source("Pages", "BlindHomePage.xaml.cs");

        page.Should().Contain("Task.Run(() => _app.DisconnectAndWipeAsync())");
        page.Should().NotContain("BlindPhoneReset.WipeAndRestart(", "the synchronous wipe would block the UI thread while it waits for the work");
        page.Should().Contain("catch (AggregateException ex)", "an incomplete wipe is reported, with 'press it again'");
    }

    [Fact]
    public void AnAbortedWipe_PutsTheWorkManagerJobsBack_AndTheSyncWorkerIsVisibleInTheActivity()
    {
        var lifecycle = Source("Platforms", "Android", "AndroidBlindLifecycle.cs");

        lifecycle.Should().Contain("public void ResumeBackgroundWork() => BlindWorkScheduler.Ensure(Platform.AppContext);");
        ClassOf("BlindSyncWorker").Should().Contain("BlindActivity.Sync", "the controller shows a registered sync round as \"Sync\"");
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
            body.Should().Contain("catch (Exception", $"{name} is an async void: an exception in it would end the app");
        }
    }

    [Fact]
    public void ThePagesRefresh_TheTimerAndTheChangedEventCallIt_NeverThrows()
    {
        var page = Source("Pages", "BlindHomePage.xaml.cs");
        var start = page.IndexOf("private void Refresh()", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1);
        var body = page[start..page.IndexOf("\n    }\n", start, StringComparison.Ordinal)];

        body.Should().Contain("catch (Exception ex)");
        body.Should().Contain("ShowProblem(ex)");
        page.Should().Contain("_timer.Tick += (_, _) => Refresh();");
        page.Should().Contain("_app.Changed += OnAppChanged");
        page.Should().Contain("_app.Changed -= OnAppChanged", "the page does not stay subscribed after it disappears");
    }

    [Fact]
    public void TheKeystoreBlobs_AreSplitByTheValidatingLayout_NotSlicedByHand()
    {
        var keys = Source("Platforms", "Android", "KeystoreBlindPhoneKeys.cs");

        keys.Should().Contain("BlindKeystoreBlobLayout.Split(blob)");
        keys.Should().Contain("BlindKeystoreBlobLayout.Join(");
        keys.Should().NotContain("blob[..IvLength]");
        keys.Should().NotContain("blob.Length - IvLength");
    }

    [Fact]
    public void ASyncThatFails_IsLoggedWithItsInnerCauses_LikeTheOtherHostsDo()
    {
        // The HTTP stack's own message ("An error occurred while sending the request.") hides what failed; BlindRunReport.Reason adds the
        // inner causes. What it produces is tested with the shared code (BlindRunReportReasonTests); this holds that the Android worker uses it.
        var worker = ClassOf("BlindSyncWorker");
        worker.Should().Contain("Sync failed: {BlindRunReport.Reason(ex)}");
        worker.Should().NotContain("Sync failed: {ex.Message}");
    }

    [Fact]
    public void TheAppCore_IsComposedWithThePhonesDeviceName_SoThePageNeedsNoIdentityCodeOfItsOwn()
    {
        Source("MauiProgram.cs").Should().Contain("DisplayNameFactory: () => DeviceInfo.Current.Name");
        Source("Pages", "BlindHomePage.xaml.cs").Should().Contain("_app.InitializeAsync()");
    }
}
