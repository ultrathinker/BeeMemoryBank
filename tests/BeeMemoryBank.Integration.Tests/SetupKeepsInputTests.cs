using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A first-run form that comes back with an error keeps what was typed: the join code or the address, the username, the hint and the name of
/// this computer, and the way of joining chosen on the first join page. The passwords are never written back into the page; the mistakes
/// about them (two different passwords, a hint the node would refuse) are caught by the page script before the form is sent, so the page
/// is not reloaded for them at all.
/// </summary>
public sealed class SetupKeepsInputTests : IAsyncLifetime
{
    private const string Pin = "q7m1Xo2W3x0b7mB8pSxYl0Fh3y1rJk3n9gW2hQx1r2A";
    private const string Token = "2uTVKRgqdGVqPDxaGwLmhA";
    private const string Password = "keptFormPassword123";
    private const string OtherPassword = "differentPassword456";

    private static readonly Regex TokenField = new(
        "__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string SetupScript = File.ReadAllText(Path.Combine(
        RepoRoot, "server", "BeeMemoryBank.Web", "wwwroot", "js", "pages", "setup.js"));

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

    [Fact]
    public async Task AJoinWithTwoDifferentPasswords_KeepsTheCodeTheNamesAndTheHint_ButNoPassword()
    {
        var code = new JoinCode($"https://127.0.0.1:{FreePort()}", Token, Pin).ToString();

        var html = await PostAsync("Join", new()
        {
            ["joinWay"] = "code",
            ["joinCode"] = code,
            ["remoteUrl"] = "",
            ["joinAdminUsername"] = "carol",
            ["joinDisplayName"] = "Kept Laptop",
            ["joinPassword"] = Password,
            ["joinConfirmPassword"] = OtherPassword,
            ["passwordHint"] = "the blue folder",
        });

        html.Should().Contain("Passwords do not match.");
        html.Should().Contain("data-view=\"join-code\"", "the way chosen on the first join page stays chosen");
        ValueOf(html, "join", "joinCode").Should().Be(code);
        ValueOf(html, "join", "joinAdminUsername").Should().Be("carol");
        ValueOf(html, "join", "joinDisplayName").Should().Be("Kept Laptop");
        ValueOf(html, "join", "passwordHint").Should().Be("the blue folder");
        html.Should().NotContain(Password).And.NotContain(OtherPassword, "a password is never written back into the page");
    }

    [Fact]
    public async Task AJoinByAddress_ThatComesBack_KeepsTheAddress()
    {
        var html = await PostAsync("Join", new()
        {
            ["joinWay"] = "url",
            ["joinCode"] = "",
            ["remoteUrl"] = "https://notes.example.com",
            ["joinAdminUsername"] = "carol",
            ["joinDisplayName"] = "Kept Laptop",
            ["joinPassword"] = Password,
            ["joinConfirmPassword"] = OtherPassword,
            ["passwordHint"] = "the blue folder",
        });

        html.Should().Contain("data-view=\"join-url\"");
        ValueOf(html, "join", "remoteUrl").Should().Be("https://notes.example.com");
        ValueOf(html, "join", "joinAdminUsername").Should().Be("carol");
    }

    [Fact]
    public async Task AJoinTheOtherNodeRefuses_KeepsWhatWasTyped()
    {
        // A valid code to a closed port: the Api is asked, and its refusal comes back to the page.
        var code = new JoinCode($"https://127.0.0.1:{FreePort()}", Token, Pin).ToString();

        var html = await PostAsync("Join", new()
        {
            ["joinWay"] = "code",
            ["joinCode"] = code,
            ["remoteUrl"] = "",
            ["joinAdminUsername"] = "carol",
            ["joinDisplayName"] = "Kept Laptop",
            ["joinPassword"] = Password,
            ["joinConfirmPassword"] = Password,
            ["passwordHint"] = "the blue folder",
        });

        html.Should().Contain("The other computer did not answer");
        html.Should().Contain("data-view=\"join-code\"");
        ValueOf(html, "join", "joinCode").Should().Be(code);
        ValueOf(html, "join", "joinAdminUsername").Should().Be("carol");
        ValueOf(html, "join", "joinDisplayName").Should().Be("Kept Laptop");
        ValueOf(html, "join", "passwordHint").Should().Be("the blue folder");
        html.Should().NotContain(Password);
    }

    [Fact]
    public async Task CreatingWithTwoDifferentPasswords_KeepsTheNamesAndTheHint_ButNoPassword()
    {
        var html = await PostAsync("Standalone", new()
        {
            ["adminUsername"] = "carol",
            ["displayName"] = "Kept Desktop",
            ["password"] = Password,
            ["confirmPassword"] = OtherPassword,
            ["passwordHint"] = "the green folder",
        });

        html.Should().Contain("Passwords do not match.");
        html.Should().Contain("data-view=\"standalone\"");
        ValueOf(html, "standalone", "adminUsername").Should().Be("carol");
        ValueOf(html, "standalone", "displayName").Should().Be("Kept Desktop");
        ValueOf(html, "standalone", "passwordHint").Should().Be("the green folder");
        html.Should().NotContain(Password).And.NotContain(OtherPassword);
    }

    [Fact]
    public async Task AFirstVisit_StillOffersTheSuggestions()
    {
        using var browser = _web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var html = await (await browser.GetAsync("/Setup")).Content.ReadAsStringAsync();

        ValueOf(html, "standalone", "adminUsername").Should().Be(SetupSuggestedUsername);
        ValueOf(html, "join", "joinAdminUsername").Should().Be(SetupSuggestedUsername);
        ValueOf(html, "join", "joinCode").Should().BeEmpty();
    }

    [Fact]
    public async Task TheHintFields_CarryThePolicysLimits_ForThePageScript()
    {
        using var browser = _web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var html = await (await browser.GetAsync("/Setup")).Content.ReadAsStringAsync();

        var limits = $"data-hint-min=\"{PasswordHintPolicy.MinimumLength}\" data-hint-max=\"{PasswordHintPolicy.MaximumLength}\"";
        Regex.Matches(html, Regex.Escape(limits)).Count.Should().Be(2, "both hint fields (create and join) carry the policy's limits");
        html.Should().Contain($"{PasswordHintPolicy.MinimumLength} to {PasswordHintPolicy.MaximumLength} characters.");
    }

    [Fact]
    public void ThePageScript_ChecksThePasswordsAndTheHint_BeforeTheFormIsSent()
    {
        SetupScript.Should().Contain("setCustomValidity", "a mistake blocks the send in the browser, so the page is not reloaded");
        SetupScript.Should().Contain("'Passwords do not match.'", "the same sentence as the node's");
        SetupScript.Should().Contain("data-hint-min").And.Contain("data-hint-max", "the hint's limits come from the page, i.e. from PasswordHintPolicy");
        SetupScript.Should().Contain("The password hint must not contain the password.")
            .And.Contain("The password hint must not contain the username.");
        SetupScript.Should().Contain("form.addEventListener('submit', check, true)", "checked again right before Shoelace's own submit check");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private const string SetupSuggestedUsername = "admin";

    /// <summary>
    /// The value the page gives the input named <paramref name="name"/> in the <paramref name="form"/> form ("standalone" or "join"; the
    /// restore forms earlier on the page have inputs of the same names), decoded, or null when it has none.
    /// </summary>
    private static string? ValueOf(string html, string form, string name)
    {
        var start = html.IndexOf($"data-setup-form=\"{form}\"", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, $"the page has the {form} form");
        var tag = Regex.Match(html[start..], $"<sl-input name=\"{Regex.Escape(name)}\"[^>]*>");
        tag.Success.Should().BeTrue($"the page has an input named {name}");
        var value = Regex.Match(tag.Value, "\\bvalue=\"([^\"]*)\"");
        return value.Success ? WebUtility.HtmlDecode(value.Groups[1].Value) : null;
    }

    private async Task<string> PostAsync(string handler, Dictionary<string, string> fields)
    {
        using var browser = _web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var form = await browser.GetAsync("/Setup");
        fields["__RequestVerificationToken"] = TokenField.Match(await form.Content.ReadAsStringAsync()).Groups[1].Value;

        using var post = new HttpRequestMessage(HttpMethod.Post, $"/Setup?handler={handler}")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        post.Headers.TryAddWithoutValidation("Cookie", string.Join("; ",
            form.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));

        using var answer = await browser.SendAsync(post);
        answer.StatusCode.Should().Be(HttpStatusCode.OK, "a refused form is shown again with the reason");
        return await answer.Content.ReadAsStringAsync();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
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
