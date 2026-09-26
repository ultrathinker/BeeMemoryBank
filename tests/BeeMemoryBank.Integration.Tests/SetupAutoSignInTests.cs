using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// First-run setup through the real Razor page: the form comes pre-filled with "admin" and the
/// computer's name, and creating the memory bank signs the user in and opens the app, instead of
/// a "done" screen followed by the same login again.
/// </summary>
public sealed class SetupAutoSignInTests : IAsyncLifetime
{
    private static readonly Regex TokenField = new(
        "__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

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
    public async Task CreatingANewMemoryBank_SignsInAndOpensTheTree()
    {
        using var browser = _web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var form = await browser.GetAsync("/Setup");
        form.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await form.Content.ReadAsStringAsync();
        html.Should().Contain("value=\"admin\"", "the username comes pre-filled");
        html.Should().Contain($"value=\"{Environment.MachineName}\"", "the computer's own name is the suggested name");
        html.Should().NotContain("step-dot", "there is no step indicator any more");
        html.Should().Contain(".mode-card {", "the three start cards keep their styling");

        using var post = new HttpRequestMessage(HttpMethod.Post, "/Setup?handler=Standalone")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["adminUsername"] = "admin",
                ["displayName"] = "Test PC",
                ["password"] = "SetupPass123",
                ["confirmPassword"] = "SetupPass123",
                ["__RequestVerificationToken"] = TokenField.Match(html).Groups[1].Value,
            }),
        };
        post.Headers.TryAddWithoutValidation("Cookie", Cookies(form));

        using var created = await browser.SendAsync(post);

        created.StatusCode.Should().Be(HttpStatusCode.Redirect,
            "setup must finish; page: " + await created.Content.ReadAsStringAsync());
        created.Headers.Location!.OriginalString.Should().Be("/Tree");
        var session = created.Headers.GetValues("Set-Cookie").Where(c => c.StartsWith("bee_session=")).ToList();
        session.Should().NotBeEmpty("the new admin is signed in right away");

        using var tree = new HttpRequestMessage(HttpMethod.Get, "/Tree");
        tree.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", session.Select(c => c.Split(';')[0])));
        using var opened = await browser.SendAsync(tree);
        opened.StatusCode.Should().Be(HttpStatusCode.OK, "the tree opens without another login");
    }

    private static string Cookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? string.Join("; ", cookies.Select(c => c.Split(';')[0]))
            : "";
}
