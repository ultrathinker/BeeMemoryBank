using System.Text.RegularExpressions;
using BeeMemoryBank.Boundary;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// Fix round 1 of the Android head, for the parts that only compile for Android and so are read as source (the behaviour they feed is tested in the portable classes, on fakes that answer failure).
/// </summary>
public sealed class BlindHeadFix1SourceTests
{
    private static readonly string AppFolder = Path.Combine(VaultBoundary.RepoRoot(), "mobile", "BeeMemoryBank.BlindMobile");

    private static string Source(params string[] relative) => File.ReadAllText(Path.Combine([AppFolder, .. relative])).ReplaceLineEndings("\n");

    private static string WorkSource() => Source("Platforms", "Android", "BlindWork.cs");

    /// <summary>The text of one class of BlindWork.cs: from its declaration to the next top-level declaration.</summary>
    private static string ClassOf(string name)
    {
        var text = WorkSource();
        var start = text.IndexOf($"class {name}", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"{name} exists");
        var next = Regex.Match(text[(start + 1)..], @"\n(public|internal) (static |sealed )?class ");
        return next.Success ? text[start..(start + 1 + next.Index)] : text[start..];
    }

    // ---- MAJOR-1, MAJOR-2: the platform answers are not dropped ---------------------------------------------------------------------

    [Fact]
    public void TheKeystoreWrap_ConfirmsAnEntryIsGoneAfterDeletingIt_AndSaysSoWhenItIsNot()
    {
        var keys = Source("Platforms", "Android", "AndroidKeystoreKeyWrap.cs");
        var delete = keys[keys.IndexOf("public void DeleteKey", StringComparison.Ordinal)..];
        delete = delete[..delete.IndexOf("\n    }\n", StringComparison.Ordinal)];

        delete.Should().Contain("ks.DeleteEntry(alias)");
        Regex.Matches(delete, @"ks\.ContainsAlias\(alias\)").Count.Should().Be(2, "once before (nothing to do is not a failure) and once after (absent is confirmed, not assumed)");
        delete.Should().Contain("throw new InvalidOperationException(", "an entry that is still there is a failure the host retries");
        delete.Should().NotContain("catch", "a platform failure must reach the caller, not be swallowed here");
    }

    [Fact]
    public void ThePreferencesFile_ReturnsTheResultOfEveryCommitAndOfTheDelete_NeverDropsIt()
    {
        var prefs = Source("Services", "Blind", "PreferencesBlindStore.cs");
        var file = prefs[prefs.IndexOf("class AndroidFile", StringComparison.Ordinal)..];
        file = file[..file.IndexOf("\n#endif", StringComparison.Ordinal)];

        Regex.Matches(file, @"editor\.Commit\(\)").Count.Should().Be(3, "put, remove and clear each commit");
        Regex.Matches(file, @"return editor\.Commit\(\);").Count.Should().Be(3, "the answer of commit() goes to the store, which takes false as a failure");
        file.Should().Contain("=> global::Android.App.Application.Context.DeleteSharedPreferences(name)", "its answer is returned, not dropped");
        file.Should().NotMatchRegex(@"(?m)^\s*editor\.Commit\(\);", "a bare Commit() statement ignores the answer");
        file.Should().NotContain("Apply(", "apply() writes in the background");
    }

    [Fact]
    public void ARemovalThatDidNotFinish_HasAnExplicitRetryOnTheCard_ThroughTheBusyHelper()
    {
        var page = Source("Pages", "BlindAccountsPage.xaml.cs");
        var handler = page[page.IndexOf("async void OnRetryRemovalClicked", StringComparison.Ordinal)..];
        handler = handler[..handler.IndexOf("\n    }\n", StringComparison.Ordinal)];

        handler.Should().Contain("BusyAction.RunAsync(button").And.Contain("_device.Accounts.RemoveAsync(id)", "the same removal, run again; the host joins one that is running");
        page.Should().Contain("RetryButton.IsVisible = !card.CanEdit", "shown while the account is being removed (a card of an account that is not being removed has none)");
        page.Should().Contain("controls.RetryButton.Clicked +=");
    }

    // ---- MINOR-1: the notification names the account that runs, and has a lock-screen version ---------------------------------------

    [Fact]
    public void TheHeavyWorker_PublishesItsForegroundNotificationWhenAnAccountsJobStarts_NotBeforeAnyAccountIsKnown()
    {
        var heavy = ClassOf("BlindHeavyWorker");
        var doWork = heavy[heavy.IndexOf("public override Result DoWork()", StringComparison.Ordinal)..heavy.IndexOf("private void ShowForeground", StringComparison.Ordinal)];

        doWork.Should().NotContain("SetForegroundAsync(", "nothing is published before an account's job has started");
        doWork.Should().NotContain("\"Blind copy\"", "no anonymous status");
        doWork.Should().Contain("work.Started += OnStarted").And.Contain("work.Started -= started", "the account's own start is the moment, and the hook is removed with the pass");
        doWork.Should().Contain("ShowForeground(device, notifications, name)", "the notification names that account");
        var show = heavy[heavy.IndexOf("private void ShowForeground", StringComparison.Ordinal)..];
        show.Should().Contain("SetForegroundAsync(notifications.ForegroundInfo(account,");
        show.Should().Contain("catch (Exception ex)").And.NotContain("ex.Message", "a system that does not allow it costs the notification, and the log line has no platform text");
    }

    [Fact]
    public void TheBackupService_NamesTheAccountItsReservedFor_BeforeItPublishesAnything_AndUpdatesItAsSoonAsTheHostKnowsIt()
    {
        var service = ClassOf("BlindBackupService");

        service.Should().Contain("StartForeground(BlindNotifications.Id, _notifications!.Build(HolderName(accountId), \"Backup\", 0), ForegroundService.TypeDataSync)",
            "the foreground notification names the account the core passes (the holder of the phone), or is the neutral status when the host cannot say yet");
        service.Should().NotContain("notifications.Build(\"Backup\"", "the old anonymous start");
        service.Should().Contain("_notifications!.Show(name, \"Backup\", 0)", "updated at once when the account is looked up");
    }

    [Fact]
    public void TheNotification_IsPrivate_WithAPublicVersion_AndItsWordsAreTheSharedOnesOnly()
    {
        var notifications = WorkSource();
        notifications = notifications[notifications.IndexOf("class BlindNotifications", StringComparison.Ordinal)..];

        notifications.Should().Contain("builder.SetVisibility(NotificationCompat.VisibilityPrivate)", "a source's name does not belong on a lock screen");
        notifications.Should().Contain("builder.SetPublicVersion(publicVersion.Build())");
        notifications.Should().Contain("BlindNotificationText.Public").And.Contain("BlindNotificationText.Body(account, title, progress)");
        notifications.Should().NotContain("{progress:P0}", "the sentence is built once, in BlindNotificationText");
        var publicBuilder = notifications[notifications.IndexOf("var publicVersion", StringComparison.Ordinal)..notifications.IndexOf("var builder", StringComparison.Ordinal)];
        publicBuilder.Should().NotContain("account").And.NotContain("title").And.NotContain("progress", "the public version holds no name, no job and no figure");
    }

    // ---- MINOR-5: the account is reserved by the call that starts the service ------------------------------------------------------

    [Fact]
    public void TheServiceFile_IsThin_EveryDecisionIsInThePortableMachine_AndTheStartReservesBeforeAskingTheSystem()
    {
        var service = ClassOf("BlindBackupService");
        var start = service[service.IndexOf("public static bool Start(", StringComparison.Ordinal)..service.IndexOf("public static void StopFor", StringComparison.Ordinal)];

        start.IndexOf("Machine.Reserve(", StringComparison.Ordinal).Should().BeGreaterThan(-1)
            .And.BeLessThan(start.IndexOf("StartForegroundService(", StringComparison.Ordinal), "reserved first, synchronously, then the system is asked");
        start.Should().Contain("Machine.Withdraw(accountId, refusal.Generation)", "a start the system refused takes back its own request");
        service.Should().Contain("private static readonly BlindManualBackup Machine = new();", "ONE state for the whole process, not one per service instance");
        service.Should().Contain("_core = new BlindManualBackupService(Machine, this)").And.Contain("_core!.OnStart(").And.Contain("_core?.OnDestroy()");
        // none of the decisions is made here
        foreach (var forbidden in new[] { "BlindSingleRun", "BlindBackupStart", "new CancellationTokenSource", "Task.Run(", "_destroyed", "_run.", "Reservation.", ".Claim(", ".Deliver(", ".End(", "MarkRunning" })
            service.Should().NotContain(forbidden, $"the service file reports what happened to the platform's service and does what the portable core answers ({forbidden} belongs to BlindManualBackup.cs)");
        Regex.Matches(service, @"StopSelf\(").Count.Should().Be(1, "the one stop is the core's StopAfter");
    }

    [Fact]
    public void ThePortableCore_RollsBackAnAcceptedRunWhenAnythingBeforeItsTaskThrows_AndNeverLetsAStaleStartNearTheRunSlot()
    {
        var core = Source("Services", "Blind", "BlindManualBackup.cs");
        var onStart = core[core.IndexOf("public void OnStart(", StringComparison.Ordinal)..core.IndexOf("public void OnDestroy()", StringComparison.Ordinal)];
        var startRun = core[core.IndexOf("private void StartRun(", StringComparison.Ordinal)..core.IndexOf("private void Ended(", StringComparison.Ordinal)];

        startRun.Should().Contain("host.ShowForeground(accountId)").And.Contain("Task.Run(");
        startRun.IndexOf("host.ShowForeground(accountId)", StringComparison.Ordinal).Should().BeLessThan(startRun.IndexOf("Task.Run(", StringComparison.Ordinal), "the foreground step is before the task: nothing runs without it");
        startRun.Should().Contain("catch (Exception)").And.Contain("machine.End(accountId, generation, _owner)", "a failure after the run was accepted gives back exactly that generation");
        onStart.Should().NotContain("Task.Run(", "only a Started delivery starts a task, and only through StartRun");
        core.Should().NotContain("BlindSingleRun", "the run slot is the state machine's, not an instance-local single-run state");
    }

    [Fact]
    public void BackUpNow_AsksTheServiceAndTakesItsAnswer_NotACheckThenAct_AndSaysWhoseBackupHoldsThePhone()
    {
        var page = Source("Pages", "BlindHomePage.xaml.cs");
        var handler = page[page.IndexOf("async void OnBackupNowClicked", StringComparison.Ordinal)..];
        handler = handler[..handler.IndexOf("\n    }\n", StringComparison.Ordinal)];

        handler.Should().Contain("if (!Platforms.Android.BlindBackupService.Start(Platform.AppContext, _accountId, out var refusal))");
        handler.Should().Contain("BlindManualBackup.RefusalText(holderName, refusal.Stopping)", "an explicit, account-specific status, and 'stopping' when the holder's service is being destroyed");
        handler.Should().NotContain("RunningFor", "a separate look at who is running and then a start is the race the reservation closes");
    }

    // ---- MINOR-4: paste, scan and link all pass the same gate -----------------------------------------------------------------------

    [Fact]
    public void TheAddPage_TakesEveryTextFromOutside_ThroughTheGate_NeverTrimmingOrAssigningItItself()
    {
        var page = Source("Pages", "BlindAddPage.xaml.cs");

        page.Should().Contain("BlindInviteInput.Accept(text)", "a link the system handed over");
        page.Should().Contain("BlindInviteInput.FromClipboardAsync(").And.Contain("BlindInviteInput.FromScannerAsync(_scanner)");
        page.Should().NotContain(".Trim()", "an over-limit text must not be copied by a trim before it is judged");
        Regex.Matches(page, @"InviteEntry\.Text = ").Count.Should().Be(2, "once from the gate's accepted text, once to empty the box after the computer answered");
        page.Should().Contain("InviteEntry.Text = taken.Text").And.Contain("InviteEntry.Text = \"\"");
        page.Should().Contain("text.Length > BlindInviteInput.MaxLength", "the preview does not look at a text over the limit that came another way");
    }

    // ---- MINOR-2: the Add preview shows the shared record, word for word ------------------------------------------------------------

    [Fact]
    public void TheAddPreview_ShowsExactlyTheSharedRecordsFields_AndNoSentenceOfItsOwn()
    {
        var page = Source("Pages", "BlindAddPage.xaml.cs");
        var xaml = Source("Pages", "BlindAddPage.xaml");

        page.Should().Contain("DescriptionLabel.Text = preview.Description;");
        page.Should().Contain("LimitsLabel.Text = preview.Limits;");
        page.Should().Contain("ValidityLabel.Text = preview.Validity(DateTimeOffset.UtcNow);");
        page.Should().Contain("RefusalLabel.Text = preview.Refusal;");
        xaml.Should().NotMatchRegex(@"<Label x:Name=""(Description|Limits|Validity|Refusal)Label""[^>]*\sText=", "the labels of the preview get their text from the record, never from the page");
        (page + xaml).Should().NotContainEquivalentOf("encrypted", "what the invitation is called is the shared record's wording alone, whatever it becomes");
    }

    // ---- fix round 3, shared busy helper: every route of a failed action goes through the closed failure text ----------------------

    [Fact]
    public void TheApp_SetsTheClosedFailureTextOnTheSharedBusyHelper_BeforeAnythingElseAtStart()
    {
        var program = Source("MauiProgram.cs");
        var create = program[program.IndexOf("public static MauiApp CreateMauiApp()", StringComparison.Ordinal)..];
        var apply = create.IndexOf("BlindFailureSetup.Apply();", StringComparison.Ordinal);

        apply.Should().BeGreaterThan(-1, "an app that never sets a Describer gets the helper's default, which says the text of a local failure");
        apply.Should().BeLessThan(create.IndexOf("var dataDir", StringComparison.Ordinal), "set before the composition of the app, so before any page can run an action");
        apply.Should().BeLessThan(create.IndexOf("builder.Build()", StringComparison.Ordinal));
        Source("Services", "Blind", "BlindFailureSetup.cs").Should().Contain("BusyAction.Describer = BlindFailureText.Say;", "the closed rule of the blind core, by type and never by message");
    }

    [Fact]
    public void TheSharedBusyHelpersRoute_TellsEveryFailureThroughDescribe_WhichUsesTheAppsDescriber()
    {
        var helper = File.ReadAllText(Path.Combine(VaultBoundary.RepoRoot(), "mobile", "BeeMemoryBank.Mobile", "Services", "BusyAction.cs")).ReplaceLineEndings("\n");

        helper.Should().Contain("public static string Describe(Exception ex) => (Describer ?? BeeMemoryBank.Sync.FailureText.Story)(ex);");
        helper.Should().Contain("await ShowResultAsync(options, ok: false, Describe(ex));", "the catch of the helper shows nothing but Describe(ex)");
        Regex.Matches(helper, @"ShowResultAsync\(options, ok: false,").Count.Should().Be(1, "one failure route");
    }

    /// <summary>
    /// What a page may do with a failure: nothing of its own. A page does not format an exception (that is the shared busy helper's Describer, set to the closed rule at start), does not call a formatter directly, and
    /// does not throw a sentence as a plain exception (an unmarked <c>InvalidOperationException</c> would be told as "an unexpected problem": a refusal of the shared core is thrown as
    /// <c>BlindPageSentences.Refusal</c>, which carries the mark that the closed rule passes on).
    /// </summary>
    internal static IReadOnlyList<string> PageFormatsAnExceptionItself(string source)
    {
        var code = Regex.Replace(source, @"(?m)(^|\s)//.*$", "$1");
        var found = new List<string>();
        foreach (var (token, what) in new[]
        {
            ("FailureText.", "calls a failure formatter itself (the busy helper's Describer is the one route)"),
            ("BlindRunReport.", "builds text from an exception with the run report"),
            ("PeerContactLog", "formats a failure of a call to another device itself"),
            ("new InvalidOperationException(", "throws a sentence as an unmarked exception (use BlindPageSentences.Refusal)"),
            ("new ArgumentException(", "throws a sentence as an unmarked exception (use BlindPageSentences.Refusal)"),
        })
            if (code.Contains(token, StringComparison.Ordinal)) found.Add(what);
        return found;
    }

    [Fact]
    public void ThePageRouteGuard_SeesAPageThatFormatsAFailureItself()
    {
        PageFormatsAnExceptionItself("Label.Text = BlindFailureText.Say(ex);").Should().NotBeEmpty();
        PageFormatsAnExceptionItself("Label.Text = BeeMemoryBank.Sync.FailureText.Story(ex);").Should().NotBeEmpty();
        PageFormatsAnExceptionItself("Log(BlindRunReport.Reason(ex));").Should().NotBeEmpty();
        PageFormatsAnExceptionItself("throw new InvalidOperationException(refused);").Should().NotBeEmpty();
        PageFormatsAnExceptionItself("Label.Text = PeerContactLog.Describe(ex);").Should().NotBeEmpty();
        PageFormatsAnExceptionItself("throw BlindPageSentences.Refusal(refused); await TellAsync(\"x\", BusyAction.Describe(ex)); // FailureText.Story is the helper's").Should().BeEmpty();
    }

    [Fact]
    public void NoAndroidPage_FormatsAFailureItself_EveryRouteIsTheBusyHelpersDescribe_AndRefusalsAreMarked()
    {
        foreach (var file in new[] { "BlindHomePage.xaml.cs", "BlindAddPage.xaml.cs", "BlindAccountsPage.xaml.cs" })
            PageFormatsAnExceptionItself(Source("Pages", file)).Should().BeEmpty($"{file}: the page leaves every failure to the busy helper (closed text) or says a fixed sentence");
        Regex.Matches(Source("Pages", "BlindAccountsPage.xaml.cs") + Source("Pages", "BlindHomePage.xaml.cs"), @"BlindPageSentences\.Refusal\(").Count.Should().Be(7, "every refusal of the shared core a page turns into a thrown sentence");
    }

    // ---- MINOR-3: no exception text on a page ---------------------------------------------------------------------------------------

    /// <summary>
    /// What a page does with an exception in its catch blocks: the exception variable may be used only as <c>BusyAction.Describe(ex)</c> (the apps' one rule for failures of what the person asked for);
    /// never its message, its text, its inner exceptions or its type. Fixed sentences need no variable at all. Returns what breaks that.
    /// </summary>
    internal static IReadOnlyList<string> ExceptionTextViolations(string source)
    {
        var found = new List<string>();
        foreach (Match catchClause in Regex.Matches(source, @"catch\s*\(\s*[\w.]+\s+(?<var>\w+)\s*\)"))
        {
            var open = source.IndexOf('{', catchClause.Index + catchClause.Length);
            if (open < 0) continue;
            var depth = 0;
            var end = open;
            for (; end < source.Length; end++)
            {
                if (source[end] == '{') depth++;
                else if (source[end] == '}' && --depth == 0) break;
            }
            var name = catchClause.Groups["var"].Value;
            var body = source[open..Math.Min(end + 1, source.Length)];
            var rest = body.Replace($"BusyAction.Describe({name})", "");
            if (Regex.IsMatch(rest, $@"\b{Regex.Escape(name)}\b"))
                found.Add($"catch ({name}): uses the exception other than as BusyAction.Describe({name}): {Regex.Match(rest, $@"[^\n]*\b{Regex.Escape(name)}\b[^\n]*").Value.Trim()}");
        }
        foreach (var token in new[] { "StatusFailedText", "StatusFailedText(" })
            if (source.Contains(token, StringComparison.Ordinal)) found.Add($"uses the shared {token}, which prints the exception's message");
        if (Regex.IsMatch(source.Replace("_device.LastProblem is not null", ""), @"\bLastProblem\b"))
            found.Add("renders LastProblem, which the scheduler builds from an exception's message");
        return found;
    }

    [Fact]
    public void ThePageGuard_SeesTheHostileHandlers_ItIsMeantToCatch()
    {
        ExceptionTextViolations("catch (Exception ex) { Label.Text = ex.Message; }").Should().NotBeEmpty();
        ExceptionTextViolations("catch (Exception e)\n{\n    await TellAsync(\"x\", $\"failed: {e}\");\n}").Should().NotBeEmpty();
        ExceptionTextViolations("catch (System.IO.IOException ex) { Show(ex.InnerException?.Message); }").Should().NotBeEmpty();
        ExceptionTextViolations("catch (Exception ex) { Show(BlindHomeView.StatusFailedText(ex)); }").Should().NotBeEmpty();
        ExceptionTextViolations("Label.Text = _device.LastProblem;").Should().NotBeEmpty();
        ExceptionTextViolations("catch (Exception ex) { await TellAsync(\"x\", BusyAction.Describe(ex)); }").Should().BeEmpty();
        ExceptionTextViolations("catch (Exception) { Label.Text = BlindPageSentences.CodeNotCopied; }").Should().BeEmpty();
        ExceptionTextViolations("Label.IsVisible = _device.LastProblem is not null;").Should().BeEmpty();
    }

    /// <summary>
    /// What a page reads of a FAILURE'S text (the shared core's record of what went wrong): a <c>.Message</c> of anything, the record <c>LastFailure</c> itself, an inner exception, a stack trace. A page binds the
    /// shared view's fields (<c>BlindHomeView</c>, <c>BlindAccountCard</c>, ...) and says fixed sentences (<see cref="BlindPageSentences"/>); it does not pick a failure's words out of the status or the job's outcome
    /// by itself, because what is in them is the shared core's to clean (it may still carry a path or a library's words today). Written against the names the shared core has now
    /// (<c>BlindJobOutcome.Message</c>, <c>BlindAppStatus.LastFailure.Message</c>); when the shared failure rule lands with other clean field names this guard stays as it is: it forbids reading raw text, not any one name.
    /// </summary>
    internal static IReadOnlyList<string> RawFailureTextReads(string source)
    {
        var code = Regex.Replace(source, @"(?m)(^|\s)//.*$", "$1");
        var found = new List<string>();
        foreach (var (pattern, what) in new[]
        {
            (@"\.Message\b", "reads a .Message"),
            (@"\bLastFailure\b", "reads the status's LastFailure"),
            (@"\.InnerException\b", "reads an inner exception"),
            (@"\.StackTrace\b", "reads a stack trace"),
        })
            foreach (Match match in Regex.Matches(code, pattern))
            {
                var lineStart = code.LastIndexOf('\n', match.Index) + 1;
                var lineEnd = code.IndexOf('\n', match.Index);
                found.Add($"{what}: {code[lineStart..(lineEnd < 0 ? code.Length : lineEnd)].Trim()}");
            }
        return found;
    }

    [Fact]
    public void TheRawFailureTextGuard_SeesTheHostileReads_ItIsMeantToCatch_AndLetsTheCleanOnesPass()
    {
        RawFailureTextReads("NoticeLabel.Text = $\"The backup did not finish: {outcome.Message}\";").Should().ContainSingle();
        RawFailureTextReads("LoadLabel.Text = status.LastFailure.Message;").Should().HaveCount(2, "the record and its message");
        RawFailureTextReads("ShowResult(failure.Message ?? \"x\");").Should().ContainSingle();
        RawFailureTextReads("Show(ex.InnerException);").Should().ContainSingle();
        RawFailureTextReads("Show(e.StackTrace);").Should().ContainSingle();
        RawFailureTextReads("new BusyOptions { Message = \"Please wait\", FailureTitle = \"x\" };").Should().BeEmpty("an object initializer is not a read");
        RawFailureTextReads("// outcome.Message is the shared core's\nvar x = 1; // status.LastFailure.Message\n").Should().BeEmpty("a comment is not code");
        RawFailureTextReads("NoticeLabel.Text = BlindPageSentences.JobEnd(outcome, BlindJobWait.Backup, finished: \"a\", failed: \"b\", ended: \"c\");").Should().BeEmpty();
    }

    [Fact]
    public void NoPageAssignsAFailuresRawText_ToALabel_NotAMessage_NotTheJobOutcomesMessage_NotLastFailure()
    {
        foreach (var file in new[] { "BlindHomePage.xaml.cs", "BlindAccountsPage.xaml.cs", "BlindAddPage.xaml.cs" })
            RawFailureTextReads(Source("Pages", file)).Should().BeEmpty($"{file}: a page binds the shared view's fields and says fixed sentences");
        var home = Source("Pages", "BlindHomePage.xaml.cs");
        Regex.Matches(home, @"BlindPageSentences\.JobEnd\(").Count.Should().Be(3, "the first load, the sync and the backup each end through the one phase-only sentence");
    }

    [Fact]
    public void NoPageOfTheApp_ShowsAnExceptionsTextRaw_NotInTheHomePage_TheAccountsPage_NorTheAddPage()
    {
        foreach (var file in new[] { "BlindHomePage.xaml.cs", "BlindAccountsPage.xaml.cs", "BlindAddPage.xaml.cs" })
            ExceptionTextViolations(Source("Pages", file)).Should().BeEmpty($"{file}: a page says a fixed sentence, or BusyAction.Describe, never the exception's own text");
    }
}
