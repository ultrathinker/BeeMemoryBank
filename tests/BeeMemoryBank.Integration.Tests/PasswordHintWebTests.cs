extern alias WebApp;

using System.Net;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-336 on the Sign In page: "Forgot your password?" offers the hint the person wrote, through the real Razor
/// page and the real Web->Api hop. The hint is text an administrator or a person typed, so the page must encode it.
/// </summary>
public sealed class PasswordHintWebTests : IAsyncLifetime
{
    private const string Password = "webHintPassword";
    private static readonly Regex TokenField = new("__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly BmbWebApplicationFactory _api = new();
    private readonly BmbWebHostFactory _web = new();
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _admin = _api.CreateClient();
        await _api.InitializeNodeAsync(password: Password);

        using (var scope = _api.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserService>();
            await users.CreateUserAsync("bob", "Bob", "BobPass1x", UserRoles.User,
                passwordHint: "the blue folder <script>alert('hint')</script>");
            await users.CreateUserAsync("carol", "Carol", "CarolPass1x", UserRoles.User);
        }

        _web.RouteOutboundHttpThrough(_api.Server.CreateHandler());
        WebApp::BeeMemoryBank.Web.Middleware.PublicRateLimitMiddleware.ResetPasswordHintForTests();
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        ((IDisposable)_api).Dispose();
        ((IDisposable)_web).Dispose();
        return Task.CompletedTask;
    }

    private async Task<HttpResponseMessage> PostHintAsync(string username)
    {
        // A fresh client per call, with the antiforgery cookie of the form page carried by hand: the page is
        // anonymous, so nothing else has to survive between calls.
        using var browser = _web.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var page = await browser.GetAsync("/Login");
        var html = await page.Content.ReadAsStringAsync();
        var token = TokenField.Match(html).Groups[1].Value;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/Login?handler=ShowHint")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["hintUsername"] = username,
                ["__RequestVerificationToken"] = token,
            })
        };
        var cookies = page.Headers.TryGetValues("Set-Cookie", out var setCookies)
            ? string.Join("; ", setCookies.Select(c => c.Split(';')[0]))
            : "";
        if (!string.IsNullOrEmpty(cookies))
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
        return await browser.SendAsync(request);
    }

    [Fact]
    public async Task TheSignInPage_OffersBothWaysBackIn()
    {
        using var page = await _web.CreateClient().GetAsync("/Login");
        var html = await page.Content.ReadAsStringAsync();

        html.Should().Contain("Forgot your password?");
        html.Should().Contain("Show my password hint");
        html.Should().Contain("Use a recovery key instead");
        html.Should().Contain("href=\"/RecoverAccess\"");
    }

    [Fact]
    public async Task TheHintIsShown_WithScriptLikeTextEncoded()
    {
        using var response = await PostHintAsync("bob");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("the blue folder");
        html.Should().Contain("&lt;script&gt;", "the hint is attacker-controlled text on a pre-authentication page");
        html.Should().NotContain("<script>alert('hint')</script>");
    }

    [Fact]
    public async Task AUserWithoutAHint_GetsTheOneGenericSentence()
    {
        using var response = await PostHintAsync("carol");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("No hint is available for this account. Use a recovery key, or ask an administrator to reset the password.");
        html.Should().NotContain("password-hint-result");
    }

    [Fact]
    public async Task AnUnknownUser_GetsTheSameSentenceAsAUserWithoutOne()
    {
        using var withoutHint = await PostHintAsync("carol");
        using var unknown = await PostHintAsync("nobody-at-all");

        var a = await withoutHint.Content.ReadAsStringAsync();
        var b = await unknown.Content.ReadAsStringAsync();
        const string sentence = "No hint is available for this account. Use a recovery key, or ask an administrator to reset the password.";

        // The page echoes the name the person typed into its form, which is not an answer about the account; what
        // must not differ is the answer itself. (The node's JSON is compared byte for byte in PasswordHintEndpointTests.)
        unknown.StatusCode.Should().Be(withoutHint.StatusCode).And.Be(HttpStatusCode.OK);
        a.Should().Contain(sentence);
        b.Should().Contain(sentence);
        a.Should().NotContain("password-hint-result");
        b.Should().NotContain("password-hint-result");
    }
}