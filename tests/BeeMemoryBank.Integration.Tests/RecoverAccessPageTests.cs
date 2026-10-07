extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using WebApp::BeeMemoryBank.Web.Middleware;
using WebApp::BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The /RecoverAccess page and the link to it on the Sign In page (BMB-156), driven the way a browser
/// does it: a Web host whose ApiClient is routed into a real API host, antiforgery token and cookie, no
/// sign-in. What matters here is what the page promises a person who cannot sign in — it is reachable,
/// it never writes a secret back, it says the same thing for every "no", success does not sign anyone in,
/// and a guesser is stopped at the Web layer before the node is even asked.
/// </summary>
public sealed class RecoverAccessPageTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminPass123";
    private const string NewPassword = "NewAdminPass456";
    private const string Refusal = "The username or the recovery key is not correct.";

    private static readonly Regex TokenField = new(
        "__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private readonly CountingHandler _apiCalls;
    private HttpClient _apiClient = null!;
    private HttpClient _browser = null!;
    private string _recoveryKey = null!;

    public RecoverAccessPageTests()
    {
        _apiCalls = new CountingHandler(_api.Server.CreateHandler());
    }

    public async Task InitializeAsync()
    {
        _apiClient = _api.CreateClient();
        await _api.InitializeNodeAsync(password: AdminPassword);
        (await _apiClient.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        var issued = await _apiClient.PostAsync("/api/keys/add-recovery", null);
        issued.EnsureSuccessStatusCode();
        _recoveryKey = (await issued.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("recoveryKey").GetString()!;

        _web.RouteOutboundHttpThrough(_apiCalls);
        _browser = _web.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        PublicRateLimitMiddleware.ResetRecoverAccessForTests();
    }

    public Task DisposeAsync()
    {
        PublicRateLimitMiddleware.ResetRecoverAccessForTests();
        _browser.Dispose();
        _apiClient.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _recoverCalls;
        public int RecoverCalls => Volatile.Read(ref _recoverCalls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.AbsolutePath == "/api/session/recover-access") Interlocked.Increment(ref _recoverCalls);
            return base.SendAsync(request, ct);
        }
    }

    private static string CookieHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? string.Join("; ", cookies.Select(c => c.Split(';')[0]).Where(p => p.Contains('=')))
            : "";

    /// <summary>GET the form for its antiforgery pair, then POST <paramref name="fields"/> with them.</summary>
    private async Task<HttpResponseMessage> PostFormAsync(string url, Dictionary<string, string> fields, string? extraCookies = null)
    {
        using var form = await _browser.GetAsync(url);
        form.EnsureSuccessStatusCode();
        var token = TokenField.Match(await form.Content.ReadAsStringAsync());
        token.Success.Should().BeTrue("the page renders an antiforgery token");

        fields["__RequestVerificationToken"] = token.Groups[1].Value;
        using var post = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
        var cookies = string.Join("; ", new[] { CookieHeader(form), extraCookies }.Where(c => !string.IsNullOrEmpty(c)));
        if (cookies.Length > 0) post.Headers.TryAddWithoutValidation("Cookie", cookies);
        return await _browser.SendAsync(post);
    }

    private Task<HttpResponseMessage> PostRecoverAsync(string username, string key, string password = NewPassword, string? repeat = null) =>
        PostFormAsync("/RecoverAccess", new Dictionary<string, string>
        {
            ["Username"] = username,
            ["RecoveryKey"] = key,
            ["NewPassword"] = password,
            ["ConfirmPassword"] = repeat ?? password,
        });

    private async Task<HttpResponseMessage> ApiLoginAsync(string username, string password) =>
        await _apiClient.PostAsJsonAsync("/api/session/login", new { username, password });

    // ── reaching it ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSignInPage_OffersTheRecoveryLink()
    {
        using var page = await _browser.GetAsync("/Login");

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        html.Should().Contain("Forgot your password? Use a recovery key");
        html.Should().Contain("href=\"/RecoverAccess\"");
    }

    [Fact]
    public async Task TheRecoveryPage_IsReachableWithoutSigningIn_AndAsksForFourThings()
    {
        using var page = await _browser.GetAsync("/RecoverAccess");

        page.StatusCode.Should().Be(HttpStatusCode.OK, "a person who cannot sign in has to be able to open it");
        var html = await page.Content.ReadAsStringAsync();
        foreach (var field in new[] { "username", "recoveryKey", "newPassword", "confirmPassword" })
            html.Should().Contain($"name=\"{field}\"");
        html.Should().Contain("type=\"password\"", "the key and the passwords are not shown on screen");
    }

    [Fact]
    public async Task TheRecoveryPage_IsReachableOnALockedVault_Too()
    {
        (await _apiClient.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();

        using var page = await _browser.GetAsync("/RecoverAccess");

        page.StatusCode.Should().Be(HttpStatusCode.OK, "the usual reason to need it is a node that restarted locked");
    }

    // ── the happy path ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARightKey_ChangesThePassword_ShowsTheDoneScreen_AndSignsNobodyIn()
    {
        (await _apiClient.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();

        using var post = await PostRecoverAsync("admin", _recoveryKey);

        post.StatusCode.Should().Be(HttpStatusCode.Redirect, "post-redirect-get: the done screen cannot be re-submitted");
        post.Headers.Location!.OriginalString.Should().StartWith("/RecoverAccess").And.Contain("done=");
        post.Headers.TryGetValues("Set-Cookie", out var setCookies);
        (setCookies ?? []).Should().NotContain(c => c.StartsWith("bee_session=", StringComparison.OrdinalIgnoreCase),
            "the page changes the password; it does not sign anyone in");

        using var done = await _browser.GetAsync(post.Headers.Location);
        var html = await done.Content.ReadAsStringAsync();
        html.Should().Contain("Password changed").And.Contain("Older recovery keys still work")
            .And.Contain("Issue new recovery key").And.Contain("/Login?recovered=1");

        (await ApiLoginAsync("admin", AdminPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var web = await WebLogin.LoginAsync(_web, "admin", NewPassword);
        web.Dispose();
    }

    [Fact]
    public async Task AWebSessionOfTheResetUser_DiesAtItsNextRevalidation()
    {
        using var signedIn = await WebLogin.LoginAsync(_web, "admin", AdminPassword);
        using (var before = await signedIn.GetAsync("/Tree"))
            before.StatusCode.Should().Be(HttpStatusCode.OK, "the cookie is valid before the reset");

        (await PostRecoverAsync("admin", _recoveryKey)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        // The Web layer remembers a user's security stamp for a few minutes (that is the bound on how stale a
        // revocation can be); clear that memory the way the passing of time would, then the old cookie is judged
        // against the node's new stamp.
        string userId;
        using (var scope = _api.Services.CreateScope())
            userId = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!.Id.ToString();
        _web.Services.GetRequiredService<IMemoryCache>().Remove(WebSignIn.StampCacheKey(userId));

        using var after = await signedIn.GetAsync("/Tree");
        after.StatusCode.Should().Be(HttpStatusCode.Redirect, "the reset ended the session");
        after.Headers.Location!.OriginalString.Should().StartWith("/Login");
    }

    [Fact]
    public async Task SigningInAfterARecovery_ShowsThePromptToIssueANewRecoveryKey_Once()
    {
        (await PostRecoverAsync("admin", _recoveryKey)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        // The done screen links to /Login?recovered=1; the form carries the marker through the sign-in.
        var loginPage = await PostFormAsync("/Login", new Dictionary<string, string>
        {
            ["Username"] = "admin",
            ["Password"] = NewPassword,
            ["ReturnUrl"] = "/Tree",
            ["Recovered"] = "true",
        });
        loginPage.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var sessionAndTempData = CookieHeader(loginPage);
        sessionAndTempData.Should().Contain("bee_session=");

        using var tree = new HttpRequestMessage(HttpMethod.Get, "/Tree");
        tree.Headers.TryAddWithoutValidation("Cookie", sessionAndTempData);
        using var first = await _browser.SendAsync(tree);
        (await first.Content.ReadAsStringAsync()).Should().Contain("issue a new one")
            .And.Contain("Issue new recovery key");
    }

    // ── what it never does ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryRefusal_ShowsTheSameSentence_AndNeverEchoesASecret()
    {
        const string secretPassword = "TypedSecretPass99";
        var wrongKey = Convert.ToBase64String(new byte[32]);

        foreach (var (user, key) in new[] { ("admin", wrongKey), ("ghost", _recoveryKey), ("ghost", wrongKey) })
        {
            using var resp = await PostRecoverAsync(user, key, secretPassword);
            resp.StatusCode.Should().Be(HttpStatusCode.OK, "a refusal re-renders the form");
            var html = await resp.Content.ReadAsStringAsync();
            html.Should().Contain(Refusal);
            html.Should().NotContain(wrongKey).And.NotContain(_recoveryKey).And.NotContain(secretPassword);
        }

        (await ApiLoginAsync("admin", AdminPassword)).StatusCode.Should().Be(HttpStatusCode.OK, "nothing was changed");
    }

    [Fact]
    public async Task MismatchedPasswords_AndWeakPasswords_AreSaidBeforeAnyRequestToTheNode()
    {
        using var mismatch = await PostRecoverAsync("admin", _recoveryKey, NewPassword, repeat: "NewAdminPass457");
        (await mismatch.Content.ReadAsStringAsync()).Should().Contain("do not match");

        using var weak = await PostRecoverAsync("admin", _recoveryKey, "short");
        (await weak.Content.ReadAsStringAsync()).Should().Contain("at least 8 characters");

        using var blank = await PostRecoverAsync("admin", "");
        (await blank.Content.ReadAsStringAsync()).Should().Contain("fill in all four fields");

        _apiCalls.RecoverCalls.Should().Be(0, "a typo costs the node nothing and the owner no attempt");
        (await ApiLoginAsync("admin", AdminPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── the Web layer's own budget ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AGuesser_IsStoppedAtTheWebLayer_AfterFiveAttempts_BeforeTheNodeIsAsked()
    {
        var wrongKey = Convert.ToBase64String(new byte[32]);
        for (var i = 0; i < 5; i++)
        {
            using var resp = await PostRecoverAsync($"ghost{i}", wrongKey);
            resp.StatusCode.Should().Be(HttpStatusCode.OK, $"attempt {i + 1} is within the budget");
        }
        var callsBefore = _apiCalls.RecoverCalls;

        using var sixth = await PostRecoverAsync("ghost-new", wrongKey);

        sixth.StatusCode.Should().Be((HttpStatusCode)429);
        (await sixth.Content.ReadAsStringAsync()).Should().Contain("Too many attempts");
        _apiCalls.RecoverCalls.Should().Be(callsBefore, "the throttle answers before the node is asked");

        using var form = await _browser.GetAsync("/RecoverAccess");
        form.StatusCode.Should().Be(HttpStatusCode.OK, "only POSTs are counted; the page itself stays readable");
    }

    [Fact]
    public async Task ASuccessfulReset_ClearsTheAddressBudget()
    {
        var wrongKey = Convert.ToBase64String(new byte[32]);
        for (var i = 0; i < 4; i++)
            (await PostRecoverAsync($"ghost{i}", wrongKey)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await PostRecoverAsync("admin", _recoveryKey)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        for (var i = 0; i < 5; i++)
            (await PostRecoverAsync($"again{i}", wrongKey)).StatusCode.Should().Be(HttpStatusCode.OK,
                "the owner who got back in has a fresh budget");
    }
}
