using System.Net;
using System.Text.RegularExpressions;
using BeeMemoryBank.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// "Open an existing profile" on the Setup page. The card asks the desktop app for a native folder picker, so
/// it is shown only inside the app (Windows WebView2, or the Mac app, which names itself in its user agent).
/// In a browser and in Docker the card never shows: the form it used to open always refused, because the
/// node is running. The shell half (the address becomes a picker) is Desktop's ShellCommandsTests.
/// </summary>
public sealed class SetupExistingProfileCardTests : IAsyncLifetime
{
    private static readonly Regex TokenField = new(
        "__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private static readonly string SetupScript = File.ReadAllText(Path.Combine(
        FindRepoRoot(), "server", "BeeMemoryBank.Web", "wwwroot", "js", "pages", "setup.js"));

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();

    public Task InitializeAsync()
    {
        using var _ = _api.CreateClient(); // builds the API host; the node stays uninitialized
        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    private async Task<string> SetupPageAsync()
    {
        using var browser = _web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var page = await browser.GetAsync("/Setup");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        return await page.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task TheCard_IsRenderedHidden_SoABrowserAndDockerNeverShowIt()
    {
        var html = await SetupPageAsync();

        html.Should().MatchRegex(@"<div data-desktop-only hidden>\s*<button class=""mode-card"" id=""btn-mode-existing""",
            "the card sits inside the wrapper that only the desktop app reveals (a hidden attribute on the card itself would lose to .mode-card { display:flex })");
        // The three other ways to start stay visible everywhere.
        foreach (var id in new[] { "btn-mode-standalone", "btn-mode-join" })
            html.Should().MatchRegex(@"<button class=""mode-card"" id=""" + id + "\"", id + " is not behind the desktop-only wrapper");
        html.Should().Contain("href=\"/Setup?step=restore\"", "restoring from a blind node works in a browser too");
    }

    [Fact]
    public async Task TheTrayHint_IsDesktopOnlyToo_AndNamesTheMenuPath()
    {
        var html = await SetupPageAsync();

        html.Should().MatchRegex(@"<p data-desktop-only hidden[^>]*>[^<]*Profiles[^<]*Add existing profile");
    }

    [Fact]
    public async Task NothingOnThePage_LeadsToTheFolderForm_AnyMore()
    {
        var html = await SetupPageAsync();

        // The form panel stays in the markup for a direct POST's error, but it starts closed and no control opens it.
        html.Should().MatchRegex(@"class=""wizard-panel "" id=""panel-legacy""");
        SetupScript.Should().NotMatchRegex(@"panelLegacy\.classList\.add",
            "no script may open the browser copy form: it always refused while the node runs");
    }

    [Fact]
    public void TheScript_ShowsDesktopOnlyControls_ForTheWindowsAppAndForTheMacApp_AndOnlyThose()
    {
        // Windows: WebView2's object. Mac: WKWebView has none, so the shell names itself in the user agent.
        SetupScript.Should().Contain("window.chrome && window.chrome.webview");
        SetupScript.Should().Contain($"navigator.userAgent.indexOf('{DesktopShellCommands.UserAgentToken}')",
            "the page and the Mac shell (MainWindow.OnWebViewEnvironmentRequested) must agree on the token");
        SetupScript.Should().MatchRegex(@"if \(inDesktopShell\) \{\s*document\.querySelectorAll\('\[data-desktop-only\]'\)",
            "the reveal is conditional on the shell check");
    }

    [Fact]
    public void TheCardsButton_NavigatesToTheAddressTheShellAnswersWithTheFolderPicker() =>
        SetupScript.Should().Contain($"window.location.href = '{DesktopShellCommands.OpenExistingProfile}'");

    [Fact]
    public void TheUserAgentToken_IsAWordNoBrowserSends() =>
        DesktopShellCommands.UserAgentToken.Should().MatchRegex("^[A-Za-z]+$").And.Be("BeeMemoryBankDesktop");

    [Fact]
    public async Task APostedBlindBackupFolder_IsStillForwardedToTheRestoreWizard()
    {
        // The unattended case of the old form (a script posting a blind node's backup folder): kept, not a profile to copy.
        var dir = Path.Combine(Path.GetTempPath(), "bmb-setup-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "node.recovery-set.json"), "{}");

        using var browser = _web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var form = await browser.GetAsync("/Setup");
        var token = TokenField.Match(await form.Content.ReadAsStringAsync()).Groups[1].Value;

        using var post = new HttpRequestMessage(HttpMethod.Post, "/Setup?handler=Migrate")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["sourcePath"] = dir,
                ["__RequestVerificationToken"] = token,
            }),
        };
        post.Headers.TryAddWithoutValidation("Cookie", string.Join("; ",
            form.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));

        using var answer = await browser.SendAsync(post);

        answer.StatusCode.Should().Be(HttpStatusCode.Redirect);
        answer.Headers.Location!.OriginalString.Should().StartWith("/Setup?step=restore&backup=");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}
