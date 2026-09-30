using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.BlindConsole;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindConsole.Tests;

/// <summary>
/// The console's own gates: the page is public, everything else needs a session cookie, the
/// proxy only forwards ALLOWLISTED Api routes (the internal key must not be spendable on
/// anything the console was not built for), and a login succeeds exactly when the Api said ok.
/// The Api itself is faked — these tests pin the CONSOLE's behavior, not the Api's.
/// </summary>
public class BlindConsoleTests : IDisposable
{
    private sealed class FakeApi : IApiProxy
    {
        public List<(string Method, string Path, string? Body)> Calls { get; } = [];
        /// <summary>Answer for everything except the login route (a test that changes Answer for a
        /// status call must not also change what /login answers — LoginAsync depends on it).</summary>
        public (int, string) Answer { get; set; } = (200, "{}");
        public (int, string) LoginAnswer { get; set; } = (200, """{"ok":true}""");

        public Task<(int Status, string Content, string ContentType)> SendAsync(
            string method, string path, string? jsonBody, CancellationToken ct)
        {
            Calls.Add((method, path, jsonBody));
            var answer = path == "api/blind/console/login" ? LoginAnswer : Answer;
            return Task.FromResult((answer.Item1, answer.Item2, "application/json"));
        }
    }

    private readonly FakeApi _api = new();
    private readonly WebApplicationFactory<Program> _factory;

    public BlindConsoleTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("BMB_INTERNAL_KEY", "test-key");
            b.ConfigureTestServices(s => s.AddSingleton<IApiProxy>(_api));
        });
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>A client that behaves like the page: it sends the anti-CSRF header on every call.</summary>
    private HttpClient PageClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Bmb-Console", "1");
        return client;
    }

    private async Task<HttpClient> LoginAsync()
    {
        var client = PageClient();
        var resp = await client.PostAsJsonAsync("/login", new { password = "console-pw" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    [Fact]
    public async Task Page_IsServed_WithItsTitle()
    {
        var html = await (await _factory.CreateClient().GetAsync("/")).Content.ReadAsStringAsync();
        html.Should().Contain("BeeMemoryBank blind node");
    }

    /// <summary>
    /// The page is one inline script; a single syntax error in it leaves the operator a blank
    /// login form that does nothing, and no server-side test notices. `node --check` parses it
    /// without running it. Node is on every CI runner (ubuntu-latest) and the test hosts; the
    /// failure message says what is missing rather than passing silently without it.
    /// </summary>
    [Fact]
    public async Task Page_Script_Parses()
    {
        var html = await (await _factory.CreateClient().GetAsync("/")).Content.ReadAsStringAsync();
        var start = html.IndexOf("<script>", StringComparison.Ordinal) + "<script>".Length;
        var script = html[start..html.IndexOf("</script>", start, StringComparison.Ordinal)];
        var file = Path.Combine(Path.GetTempPath(), "bmb_console_page_" + Guid.NewGuid().ToString("N") + ".js");
        await File.WriteAllTextAsync(file, script);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("--check");
            psi.ArgumentList.Add(file);
            System.Diagnostics.Process? node;
            try { node = System.Diagnostics.Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception) { node = null; }
            node.Should().NotBeNull("this test needs Node.js on PATH to parse the console page script");
            var stderr = await node!.StandardError.ReadToEndAsync();
            await node.WaitForExitAsync();
            node.ExitCode.Should().Be(0, $"the console page script must parse: {stderr}");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Login_ForwardsToApiLogin_WithThePasswordOnly()
    {
        await LoginAsync();
        _api.Calls.Should().Contain(c => c.Method == "POST" && c.Path == "api/blind/console/login");
        _api.Calls.Single(c => c.Path == "api/blind/console/login").Body.Should().Contain("console-pw");
    }

    [Fact]
    public async Task Login_RefusedByApi_Answer_IsRefusedHere()
    {
        _api.LoginAnswer = (200, """{"ok":false,"locked":false}""");
        var client = PageClient();
        var resp = await client.PostAsJsonAsync("/login", new { password = "wrong" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resp.Headers.TryGetValues("Set-Cookie", out _).Should().BeFalse(
            "a refused login must not mint a session cookie");
    }

    [Fact]
    public async Task Proxy_WithoutSession_IsUnauthorized_AndForwardsNothing()
    {
        using var anon = _factory.CreateClient();
        var resp = await anon.GetAsync("/proxy/api/blind/status");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _api.Calls.Should().BeEmpty("the internal key must not be spent by an unauthenticated browser");
    }

    [Fact]
    public async Task Proxy_WithSession_ForwardsAllowedRoute_WithTheKeySideEffectIntact()
    {
        _api.Answer = (200, """{"node_name":"BlindNode","peers":[]}""");
        using var client = await LoginAsync();

        var resp = await client.GetAsync("/proxy/api/blind/status");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("node_name").GetString()
            .Should().Be("BlindNode");
    }

    [Theory]
    [InlineData("GET", "api/session/unlock")]
    [InlineData("POST", "api/init/reset")]
    [InlineData("GET", "api/blind/seed")]
    [InlineData("POST", "api/users")]
    [InlineData("POST", "api/blind/wipe/cli")] // the CLI's audit source must not be claimable from a browser
    public async Task Proxy_Allowlist_RefusesEverythingElse(string method, string path)
    {
        using var client = await LoginAsync();
        var resp = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/proxy/" + path));
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            $"{method} {path} is not a console route; the proxy is an allowlist, not a tunnel");
        _api.Calls.Should().NotContain(c => c.Path == path);
    }

    [Fact]
    public async Task Proxy_ForwardsPut_ForBackupSettings()
    {
        using var client = await LoginAsync();
        var resp = await client.PutAsJsonAsync("/proxy/api/blind/backup/settings", new { repoFolder = "/x" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _api.Calls.Should().Contain(c => c.Method == "PUT" && c.Path == "api/blind/backup/settings");
    }

    [Fact]
    public async Task StateChange_WithoutTheConsoleHeader_IsRefused_AndForwardsNothing()
    {
        // What a cross-site form post looks like: the session cookie rides along, the header does not.
        using var bare = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var login = new HttpRequestMessage(HttpMethod.Post, "/login") { Content = JsonContent.Create(new { password = "console-pw" }) };
        login.Headers.Add("X-Bmb-Console", "1");
        var session = (await bare.SendAsync(login)).Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        bare.DefaultRequestHeaders.Add("Cookie", session);
        (await bare.GetAsync("/proxy/api/blind/status")).StatusCode.Should().Be(HttpStatusCode.OK,
            "the cookie itself is valid — reads need no header");
        _api.Calls.Clear();

        (await bare.PostAsync("/proxy/api/blind/backup/now", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "another localhost port is the same SITE, so SameSite=Strict alone does not stop it");
        (await bare.PostAsJsonAsync("/login", new { password = "console-pw" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _api.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ForeignHostHeader_IsRefused()
    {
        using var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Host = "rebind.attacker.example";
        (await client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a DNS-rebound page reaches this port under its own hostname — it must not get the console");
    }

    [Fact]
    public async Task Page_CannotBeFramed()
    {
        var resp = await _factory.CreateClient().GetAsync("/");
        resp.Headers.GetValues("X-Frame-Options").Should().Contain("DENY");
        resp.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("frame-ancestors 'none'");
    }

    [Fact]
    public async Task Login_ForwardsTheBrowserAddress_ForTheJournal()
    {
        await LoginAsync();
        _api.Calls.Single(c => c.Path == "api/blind/console/login").Body.Should().Contain("\"remote\"");
    }

    [Fact]
    public async Task RestoreCode_NeedsThePasswordAgain_WrongOneForwardsNothing()
    {
        using var client = await LoginAsync();
        _api.LoginAnswer = (200, """{"ok":false,"locked":false}""");
        _api.Calls.Clear();

        var resp = await client.PostAsJsonAsync("/proxy/api/blind/restore-code", new { password = "wrong" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an unattended logged-in tab must not be enough to hand out the vault");
        _api.Calls.Should().NotContain(c => c.Path == "api/blind/restore-code");
    }

    [Fact]
    public async Task RestoreCode_RightPassword_IsForwarded_WithoutThePassword()
    {
        using var client = await LoginAsync();
        _api.Calls.Clear();

        (await client.PostAsJsonAsync("/proxy/api/blind/restore-code", new { password = "console-pw" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        _api.Calls.Should().ContainSingle(c => c.Path == "api/blind/restore-code")
            .Which.Body.Should().Be("{}", "the console password is the console's business, not the restore endpoint's");
    }

    // ── "Console password" (the page changes it; the current one is asked again) ─────────────────

    private const string PasswordRoute = "/proxy/api/blind/console/password";

    [Fact]
    public async Task PasswordChange_WrongCurrent_IsRefused_ForwardsNothing_ButTheAttemptWentToTheLogin()
    {
        using var client = await LoginAsync();
        _api.LoginAnswer = (200, """{"ok":false,"locked":false}""");
        _api.Calls.Clear();

        var resp = await client.PostAsJsonAsync(PasswordRoute, new { currentPassword = "wrong", newPassword = "brand-new-pw" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("wrong", "a bare 401 means the session ended; this one names the password");
        _api.Calls.Should().ContainSingle(c => c.Path == "api/blind/console/login",
            "the Api's login is what counts the attempt, journals it and locks after five");
        _api.Calls.Should().NotContain(c => c.Path == "api/blind/console/password");
    }

    [Fact]
    public async Task PasswordChange_WhileLocked_IsRefused_ForwardsNothing()
    {
        using var client = await LoginAsync();
        _api.LoginAnswer = (200, """{"ok":false,"locked":true}""");
        _api.Calls.Clear();

        (await client.PostAsJsonAsync(PasswordRoute, new { currentPassword = "console-pw", newPassword = "brand-new-pw" }))
            .StatusCode.Should().Be(HttpStatusCode.Locked);
        _api.Calls.Should().NotContain(c => c.Path == "api/blind/console/password");
    }

    [Fact]
    public async Task PasswordChange_TooShortNewPassword_IsRefused_BeforeAnyVerification()
    {
        using var client = await LoginAsync();
        _api.Calls.Clear();

        (await client.PostAsJsonAsync(PasswordRoute, new { currentPassword = "console-pw", newPassword = "short" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _api.Calls.Should().BeEmpty("a typo in the new password spends no attempt and reaches no route");
    }

    [Fact]
    public async Task PasswordChange_RightCurrent_VerifiesFirst_ThenForwardsBoth_AndEndsTheOtherSessions()
    {
        using var mine = await LoginAsync();
        using var other = await LoginAsync();
        _api.Answer = (204, "");
        _api.Calls.Clear();

        (await mine.PostAsJsonAsync(PasswordRoute, new { currentPassword = "console-pw", newPassword = "brand-new-pw" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        _api.Calls.Select(c => c.Path).Should().Equal("api/blind/console/login", "api/blind/console/password");
        using var sent = JsonDocument.Parse(_api.Calls[1].Body!);
        sent.RootElement.GetProperty("currentPassword").GetString().Should().Be("console-pw");
        sent.RootElement.GetProperty("newPassword").GetString().Should().Be("brand-new-pw");

        _api.Answer = (200, "{}");
        (await mine.GetAsync("/proxy/api/blind/status")).StatusCode.Should().Be(HttpStatusCode.OK,
            "the session that changed it stays valid");
        (await other.GetAsync("/proxy/api/blind/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "every other session ends with the old password");
    }

    [Fact]
    public async Task PasswordChange_RefusedByTheApi_KeepsTheOtherSessions()
    {
        using var mine = await LoginAsync();
        using var other = await LoginAsync();
        _api.Answer = (400, """{"error":"the current console password is wrong"}""");

        (await mine.PostAsJsonAsync(PasswordRoute, new { currentPassword = "console-pw", newPassword = "brand-new-pw" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        _api.Answer = (200, "{}");
        (await other.GetAsync("/proxy/api/blind/status")).StatusCode.Should().Be(HttpStatusCode.OK,
            "nothing changed, nobody is signed out");
    }

    [Fact]
    public async Task PasswordChange_WithoutASession_IsUnauthorized_AndForwardsNothing()
    {
        using var anon = PageClient();

        (await anon.PostAsJsonAsync(PasswordRoute, new { currentPassword = "console-pw", newPassword = "brand-new-pw" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _api.Calls.Should().BeEmpty("the internal key is not spent by an unauthenticated browser");
    }

    [Fact]
    public async Task Proxy_ForwardsTheOneOffCopy()
    {
        using var client = await LoginAsync();
        (await client.PostAsJsonAsync("/proxy/api/blind/backup/copy", new { destination = "/backups/usb" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        _api.Calls.Should().Contain(c => c.Method == "POST" && c.Path == "api/blind/backup/copy");
    }

    private sealed class HeaderRecorder : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    [Fact]
    public async Task ApiProxy_CallsTheApiAsTheNodesLocalSuperadmin()
    {
        var recorder = new HeaderRecorder();
        await new ApiProxy("http://api", "the-key", recorder).SendAsync("GET", "api/blind/status", null, CancellationToken.None);

        recorder.Last!.Headers.GetValues("X-Internal-Key").Should().Equal("the-key");
        recorder.Last.Headers.GetValues("X-User-Role").Should().Equal(["superadmin"],
            "every /api/blind route is .RequireSuperadmin(); the console's own login decides who acts through it");
    }

    [Fact]
    public async Task Logout_InvalidatesTheSession()
    {
        using var client = await LoginAsync();
        await client.PostAsync("/logout", null);
        (await client.GetAsync("/proxy/api/blind/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
