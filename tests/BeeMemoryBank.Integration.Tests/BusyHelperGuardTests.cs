using System.Text.RegularExpressions;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-311, the guard: a new button that sends something cannot bypass the one busy helper of its technology without saying why.
/// <list type="bullet">
/// <item>Web forms: every <c>&lt;form method="post"&gt;</c> carries <c>data-busy="short|long"</c> (a long one a title) or <c>data-busy-exempt="reason"</c>
/// (the helper is <c>wwwroot/js/bmb-busy.js</c>).</item>
/// <item>Web scripts: every POST/PUT/PATCH/DELETE, XHR or <c>form.submit()</c> sits inside <c>bmbBusy.run(...)</c> or has a
/// <c>// bmb-busy-exempt: reason</c> comment just above it.</item>
/// <item>Desktop and MAUI apps (Android and iPhone): every click/tap handler that is <c>async</c> reaches <c>BusyAction.RunAsync</c> (in its own
/// body or in what it calls) or has a <c>// busy-exempt: reason</c> comment just above it.</item>
/// <item>The blind node's own console page (<c>BlindConsole/wwwroot/index.html</c>, a standalone page with a small inline copy of the rule): every
/// <c>api("POST"|"PUT"|"PATCH"|"DELETE", ...)</c>, sign-in and sign-out call sits inside <c>busy(...)</c>.</item>
/// </list>
/// </summary>
public class BusyHelperGuardTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")) || File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");

    // ---------------------------------------------------------------- Web: forms

    private static readonly Regex FormTag = new(@"<form\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex PostMethod = new(@"method\s*=\s*""post""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The problems of the post forms in one Razor file's text (empty when every one is marked).</summary>
    internal static IEnumerable<string> FormProblems(string file, string text)
    {
        foreach (Match m in FormTag.Matches(text))
        {
            var tag = m.Value;
            if (!PostMethod.IsMatch(tag)) continue;
            var line = text[..m.Index].Count(c => c == '\n') + 1;
            var exempt = Regex.Match(tag, @"data-busy-exempt\s*=\s*""([^""]*)""");
            if (exempt.Success)
            {
                if (string.IsNullOrWhiteSpace(exempt.Groups[1].Value)) yield return $"{file}:{line}: data-busy-exempt needs the reason";
                continue;
            }
            var kind = Regex.Match(tag, @"(?<![\w-])data-busy\s*=\s*""([^""]*)""");
            if (!kind.Success) { yield return $"{file}:{line}: a post form without data-busy=\"short|long\" (or data-busy-exempt=\"reason\")"; continue; }
            if (kind.Groups[1].Value is not ("short" or "long")) { yield return $"{file}:{line}: data-busy is '{kind.Groups[1].Value}', not short or long"; continue; }
            if (kind.Groups[1].Value == "long" && !Regex.IsMatch(tag, @"data-busy-title\s*=\s*""[^""]+"""))
                yield return $"{file}:{line}: a long form names what is going on in the pop-up (data-busy-title)";
        }
    }

    [Fact]
    public void EveryPostForm_IsMarkedShortOrLong_OrSaysWhyNot()
    {
        var problems = new List<string>();
        var pages = Path.Combine(RepoRoot, "server", "BeeMemoryBank.Web", "Pages");
        var seen = 0;
        foreach (var file in Directory.EnumerateFiles(pages, "*.cshtml", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
        {
            var text = File.ReadAllText(file);
            seen += FormTag.Matches(text).Count(m => PostMethod.IsMatch(m.Value));
            problems.AddRange(FormProblems(Path.GetRelativePath(RepoRoot, file), text));
        }
        Assert.True(seen > 50, "the scan should find the post forms of the pages");
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("<form method=\"post\" asp-page-handler=\"Go\">", 1)]
    [InlineData("<form method=\"post\" data-busy>", 1)]
    [InlineData("<form method=\"post\" data-busy=\"long\">", 1)]
    [InlineData("<form method=\"post\" data-busy=\"later\">", 1)]
    [InlineData("<form method=\"post\" data-busy-exempt=\"\">", 1)]
    [InlineData("<form method=\"post\" data-busy=\"short\">", 0)]
    [InlineData("<form method=\"post\" data-busy=\"long\" data-busy-title=\"Encrypting\">", 0)]
    [InlineData("<form method=\"post\" data-busy-exempt=\"the page answers at once\">", 0)]
    [InlineData("<form method=\"get\">", 0)]
    public void TheFormScanner_FlagsAFormThatBypassesTheHelper(string html, int expected)
        => Assert.Equal(expected, FormProblems("x.cshtml", html).Count());

    // ---------------------------------------------------------------- Web: scripts

    private static readonly Regex SendsSomething = new(
        @"method\s*:\s*(['""](POST|PUT|PATCH|DELETE)['""]|[A-Za-z_])|\$\.(ajax|post)\(|\.open\(\s*['""](POST|PUT|PATCH|DELETE)['""]|\.submit\(\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The (start, end) of every balanced <c>token(...)</c> call, strings and comments skipped.</summary>
    internal static List<(int Start, int End)> CallSpans(string text, string token)
    {
        var spans = new List<(int, int)>();
        var from = 0;
        while ((from = text.IndexOf(token, from, StringComparison.Ordinal)) >= 0)
        {
            var depth = 0;
            char? inString = null;
            for (var i = from + token.Length - 1; i < text.Length; i++)
            {
                var c = text[i];
                if (inString != null)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == inString) inString = null;
                    continue;
                }
                if (c is '\'' or '"' or '`') { inString = c; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') { i = text.IndexOf('\n', i); if (i < 0) break; continue; }
                if (c == '(') depth++;
                else if (c == ')' && --depth == 0) { spans.Add((from, i)); break; }
            }
            from += token.Length;
        }
        return spans;
    }

    /// <summary>The places of a script's text that send something without the helper and without an exemption (file:line: text).</summary>
    internal static IEnumerable<string> ScriptProblems(string file, string text)
    {
        var spans = CallSpans(text, "bmbBusy.run(");
        var lines = text.Split('\n');
        foreach (Match m in SendsSomething.Matches(text))
        {
            if (spans.Any(s => s.Start <= m.Index && m.Index <= s.End)) continue;
            var line = text[..m.Index].Count(c => c == '\n') + 1;
            var window = string.Join("\n", lines.Skip(Math.Max(0, line - 4)).Take(4));
            var exempt = Regex.Match(window, @"bmb-busy-exempt:[ 	]*\S");
            if (exempt.Success) continue;
            yield return $"{file}:{line}: {lines[line - 1].Trim()}";
        }
    }

    [Fact]
    public void EveryScriptThatSendsSomething_RunsBehindTheHelper_OrSaysWhyNot()
    {
        var wwwroot = Path.Combine(RepoRoot, "server", "BeeMemoryBank.Web", "wwwroot");
        var lib = Path.GetFullPath(Path.Combine(wwwroot, "lib")) + Path.DirectorySeparatorChar;
        var problems = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(wwwroot, "*.js", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(file);
            if (full.StartsWith(lib, StringComparison.OrdinalIgnoreCase) || IsBuildOutput(full)) continue;
            if (Path.GetFileName(file) == "bmb-busy.js") continue;   // the helper itself
            scanned++;
            problems.AddRange(ScriptProblems(Path.GetRelativePath(wwwroot, file).Replace('\\', '/'), File.ReadAllText(file)));
        }
        Assert.True(scanned > 20, "the scan should find the scripts of the pages");
        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("fetch('/x', { method: 'POST' });", 1)]
    [InlineData("fetch('/x', { method: \"delete\" });", 1)]
    [InlineData("fetch('/x', { method: verb });", 1)]
    [InlineData("form.submit();", 1)]
    [InlineData("xhr.open('POST', '/x');", 1)]
    [InlineData("bmbBusy.run(btn, function () { return fetch('/x', { method: 'POST' }); });", 0)]
    [InlineData("bmbBusy.run(btn, function () { return bmbBusy.request('/x', { method: 'PUT' }); }, { quiet: true });", 0)]
    [InlineData("// bmb-busy-exempt: a streamed answer with its own Stop button\nfetch('/x', { method: 'POST' });", 0)]
    [InlineData("// bmb-busy-exempt:\nfetch('/x', { method: 'POST' });", 1)]
    [InlineData("fetch('/x');", 0)]
    [InlineData("fetch('/x', { method: 'GET' });", 0)]
    public void TheScriptScanner_FlagsASendThatBypassesTheHelper(string js, int expected)
        => Assert.Equal(expected, ScriptProblems("x.js", js).Count());

    [Fact]
    public void TheHelperIsTheOnlyPlaceThatBuildsThePopUp_AndEveryPageLoadsIt()
    {
        var web = Path.Combine(RepoRoot, "server", "BeeMemoryBank.Web");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(web, "wwwroot", "js"), "*.js", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "bmb-busy.js") continue;
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("window.bmbShowBusyModal =", text);   // one pop-up, built in one place
            Assert.DoesNotContain("bmb-busy-modal", text);
        }

        // The page that has its own <html> (no layout) loads the helper itself.
        foreach (var page in new[] { "Setup.cshtml", "Connect.cshtml", Path.Combine("Shared", "_Layout.cshtml") })
            Assert.Contains("/js/bmb-busy.js", File.ReadAllText(Path.Combine(web, "Pages", page)));
    }

    // ---------------------------------------------------------------- Desktop and MAUI: click handlers

    // What a person does that can start something: a tap, a click, a menu item, the Enter key of a field (Completed), a search, a switch, and the pull of a
    // list to refresh (Refreshing: a sync round is a request, not a read). SelectedIndexChanged and TextChanged only change what the screen holds.
    private static readonly Regex EventAttribute = new(
        @"\b(?:Clicked|Click|Tapped|DoubleTapped|Invoked|Refreshing|Completed|SearchButtonPressed|Toggled)=""(\w+)""", RegexOptions.Compiled);

    /// <summary>The body (between the braces, or after =>) of the first method called <paramref name="name"/>, and whether it is async.</summary>
    internal static (bool IsAsync, string Body)? MethodBody(string code, string name, bool anyReturnType = false)
    {
        var returns = anyReturnType ? @"[\w<>\[\],?.]+" : @"void|Task(?:<[^>]+>)?";
        var m = Regex.Match(code, @"\b(async\s+)?(" + returns + @")\s+" + Regex.Escape(name) + @"\s*\([^)]*\)\s*(=>|\{)");
        if (!m.Success) return null;
        var isAsync = m.Groups[1].Success;
        var start = m.Groups[3].Index;
        if (m.Groups[3].Value == "=>")
        {
            var end = code.IndexOf(';', start);
            return (isAsync, code[start..(end < 0 ? code.Length : end)]);
        }
        var depth = 0;
        char? inString = null;
        for (var i = start; i < code.Length; i++)
        {
            var c = code[i];
            if (inString != null)
            {
                if (c == '\\') { i++; continue; }
                if (c == inString) inString = null;
                continue;
            }
            if (c == '"' || c == '\'') { inString = c; continue; }
            if (c == '/' && i + 1 < code.Length && code[i + 1] == '/') { i = code.IndexOf('\n', i); if (i < 0) break; continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return (isAsync, code[start..(i + 1)]);
        }
        return (isAsync, code[start..]);
    }

    /// <summary>
    /// Whether the body reaches <c>BusyAction.RunAsync</c>, in itself or in any method of the same file it calls, however deep and whatever the name's case
    /// (every call is followed once: a helper two or five levels down, a camelCase one).
    /// </summary>
    private static bool ReachesTheHelper(string code, string body, HashSet<string>? seen = null)
    {
        if (body.Contains("BusyAction.RunAsync(", StringComparison.Ordinal)) return true;
        seen ??= [];
        foreach (Match call in Regex.Matches(body, @"\b([A-Za-z_]\w*)\s*\("))
        {
            var name = call.Groups[1].Value;
            if (!seen.Add(name)) continue;
            if (MethodBody(code, name) is { } inner && ReachesTheHelper(code, inner.Body, seen)) return true;
        }
        return false;
    }

    /// <summary>The async click/tap handlers of one XAML file that neither reach the helper nor say why not.</summary>
    internal static IEnumerable<string> HandlerProblems(string file, string xaml, string code)
    {
        foreach (var name in EventAttribute.Matches(xaml).Select(m => m.Groups[1].Value).Distinct())
        {
            if (MethodBody(code, name) is not { } method || !method.IsAsync) continue;   // a synchronous handler sends nothing that waits
            if (ReachesTheHelper(code, method.Body)) continue;
            var at = Regex.Match(code, @"\b(async\s+)?(void|Task(?:<[^>]+>)?)\s+" + Regex.Escape(name) + @"\s*\(").Index;
            var before = string.Join("\n", code[..at].Split('\n').TakeLast(4));
            if (Regex.IsMatch(before, @"//[ 	]*busy-exempt:[ 	]*\S")) continue;
            yield return $"{file}: {name} is async and neither runs behind BusyAction.RunAsync nor says '// busy-exempt: reason'";
        }
    }

    private static readonly string[] Apps =
    [
        Path.Combine("desktop", "BeeMemoryBank.Desktop"),
        Path.Combine("desktop", "BeeMemoryBank.BlindDesktop"),
        Path.Combine("mobile", "BeeMemoryBank.Mobile"),
        Path.Combine("mobile", "BeeMemoryBank.BlindMobile"),
        Path.Combine("mobile", "BeeMemoryBank.BlindIos"),
        Path.Combine("mobile", "BeeMemoryBank.FullIos"),
    ];

    [Fact]
    public void EveryAsyncClickHandler_OfTheDesktopAndPhoneApps_RunsBehindTheHelper_OrSaysWhyNot()
    {
        var problems = new List<string>();
        var handlers = 0;
        foreach (var app in Apps)
        {
            foreach (var xaml in Directory.EnumerateFiles(Path.Combine(RepoRoot, app), "*.*", SearchOption.AllDirectories)
                         .Where(f => (f.EndsWith(".xaml") || f.EndsWith(".axaml")) && !IsBuildOutput(f)))
            {
                var codeFile = xaml + ".cs";
                if (!File.Exists(codeFile)) continue;
                var text = File.ReadAllText(xaml);
                var code = File.ReadAllText(codeFile);
                handlers += EventAttribute.Matches(text).Count;
                problems.AddRange(HandlerProblems(Path.GetRelativePath(RepoRoot, codeFile), text, code));
            }
        }
        Assert.True(handlers > 100, "the scan should find the handlers of the apps");
        Assert.True(problems.Count == 0, "\n" + string.Join("\n", problems));
    }

    // ---------------------------------------------------------------- BMB-311 fix round: what the phone apps do while a job runs

    private static string Handler(string code, string name)
    {
        code = code.Replace("\r\n", "\n");   // a working copy may carry either line ending
        var starts = new[] { code.IndexOf("async void " + name, StringComparison.Ordinal), code.IndexOf("async Task " + name, StringComparison.Ordinal) }.Where(i => i >= 0).ToList();
        Assert.True(starts.Count > 0, $"{name} is not in the page");
        var start = starts.Min();
        var end = code.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return code[start..(end < 0 ? code.Length : end)];
    }

    [Theory]
    [InlineData("BlindMobile")]
    [InlineData("BlindIos")]
    public void TheBlindPhonePages_WaitForAJobByItsState_NotByAClockThatGivesTheButtonBack(string app)
    {
        // F3: a wait of 20 seconds for the job to be seen, an hour (Android) or 30 minutes (iPhone) for it to end, and then the button was free to ask
        // for another job although the first might still run. The wait is BlindJobWait.Judge now: open while the job runs or is queued.
        var page = File.ReadAllText(Path.Combine(RepoRoot, "mobile", $"BeeMemoryBank.{app}", "Pages", "BlindHomePage.xaml.cs"));

        Assert.Contains("BlindJobWait.Judge(", page);
        Assert.Contains("BlindJobWait.StillRunningText", page);
        Assert.DoesNotContain("AddHours(1)", page);
        Assert.DoesNotContain("AddSeconds(20)", page);
        Assert.DoesNotContain("TimeSpan.FromMinutes(30)", page);
        foreach (var handler in new[] { "OnConnectClicked", "OnSyncNowClicked", "OnBackupNowClicked" })
        {
            // The iPhone's Connect is one funnel for the button, a link and the end-to-end code (R3): the handler asks it, and the helper's work is in it.
            var body = Handler(page, handler) + (handler == "OnConnectClicked" && page.Contains("async Task ConnectAsync(") ? Handler(page, "ConnectAsync") : "");
            Assert.Contains("BusyAction.RunAsync(", body);
            Assert.Contains("BlindJobWait.Ask(", body);
            Assert.Contains("Long = true", body);
        }
    }

    [Theory]
    [InlineData("BlindMobile")]
    [InlineData("BlindIos")]
    public void TheBlindPhonePages_ClassifyTheSaveToActionAsLong(string app)
    {
        // F11: "Save the latest backup to..." hands a file to the person's own place through a picker or a share sheet; the inventory calls it long.
        var page = File.ReadAllText(Path.Combine(RepoRoot, "mobile", $"BeeMemoryBank.{app}", "Pages", "BlindHomePage.xaml.cs"));

        var body = Handler(page, "OnSaveToClicked");

        Assert.Contains("BusyAction.RunAsync(", body);
        Assert.Contains("Long = true", body);
    }

    [Fact]
    public void TheIosBlindConnect_RunsBehindTheHelper_AndWaitsForTheFirstLoad()
    {
        // F10: Connect called Accept directly - no pop-up, no guard against a second tap, no result of the first copy.
        var page = File.ReadAllText(Path.Combine(RepoRoot, "mobile", "BeeMemoryBank.BlindIos", "Pages", "BlindHomePage.xaml.cs"));

        var body = Handler(page, "ConnectAsync");

        Assert.Contains("BusyAction.RunAsync(sender,", body);
        Assert.Contains("Long = true", body);
        Assert.Contains("BlindJobWait.FirstLoad", body);
        Assert.Contains("WaitForJobAsync(", body);
        Assert.Contains("ConnectAsync(sender as Button", Handler(page, "OnConnectClicked"));
    }

    /// <summary>
    /// BMB-311 fix2 (R3): a link the app was opened with, one that waited for the screen, and the end-to-end check's code start the first load. They did it OUTSIDE the
    /// busy helper (no pop-up, a second link or a tap could start another load). They must all end in the one funnel of the Connect button.
    /// </summary>
    [Fact]
    public void TheIosBlindPendingLinkAndTheE2ECode_AreAcceptedThroughTheSameBusyPathAsTheConnectButton()
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot, "mobile", "BeeMemoryBank.BlindIos", "Pages", "BlindHomePage.xaml.cs"));
        var app = File.ReadAllText(Path.Combine(RepoRoot, "mobile", "BeeMemoryBank.BlindIos", "App.xaml.cs"));

        Assert.DoesNotContain("AcceptLink(", page);     // the synchronous way in is gone
        Assert.DoesNotContain(".AcceptLink(", app);
        Assert.Contains("page.AcceptLinkAsync(url)", app);
        var appearing = page[page.IndexOf("protected override async void OnAppearing", StringComparison.Ordinal)..page.IndexOf("protected override void OnDisappearing", StringComparison.Ordinal)];
        Assert.Contains("await AcceptLinkAsync(link)", appearing);
        Assert.Contains("await AcceptLinkAsync(e2eCode)", appearing);
        Assert.Contains("ConnectAsync(ConnectButton, url, fromLink: true)", Handler(page, "AcceptLinkAsync"));
        // The code is accepted, and the first load asked for, in ONE place, and that place is inside the helper's work.
        var accepts = System.Text.RegularExpressions.Regex.Matches(page, @"(?<!bool )\bAccept\(").Count;
        Assert.Equal(1, accepts);
        Assert.True(page.IndexOf("BusyAction.RunAsync(sender,", StringComparison.Ordinal) < page.IndexOf("if (!Accept(code))", StringComparison.Ordinal));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(page, @"Scheduler\.RequestHeavy\(").Count);
    }

    [Fact]
    public void TheFullIosFolderRefresh_AndEveryPullToRefreshOfThePhoneApps_AskForTheSyncRoundThroughTheHelper()
    {
        // F11: pulling a list down starts a sync round - a request to the computer, not a read.
        var pages = new List<string>
        {
            Path.Combine("mobile", "BeeMemoryBank.FullIos", "Pages", "FolderPage.xaml.cs"),
            Path.Combine("mobile", "BeeMemoryBank.Mobile", "Pages", "ArticlesPage.xaml.cs"),
            Path.Combine("mobile", "BeeMemoryBank.Mobile", "Pages", "ArticleDetailPage.xaml.cs"),
            Path.Combine("mobile", "BeeMemoryBank.Mobile", "Pages", "StatusPage.xaml.cs"),
            Path.Combine("mobile", "BeeMemoryBank.Mobile", "Pages", "TagsPage.xaml.cs"),
            Path.Combine("mobile", "BeeMemoryBank.Mobile", "Pages", "TreePage.xaml.cs"),
        };
        foreach (var page in pages)
        {
            var body = Handler(File.ReadAllText(Path.Combine(RepoRoot, page)), "OnRefreshing");
            Assert.True(body.Contains("BusyAction.RunAsync(") && body.Contains("IsRefreshing = false"), $"{page}: the pull is not behind the helper");
        }
    }

    /// <summary>
    /// BMB-311 fix2 (R5): whatever the clean-up of the MAUI helper does - a pop-up whose dismissal throws, a spinner, a control of a page that is gone - the key of the
    /// running work is freed by an OUTER finally that holds nothing else, and no clean-up step is called outside a guard. A platform exception that skipped
    /// <c>Running.Remove(key)</c> refused the same button for ever. (The helper is MAUI code and cannot be run here; this reads its structure.)
    /// </summary>
    [Fact]
    public void TheMauiHelper_FreesTheKeyInAnOuterFinally_WhateverTheCleanUpThrows()
    {
        var helper = File.ReadAllText(Path.Combine(RepoRoot, "mobile", "BeeMemoryBank.Mobile", "Services", "BusyAction.cs"));
        var run = helper[helper.IndexOf("private static async Task<bool> RunAsync", StringComparison.Ordinal)..helper.IndexOf("public static bool IsRunning", StringComparison.Ordinal)];

        // 1. The removal of the key is the whole of a finally of its own, the last thing the method does.
        System.Text.RegularExpressions.Regex.Matches(run, @"finally\s*\{\s*Running\.Remove\(key\);\s*\}").Should().HaveCount(1);
        System.Text.RegularExpressions.Regex.Matches(run, @"Running\.Remove\(key\)").Should().HaveCount(1, "one place frees the key");
        // 2. Every clean-up step before it is inside the try that finally covers: nothing of the clean-up runs outside it.
        var outer = run[run.IndexOf("finally", run.IndexOf("catch (Exception ex)", StringComparison.Ordinal), StringComparison.Ordinal)..];
        outer.Should().Contain("try").And.Contain("Running.Remove(key)");
        outer.IndexOf("try", StringComparison.Ordinal).Should().BeLessThan(outer.IndexOf("CloseModalQuietlyAsync", StringComparison.Ordinal));
        outer.IndexOf("CloseModalQuietlyAsync", StringComparison.Ordinal).Should().BeLessThan(outer.IndexOf("Running.Remove(key)", StringComparison.Ordinal));
        // 3. The pop-up is never closed outside the quiet wrapper, and the wrapper swallows what the platform throws.
        System.Text.RegularExpressions.Regex.Matches(run, @"(?<!Quietly)await CloseModalAsync\(").Should().BeEmpty("a throwing dismissal must not escape the clean-up");
        helper.Should().MatchRegex(@"CloseModalQuietlyAsync\([^)]*\)\s*\{\s*try \{ await CloseModalAsync\(navigation, modal\); \}\s*catch \(Exception\)");
        // 4. The spinner and the controls are touched through the guard.
        outer.Should().Contain("Guarded(() => spin?.Stop())").And.Contain("Guarded(() =>");
    }

    [Fact]
    public void TheMauiHelper_DoesNotTouchControlsOfAPageThatIsGone_AndLeavesNoPopUpStanding()
    {
        // F15: the page of the target can leave while the work goes on (a rotation re-creates the activity; a page is replaced or disposed).
        var helper = File.ReadAllText(Path.Combine(RepoRoot, "mobile", "BeeMemoryBank.Mobile", "Services", "BusyAction.cs"));

        Assert.Contains("Unloaded +=", helper);                      // the spinner stops with the page
        Assert.Contains("Unloaded -=", helper);
        Assert.Contains("Guarded(", helper);                          // no touch of a disposed control keeps the key taken
        Assert.Contains("Handler is not null", helper);               // a result is not written into a label of a disposed page
        Assert.Contains("DismissAsync", helper);                      // an old pop-up under another page closes the moment it is shown again
        Assert.Contains("OnAppearing", helper);
        Assert.Contains("catch (InvalidOperationException)", helper); // the modal stack changed under it
    }

    [Fact]
    public void TheDesktopOldCopyDelete_RunsBehindTheHelper()
    {
        // F12: a recursive delete of a whole vault, with no pop-up and no guard, while another storage action could be started.
        var code = File.ReadAllText(Path.Combine(RepoRoot, "desktop", "BeeMemoryBank.Desktop", "Views", "ManageStoragesWindow.axaml.cs"));
        var start = code.IndexOf("Task OfferToDeleteOldCopyAsync", StringComparison.Ordinal);
        var body = code[start..code.IndexOf("private static string SafeFolderName", start, StringComparison.Ordinal)];

        Assert.Contains("BusyAction.RunAsync(this, null", body);
        Assert.Contains("Long = true", body);
        Assert.True(body.IndexOf("BusyAction.RunAsync", StringComparison.Ordinal) < body.IndexOf("Directory.Delete", StringComparison.Ordinal) || body.Contains("Task.Run(() => Directory.Delete"),
            "the delete is inside the helper's work");
        // The safety checks stay where they were, before the delete.
        Assert.True(body.IndexOf("VaultFiles.Inspect(oldPath)", StringComparison.Ordinal) < body.IndexOf("BusyAction.RunAsync", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the commands of the blind desktop

    /// <summary>The argument text of every <c>new RelayCommand(...)</c> of a view model, with the method bodies of the names it passes (the can-execute).</summary>
    internal static IEnumerable<(string Text, int Line, bool Exempt)> RelayCommands(string code)
    {
        foreach (Match m in Regex.Matches(code, @"new\s+RelayCommand\s*\("))
        {
            var start = m.Index + m.Length;
            var depth = 1;
            var i = start;
            for (; i < code.Length && depth > 0; i++)
            {
                if (code[i] == '(') depth++;
                else if (code[i] == ')') depth--;
            }
            var args = code[start..Math.Max(start, i - 1)];
            var text = args;
            foreach (Match name in Regex.Matches(args, @"\b([A-Za-z_]\w*)\b"))
                if (MethodBody(code, name.Groups[1].Value, anyReturnType: true) is { } inner) text += "\n" + inner.Body;
            var line = code[..m.Index].Count(c => c == '\n') + 1;
            var before = string.Join("\n", code[..m.Index].Split('\n').TakeLast(4));
            yield return (text, line, Regex.IsMatch(before, @"//[ \t]*busy-exempt:[ \t]*\S"));
        }
    }

    /// <summary>The commands of a view model that can be started while a long operation runs: their can-execute does not look at the busy state, and no reason is given.</summary>
    internal static IEnumerable<string> CommandProblems(string file, string code)
    {
        foreach (var (text, line, exempt) in RelayCommands(code))
        {
            if (exempt || Regex.IsMatch(text, "busy", RegexOptions.IgnoreCase)) continue;
            yield return $"{file}:{line}: a command whose can-execute ignores the busy state and says no '// busy-exempt: reason'";
        }
    }

    [Fact]
    public void EveryCommandOfTheBlindDesktop_IsOffWhileALongOperationRuns_OrSaysWhyNot()
    {
        var viewModels = Path.Combine(RepoRoot, "desktop", "BeeMemoryBank.BlindDesktop", "ViewModels");
        var problems = new List<string>();
        var commands = 0;
        foreach (var file in Directory.EnumerateFiles(viewModels, "*.cs", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)))
        {
            var code = File.ReadAllText(file);
            commands += RelayCommands(code).Count();
            problems.AddRange(CommandProblems(Path.GetRelativePath(RepoRoot, file), code));
        }
        Assert.True(commands >= 10, "the scan should find the commands of the window");
        Assert.Empty(problems);
    }

    // ---------------------------------------------------------------- BMB-311 fix2 (R4): every command a XAML binds, whatever class makes it

    // Command="{Binding Name}", ReturnCommand="{Binding Path=Name}", CompiledBinding, a dotted path (the last name is the command). A binding with a Source is the
    // command of another ELEMENT (an Entry's ReturnCommand that points to the button), whose own handler the handler scanner covers.
    private static readonly Regex CommandBinding = new(
        @"\b\w*Command\s*=\s*""\{(?:\w+:)?(?:Binding|CompiledBinding|ReflectionBinding)\s+(?!Source\s*=)(?:Path\s*=\s*)?([A-Za-z_][\w.]*)(?!\s*=)", RegexOptions.Compiled);

    // A command attribute whose binding starts with a Source: the group is the source expression up to the end of the binding's first argument.
    private static readonly Regex SourceCommandBinding = new(
        @"\b\w*Command\s*=\s*""\{(?:\w+:)?(?:Binding|CompiledBinding|ReflectionBinding)\s+Source\s*=\s*(\{[^{}]*\}|[^,}""]*)", RegexOptions.Compiled);

    /// <summary>The text from <paramref name="from"/> to the end of the statement (a ';' outside every bracket and string).</summary>
    private static string StatementFrom(string code, int from)
    {
        var depth = 0;
        char? inString = null;
        for (var i = from; i < code.Length; i++)
        {
            var c = code[i];
            if (inString != null)
            {
                if (c == '\\') { i++; continue; }
                if (c == inString) inString = null;
                continue;
            }
            if (c == '"' || c == '\'') { inString = c; continue; }
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth--;
            else if (c == ';' && depth <= 0) return code[from..i];
        }
        return code[from..];
    }

    /// <summary>The arguments of the call whose opening bracket is at <paramref name="open"/>, split on the commas outside every bracket and string.</summary>
    private static List<string> ArgumentsOf(string code, int open)
    {
        var args = new List<string>();
        var depth = 0;
        var start = open + 1;
        char? inString = null;
        for (var i = open; i < code.Length; i++)
        {
            var c = code[i];
            if (inString != null)
            {
                if (c == '\\') { i++; continue; }
                if (c == inString) inString = null;
                continue;
            }
            if (c == '"' || c == '\'') { inString = c; continue; }
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']')
            {
                if (--depth == 0) { args.Add(code[start..i]); return args; }
            }
            else if (c == ',' && depth == 1) { args.Add(code[start..i]); start = i + 1; }
        }
        return args;
    }

    /// <summary>
    /// The commands a view's XAML binds that can be started while a long operation runs: whatever makes them (RelayCommand, AsyncRelayCommand, Command, a factory), the
    /// place that assigns them is found across every view-model file of the app (an assignment, an expression-bodied property, a property initializer; one that takes
    /// the command from a constructor parameter is followed to every <c>new Type(...)</c> that passes it), and its text (the arguments, the methods it names, every call
    /// followed) must look at the busy state, reach <c>BusyAction.RunAsync</c>, or be preceded by <c>// busy-exempt: reason</c>. A bound name that no file defines is a
    /// dead button and is reported.
    /// </summary>
    internal static IEnumerable<string> BoundCommandProblems(IReadOnlyDictionary<string, string> xamls, IReadOnlyDictionary<string, string> codes)
    {
        bool Allowed(string code, string text, int definitionAt)
        {
            foreach (Match identifier in Regex.Matches(text, @"\b([A-Za-z_]\w*)\b"))
                if (MethodBody(code, identifier.Groups[1].Value, anyReturnType: true) is { } inner) text += "\n" + inner.Body;
            var before = string.Join("\n", code[..Math.Max(0, definitionAt)].Split('\n').TakeLast(4));
            return Regex.IsMatch(before, @"//[ \t]*busy-exempt:[ \t]*\S") || Regex.IsMatch(text, "busy", RegexOptions.IgnoreCase) || ReachesTheHelper(code, text);
        }

        foreach (var (xamlFile, xaml) in xamls)
        {
            // A command bound with a Source is the command of another element, which is not a view-model command to look up: it is allowed only when that element is named
            // in the same file and carries a handler or a binding that the scanners above and below cover. Any other Source (a resource, a relative source) cannot be followed.
            foreach (Match forwarded in SourceCommandBinding.Matches(xaml))
            {
                var source = Regex.Match(forwarded.Groups[1].Value, @"^\{x:Reference\s+(\w+)\s*\}");
                var element = source.Success
                    ? Regex.Match(xaml, @"<[\w:.]+\s[^<>]*?\bx:Name\s*=\s*""" + Regex.Escape(source.Groups[1].Value) + @"""[^<>]*>", RegexOptions.Singleline)
                    : Match.Empty;
                if (!element.Success || !(EventAttribute.IsMatch(element.Value) || CommandBinding.IsMatch(element.Value)))
                    yield return $"{xamlFile}: '{forwarded.Value}' forwards a command from a source the guard cannot follow: name an element of the same file whose own handler or command it covers, or bind a view-model command";
            }

            foreach (var name in CommandBinding.Matches(xaml).Select(m => m.Groups[1].Value.Split('.').Last()).Distinct())
            {
                var defined = false;
                var allowed = true;
                foreach (var (_, code) in codes)
                {
                    foreach (Match definition in Regex.Matches(code, @"(?<![\w.])(?:this\.)?" + Regex.Escape(name) + @"\s*(?:\{[^}]*\}\s*)?(=>|=(?!=))"))
                    {
                        defined = true;
                        var text = StatementFrom(code, definition.Index + definition.Length);
                        // "= save": the command comes in through a constructor parameter. Every place that makes the object says what the command is.
                        if (Regex.IsMatch(text.Trim(), @"^[A-Za-z_]\w*$"))
                        {
                            var parameter = text.Trim();
                            var made = false;
                            foreach (var (_, owner) in codes)
                                foreach (Match declaration in Regex.Matches(owner, @"\b(?:class|record|struct)\s+(\w+)\s*\(([^)]*)\)"))
                                {
                                    var parameters = declaration.Groups[2].Value.Split(',').Select(p => p.Trim().Split(' ').Last()).ToList();
                                    var position = parameters.IndexOf(parameter);
                                    if (position < 0) continue;
                                    foreach (var (_, user) in codes)
                                        foreach (Match creation in Regex.Matches(user, @"\bnew\s+" + Regex.Escape(declaration.Groups[1].Value) + @"\s*\("))
                                        {
                                            var args = ArgumentsOf(user, creation.Index + creation.Length - 1);
                                            if (position >= args.Count) continue;
                                            made = true;
                                            if (!Allowed(user, args[position], creation.Index)) allowed = false;
                                        }
                                }
                            if (made) continue;
                        }
                        if (!Allowed(code, text, definition.Index)) allowed = false;
                    }
                }
                if (!defined) yield return $"{xamlFile}: the command '{name}' is bound but no view model of the app defines it";
                else if (!allowed) yield return $"{xamlFile}: the command '{name}' can be started while a long operation runs: its can-execute ignores the busy state, it does not run behind BusyAction.RunAsync, and no '// busy-exempt: reason' says why";
            }
        }
    }

    [Fact]
    public void EveryCommandABindingNamesInTheDesktopAndPhoneApps_IsOffWhileALongOperationRuns_OrRunsBehindTheHelper_OrSaysWhyNot()
    {
        var problems = new List<string>();
        var bindings = 0;
        foreach (var app in Apps)
        {
            var files = Directory.EnumerateFiles(Path.Combine(RepoRoot, app), "*.*", SearchOption.AllDirectories).Where(f => !IsBuildOutput(f)).ToList();
            var xamls = files.Where(f => f.EndsWith(".xaml") || f.EndsWith(".axaml")).ToDictionary(f => Path.GetRelativePath(RepoRoot, f), File.ReadAllText);
            var codes = files.Where(f => f.EndsWith(".cs")).ToDictionary(f => Path.GetRelativePath(RepoRoot, f), File.ReadAllText);
            bindings += xamls.Values.Sum(x => CommandBinding.Matches(x).Count);
            problems.AddRange(BoundCommandProblems(xamls, codes));
        }
        Assert.True(bindings >= 10, "the scan should find the command bindings of the blind desktop window: " + bindings);
        Assert.Empty(problems);
    }

    private static int BoundProblems(string xaml, params string[] code) =>
        BoundCommandProblems(new Dictionary<string, string> { ["V.axaml"] = xaml }, code.Select((c, i) => (c, i)).ToDictionary(t => $"M{t.i}.cs", t => t.c)).Count();

    [Theory]
    // a command made by ANY class is read, not only RelayCommand
    [InlineData("<Button Command=\"{Binding GoCommand}\"/>", "GoCommand = new AsyncRelayCommand(DoAsync);", 1)]
    [InlineData("<Button Command=\"{Binding GoCommand}\"/>", "GoCommand = new Command(Go);", 1)]
    [InlineData("<Button Command=\"{Binding GoCommand}\"/>", "public ICommand GoCommand => new RelayCommand(Go);", 1)]
    [InlineData("<Button Command=\"{Binding GoCommand}\"/>", "GoCommand = Commands.Make(Go);", 1)]
    // the busy state in the can-execute, the helper under the command, or a reason
    [InlineData("<Button Command=\"{Binding GoCommand}\"/>", "GoCommand = new AsyncRelayCommand(DoAsync, () => !IsBusy);", 0)]
    [InlineData("<Button Command=\"{Binding GoCommand}\"/>", "GoCommand = new AsyncRelayCommand(RunAsync);\nprivate async Task RunAsync() { await Step(); }\nprivate async Task Step() { await BusyAction.RunAsync(null, Work); }", 0)]
    [InlineData("<Button Command=\"{Binding GoCommand}\"/>", "// busy-exempt: only opens a panel\nGoCommand = new Command(Open);", 0)]
    [InlineData("<Button Command=\"{Binding GoCommand}\"/>", "// busy-exempt:\nGoCommand = new Command(Open);", 1)]
    // the other ways to write the binding, and a name defined in another file of the app
    [InlineData("<Button Command=\"{Binding Path=GoCommand}\"/>", "GoCommand = new Command(Go);", 1)]
    [InlineData("<Button Command=\"{CompiledBinding Row.GoCommand}\"/>", "GoCommand = new Command(Go);", 1)]
    [InlineData("<Entry ReturnCommand=\"{Binding GoCommand}\"/>", "GoCommand = new Command(Go);", 1)]
    [InlineData("<Button Command=\"{Binding GoCommand, Mode=OneWay}\"/>", "GoCommand = new Command(Go, () => !_busy);", 0)]
    // a name nothing defines is a dead button; a binding to another element is not a view-model command
    [InlineData("<Button Command=\"{Binding MissingCommand}\"/>", "GoCommand = new Command(Go, () => !_busy);", 1)]
    // a command forwarded from another element: allowed when that element is named in the file and has a handler or a binding the scanners cover, refused otherwise
    [InlineData("<Entry ReturnCommand=\"{Binding Source={x:Reference Go}, Path=Command}\"/><Button x:Name=\"Go\" Clicked=\"OnGo\"/>", "void OnGo(object s, EventArgs e) { Scheduler.RequestHeavy(null); }", 0)]
    [InlineData("<Entry ReturnCommand=\"{Binding Source={x:Reference Go}, Path=Command}\"/><Button x:Name=\"Go\" Command=\"{Binding GoCommand}\"/>", "GoCommand = new Command(Go, () => !_busy);", 0)]
    [InlineData("<Entry ReturnCommand=\"{Binding Source={x:Reference Go}, Path=Command}\"/>", "Nothing = 1;", 1)]
    [InlineData("<Entry ReturnCommand=\"{Binding Source={x:Reference Go}, Path=Command}\"/><Button x:Name=\"Go\" Text=\"Go\"/>", "Nothing = 1;", 1)]
    [InlineData("<Entry ReturnCommand=\"{Binding Source={StaticResource Shared}, Path=Command}\"/>", "Nothing = 1;", 1)]
    [InlineData("<Entry ReturnCommand=\"{Binding Source={RelativeSource Self}, Path=Command}\"/>", "Nothing = 1;", 1)]
    public void TheBoundCommandScanner_FlagsACommandThatBypassesTheBusyState_WhateverClassMakesIt(string xaml, string code, int expected)
        => Assert.Equal(expected, BoundProblems(xaml, code));

    [Fact]
    public void TheBoundCommandScanner_FollowsAConstructorParameterToEveryPlaceThatMakesTheObject()
    {
        var xaml = "<Button Command=\"{Binding SaveCommand}\"/>";
        var row = "public sealed class Row(string name, ICommand save) { public ICommand SaveCommand { get; } = save; }";

        // the real shape of the backup rows of the blind desktop: the view model passes the command in
        Assert.Equal(0, BoundProblems(xaml, row, "var a = new Row(n, new RelayCommand(() => _ = Save(n), () => !_busy, failed));"));
        Assert.Equal(1, BoundProblems(xaml, row, "var a = new Row(n, new RelayCommand(() => _ = Save(n), null, failed));"));
        // one safe place does not excuse another that bypasses
        Assert.Equal(1, BoundProblems(xaml, row, "var a = new Row(n, new RelayCommand(Go, () => !_busy));\nvar b = new Row(m, new AsyncRelayCommand(GoAsync));"));
    }

    [Fact]
    public void TheBoundCommandScanner_FindsTheCommandInAnotherFileOfTheApp()
    {
        var xaml = "<Button Command=\"{Binding Row.SaveCommand}\"/>";

        Assert.Equal(0, BoundProblems(xaml, "class Other { }", "class Row { public Row() { SaveCommand = new AsyncRelayCommand(Save, () => !_busy); } }"));
        Assert.Equal(1, BoundProblems(xaml, "class Other { }", "class Row { public Row() { SaveCommand = new AsyncRelayCommand(Save); } }"));
    }

    [Theory]
    [InlineData("X = new RelayCommand(Go, () => _can && !_busy, failed);", 0)]
    [InlineData("X = new RelayCommand(Go, () => _can, failed);", 1)]
    [InlineData("X = new RelayCommand(() => _ = GoAsync(), CanGo, failed);\nprivate bool CanGo() => !_wipeBusy && _x;", 0)]
    [InlineData("X = new RelayCommand(() => _ = GoAsync(), CanGo, failed);\nprivate bool CanGo() => _x;", 1)]
    [InlineData("// busy-exempt: copies the code to the clipboard\nX = new RelayCommand(Copy, null, failed);", 0)]
    [InlineData("// busy-exempt:\nX = new RelayCommand(Copy, null, failed);", 1)]
    [InlineData("Rows = new RelayCommand(() => _ = SaveAsync(n), () => !_busy, failed);", 0)]
    public void TheCommandScanner_FlagsACommandThatIgnoresTheBusyState(string code, int expected)
        => Assert.Equal(expected, CommandProblems("x.cs", code).Count());

    [Theory]
    // a helper five levels down, and a camelCase one: the scanner follows every call, not two levels of capitalized ones
    [InlineData("<Button Clicked=\"OnGo\"/>", "private async void OnGo(object? s, EventArgs e) => await A();\nprivate async Task A() => await B();\nprivate async Task B() => await C();\nprivate async Task C() => await D();\nprivate async Task D() => await E();\nprivate async Task E() { await BusyAction.RunAsync(null, Work); }", 0)]
    [InlineData("<Button Clicked=\"OnGo\"/>", "private async void OnGo(object? s, EventArgs e) => await run();\nprivate async Task run() { await BusyAction.RunAsync(null, Work); }", 0)]
    [InlineData("<Button Clicked=\"OnGo\"/>", "private async void OnGo(object? s, EventArgs e) => await A();\nprivate async Task A() => await B();\nprivate async Task B() => await Work();", 1)]
    // the other things a person does that start something
    [InlineData("<RefreshView Refreshing=\"OnPull\"/>", "private async void OnPull(object? s, EventArgs e) { await Sync(); }", 1)]
    [InlineData("<Entry Completed=\"OnEnter\"/>", "private async void OnEnter(object? s, EventArgs e) { await Send(); }", 1)]
    [InlineData("<Switch Toggled=\"OnFlip\"/>", "private async void OnFlip(object? s, ToggledEventArgs e) { await Save(); }", 1)]
    [InlineData("<RefreshView Refreshing=\"OnPull\"/>", "private async void OnPull(object? s, EventArgs e) { await BusyAction.RunAsync(null, Sync); }", 0)]
    public void TheHandlerScanner_FollowsEveryCall_AndSeesTheOtherWaysToStartSomething(string xaml, string code, int expected)
        => Assert.Equal(expected, HandlerProblems("x.xaml.cs", xaml, code).Count());

    [Theory]
    [InlineData("<Button Clicked=\"OnGo\"/>", "private async void OnGo(object? s, EventArgs e) { await Work(); }", 1)]
    [InlineData("<Button Clicked=\"OnGo\"/>", "private async void OnGo(object? s, EventArgs e) { await BusyAction.RunAsync(s as Button, Work); }", 0)]
    [InlineData("<Button Clicked=\"OnGo\"/>", "private async void OnGo(object? s, EventArgs e) => await GoAsync();\nprivate async Task GoAsync() { await BusyAction.RunAsync(null, Work); }", 0)]
    [InlineData("<Button Clicked=\"OnGo\"/>", "// busy-exempt: opens the editor\nprivate async void OnGo(object? s, EventArgs e) { await Nav(); }", 0)]
    [InlineData("<Button Clicked=\"OnGo\"/>", "// busy-exempt:\nprivate async void OnGo(object? s, EventArgs e) { await Nav(); }", 1)]
    [InlineData("<Button Clicked=\"OnGo\"/>", "private void OnGo(object? s, EventArgs e) { Close(); }", 0)]
    public void TheHandlerScanner_FlagsAnAsyncHandlerThatBypassesTheHelper(string xaml, string code, int expected)
        => Assert.Equal(expected, HandlerProblems("x.xaml.cs", xaml, code).Count());

    // ---------------------------------------------------------------- the blind node console page

    private static readonly Regex ConsoleSend = new(
        @"\bapi\(\s*""(POST|PUT|PATCH|DELETE)""|\bfetch\(\s*""/(login|logout)""", RegexOptions.Compiled);

    /// <summary>The places of the console page that change something outside <c>busy(...)</c> and without an exemption (file:line: text).</summary>
    internal static IEnumerable<string> ConsolePageProblems(string file, string text)
    {
        var spans = CallSpans(text, "busy(")
            .Where(s => s.Start == 0 || !(char.IsLetterOrDigit(text[s.Start - 1]) || text[s.Start - 1] is '_' or '.'))
            .ToList();
        var lines = text.Split('\n');
        foreach (Match m in ConsoleSend.Matches(text))
        {
            if (spans.Any(s => s.Start <= m.Index && m.Index <= s.End)) continue;
            var line = text[..m.Index].Count(c => c == '\n') + 1;
            var window = string.Join("\n", lines.Skip(Math.Max(0, line - 4)).Take(4));
            if (Regex.IsMatch(window, @"busy-exempt:[ \t]*\S")) continue;
            yield return $"{file}:{line}: {lines[line - 1].Trim()}";
        }
    }

    [Fact]
    public void EveryChangeOnTheBlindConsolePage_RunsBehindItsBusyHelper()
    {
        var page = Path.Combine(RepoRoot, "server", "BeeMemoryBank.BlindConsole", "wwwroot", "index.html");
        var text = File.ReadAllText(page);
        Assert.Contains("async function busy(", text);
        Assert.Contains("id=\"busy\"", text);   // the "please wait" pop-up a long operation puts up
        Assert.True(ConsoleSend.Matches(text).Count >= 10, "the scan should find the buttons of the page");
        Assert.Empty(ConsolePageProblems("BlindConsole/wwwroot/index.html", text));
    }

    [Theory]
    [InlineData("$(\"x\").onclick = async () => { await api(\"POST\", \"api/x\"); };", 1)]
    [InlineData("await fetch(\"/logout\", { method: \"POST\" });", 1)]
    [InlineData("$(\"x\").onclick = () => busy($(\"x\"), async () => { await api(\"POST\", \"api/x\"); });", 0)]
    [InlineData("$(\"x\").onclick = () => isbusy($(\"x\"), async () => { await api(\"POST\", \"api/x\"); });", 1)]
    [InlineData("const r = await api(\"GET\", \"api/x\");", 0)]
    [InlineData("// busy-exempt: a read the server answers with POST\nawait api(\"POST\", \"api/x\");", 0)]
    public void TheConsoleScanner_FlagsAChangeThatBypassesTheHelper(string js, int expected)
        => Assert.Equal(expected, ConsolePageProblems("index.html", js).Count());

    [Fact]
    public void TheHelperSourceIsShared_ByTheFourMauiApps_AndTheDesktopShellHasItsOwn()
    {
        var shared = Path.Combine("BeeMemoryBank.Mobile", "Services", "BusyAction.cs");
        Assert.True(File.Exists(Path.Combine(RepoRoot, "mobile", shared)));
        foreach (var app in new[] { "BlindMobile", "BlindIos", "FullIos" })
        {
            var project = File.ReadAllText(Path.Combine(RepoRoot, "mobile", $"BeeMemoryBank.{app}", $"BeeMemoryBank.{app}.csproj"));
            Assert.Contains(@"BeeMemoryBank.Mobile\Services\BusyAction.cs", project);
        }
        Assert.True(File.Exists(Path.Combine(RepoRoot, "desktop", "BeeMemoryBank.Desktop", "Views", "BusyAction.cs")));
    }
}
