using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-313 fix round, F6: the check of a node makes this node call out, and it is asked by a SCRIPT of a page (a double-click or the "Check now" button), not
/// by a form. So the proxy checks, before anything is forwarded, that the request carries the anti-forgery token of a page of this site in the
/// <c>RequestVerificationToken</c> header (and the cookie it pairs with): a request without it, with a wrong one, or with a token of another browser
/// never reaches the Api.
/// </summary>
[Collection(ProxyForwarderCollection.Name)]
public sealed class SyncCheckAntiforgeryTests(ProxyForwarderFixture fx)
{
    private static readonly Regex Token = new("__RequestVerificationToken[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private static string SomeNode() => $"/api-proxy/sync/nodes/{Guid.NewGuid()}/check";

    /// <summary>A browser of the signed-in admin: the session cookie, and what a page it opened gave it (the anti-forgery cookie and the token of the page).</summary>
    private async Task<(HttpClient Client, string Cookies, string Token)> BrowserAsync()
    {
        var client = fx.Web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var session = string.Join("; ", fx.Admin.DefaultRequestHeaders.GetValues("Cookie"));

        using var page = new HttpRequestMessage(HttpMethod.Get, "/Tree");
        page.Headers.TryAddWithoutValidation("Cookie", session);
        using var response = await client.SendAsync(page);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = Token.Match(await response.Content.ReadAsStringAsync());
        token.Success.Should().BeTrue("every signed-in page renders the logout form, which carries the token the script reads");
        var cookies = response.Headers.TryGetValues("Set-Cookie", out var set)
            ? string.Join("; ", set.Select(c => c.Split(';')[0]).Where(p => p.Contains('=')))
            : "";
        return (client, string.Join("; ", new[] { session, cookies }.Where(c => c.Length > 0)), token.Groups[1].Value);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string cookies, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SomeNode()) { Content = new StringContent("", Encoding.UTF8) };
        request.Headers.TryAddWithoutValidation("Cookie", cookies);
        if (token is not null) request.Headers.TryAddWithoutValidation("RequestVerificationToken", token);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task A_check_without_the_token_is_refused_by_the_proxy_and_never_reaches_the_api()
    {
        var (client, cookies, _) = await BrowserAsync();
        var before = fx.ApiCalls.Count;

        using var response = await PostAsync(client, cookies, token: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("could not be verified");
        fx.ApiCalls.Count.Should().Be(before, "nothing was forwarded");
    }

    [Fact]
    public async Task A_check_with_a_token_that_is_not_one_is_refused()
    {
        var (client, cookies, _) = await BrowserAsync();
        var before = fx.ApiCalls.Count;

        using var response = await PostAsync(client, cookies, token: "not-a-token");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fx.ApiCalls.Count.Should().Be(before);
    }

    [Fact]
    public async Task The_token_of_a_page_without_the_cookie_that_pairs_with_it_is_refused()
    {
        var (client, _, token) = await BrowserAsync();
        var before = fx.ApiCalls.Count;
        var sessionOnly = string.Join("; ", fx.Admin.DefaultRequestHeaders.GetValues("Cookie"));

        using var response = await PostAsync(client, sessionOnly, token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a token is only good with the cookie of the browser it was issued to");
        fx.ApiCalls.Count.Should().Be(before);
    }

    [Fact]
    public async Task A_check_with_the_token_of_a_page_and_its_cookie_is_forwarded()
    {
        var (client, cookies, token) = await BrowserAsync();
        var before = fx.ApiCalls.Count;

        using var response = await PostAsync(client, cookies, token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the Api answered: no such node in the whitelist (a random id)");
        (await response.Content.ReadAsStringAsync()).Should().Contain("unknown_node");
        fx.ApiCalls.Count.Should().BeGreaterThan(before, "this one was forwarded");
    }

    [Fact]
    public async Task The_script_sends_the_token_and_every_row_has_a_button_that_can_be_reached_without_a_mouse()
    {
        var script = await fx.Admin.GetStringAsync("/js/site-sync-status.js");

        script.Should().Contain("'RequestVerificationToken'").And.Contain("input[name=\"__RequestVerificationToken\"]");
        script.Should().Contain("sync-check-btn").And.Contain("data-check-node").And.Contain("aria-label=\"Check ");
        script.Should().Contain("<button type=\"button\" class=\"sync-check-btn\"", "a real button: focus, Enter, Space and a tap work without a double-click")
            .And.Contain("dblclick", "the double-click stays as a shortcut");
    }
}
