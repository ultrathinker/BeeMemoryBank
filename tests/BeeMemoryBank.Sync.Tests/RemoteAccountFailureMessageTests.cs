using System.Net;
using System.Text;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// What a remote account says when the other node says no. A guest account works only if the other node's reverse
/// proxy forwards three routes to its API; when it does not, the other node answers 404/403/405 from its proxy or its
/// web page, and the guest used to read "Remote login failed (HTTP 404)". The messages are the product here: each
/// names what is wrong and what to do, and none of them repeats the other node's text or the address typed.
/// </summary>
public class RemoteAccountFailureMessageTests : SyncTestFixture
{
    private const string ProxyHtml = "<html><body>404 Not Found</body></html>";
    private const string ApiError404 = """{"error":"Folder /Shared not found","code":null}""";

    private static HttpResponseMessage Answer(HttpStatusCode status, string? body = null, string mediaType = "text/html") =>
        new(status) { Content = body == null ? new ByteArrayContent([]) : new StringContent(body, Encoding.UTF8, mediaType) };

    // ── the helper, on its own ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task TheSignInRoute_Answering404Or403Or405_MeansTheProxyDoesNotForwardIt_WhateverTheBody(HttpStatusCode status)
    {
        // The handler of /api/auth/remote-token answers 200, 400, 401 or 429 and nothing else, so even a JSON body is not its.
        foreach (var body in new[] { null, ProxyHtml, ApiError404 })
        {
            using var response = Answer(status, body, body == ApiError404 ? "application/json" : "text/html");

            (await RemoteAccountErrors.AreRoutesNotForwardedAsync(response, RemoteRoute.RemoteToken)).Should().BeTrue();
            (await RemoteAccountErrors.DescribeAsync(response, RemoteRoute.RemoteToken)).Should().Be(RemoteAccountErrors.RoutesNotForwarded);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Locked)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OtherStatuses_AreNotRoutesNotForwarded(HttpStatusCode status)
    {
        using var response = Answer(status, ProxyHtml);

        (await RemoteAccountErrors.AreRoutesNotForwardedAsync(response, RemoteRoute.RemoteToken)).Should().BeFalse();
        (await RemoteAccountErrors.AreRoutesNotForwardedAsync(response, RemoteRoute.FolderSnapshot)).Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task TheFolderRoutes_AnsweringWithAnApiErrorBody_AreARealAnswer_NotAMissingProxyRule(HttpStatusCode status)
    {
        // "Folder not found" and "Access denied" are the handler's own answers: access really was lost; the subscription is not misconfigured.
        using var response = Answer(status, ApiError404, "application/json");

        (await RemoteAccountErrors.AreRoutesNotForwardedAsync(response, RemoteRoute.FolderSnapshot)).Should().BeFalse();
        (await RemoteAccountErrors.AreRoutesNotForwardedAsync(response, RemoteRoute.AccessibleFolders)).Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, null, "text/html")]
    [InlineData(HttpStatusCode.NotFound, ProxyHtml, "text/html")]
    [InlineData(HttpStatusCode.Forbidden, "denied", "text/plain")]
    [InlineData(HttpStatusCode.NotFound, """{"message":"no such route"}""", "application/json")]
    [InlineData(HttpStatusCode.NotFound, "{ not json", "application/json")]
    [InlineData(HttpStatusCode.NotFound, """["error"]""", "application/json")]
    [InlineData(HttpStatusCode.MethodNotAllowed, ApiError404, "application/json")]
    public async Task TheFolderRoutes_AnsweringWithoutAnApiErrorBody_AreTheProxy(HttpStatusCode status, string? body, string mediaType)
    {
        using var response = Answer(status, body, mediaType);

        (await RemoteAccountErrors.AreRoutesNotForwardedAsync(response, RemoteRoute.FolderSnapshot)).Should().BeTrue();
        (await RemoteAccountErrors.DescribeAsync(response, RemoteRoute.AccessibleFolders)).Should().Be(RemoteAccountErrors.RoutesNotForwarded);
    }

    [Fact]
    public void TheMessage_NamesTheThreeRoutesAndTheDoc() =>
        RemoteAccountErrors.RoutesNotForwarded.Should().Be(
            "The other node does not let guest sign-ins through. Its reverse proxy must forward /api/auth/remote-token, " +
            "/api/folders/accessible and /api/folders/by-path/snapshot: see docs/internet-access.md");

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, RemoteRoute.RemoteToken, "The other node did not accept that user name and password.")]
    [InlineData(HttpStatusCode.TooManyRequests, RemoteRoute.RemoteToken, "The other node limits sign-in attempts (5 in 5 minutes). Wait a few minutes and try again.")]
    [InlineData(HttpStatusCode.Locked, RemoteRoute.AccessibleFolders, "The other node is locked: its owner has to unlock it first.")]
    [InlineData(HttpStatusCode.InternalServerError, RemoteRoute.RemoteToken, "Remote login failed (HTTP 500)")]
    [InlineData(HttpStatusCode.BadGateway, RemoteRoute.AccessibleFolders, "List accessible folders failed (HTTP 502)")]
    public async Task OtherFailures_KeepTheirOwnWords(HttpStatusCode status, RemoteRoute route, string expected)
    {
        using var response = Answer(status, "<html>secret-looking text from the other node</html>");

        var message = await RemoteAccountErrors.DescribeAsync(response, route);

        message.Should().Be(expected);
        message.Should().NotContain("secret-looking", "the other node's body is never passed on");
    }

    // ── through the service ──────────────────────────────────────────────────────────────────────

    /// <summary>Answers each call with whatever the test says; the first answer is a token so an account can be created.</summary>
    private sealed class Scripted : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"token":"bmbrt_t","expiresAt":"2030-01-01T00:00:00Z","userId":1,"username":"u"}""", Encoding.UTF8, "application/json"),
        };

        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Answer(request));
        }
    }

    private RemoteAccountService Service(Scripted handler) => new(
        new RemoteAccountRepository(Factory), new RemoteSubscriptionRepository(Factory),
        new FolderRepository(Factory, new CallerScopeHolder()), NodeRepo, Clock, Session,
        new HttpClient(handler), new CallerScopeHolder());

    private async Task UnlockedNodeAsync()
    {
        await InitService.InitializeAsync("admin", "TestNode", Password);
        await Session.UnlockAsync(Password);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task AddingAnAccount_WhenTheOtherNodesProxyDoesNotForwardSignIn_SaysSo(HttpStatusCode status)
    {
        await UnlockedNodeAsync();
        var handler = new Scripted { Answer = _ => Answer(status, ProxyHtml) };

        var act = () => Service(handler).CreateAsync("Friend", "https://bee.example.com", "guest", "pw");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(RemoteAccountErrors.RoutesNotForwarded);
        (await new RemoteAccountRepository(Factory).ListAllAsync()).Should().BeEmpty("a failed sign-in stores nothing");
    }

    [Fact]
    public async Task ChangingTheCredentials_WhenTheProxyDoesNotForwardSignIn_SaysSoToo()
    {
        await UnlockedNodeAsync();
        var handler = new Scripted();
        var service = Service(handler);
        var account = await service.CreateAsync("Friend", "https://bee.example.com", "guest", "pw");
        handler.Answer = _ => Answer(HttpStatusCode.NotFound, ProxyHtml);

        var act = () => service.RefreshCredentialsAsync(account.Id, "guest", "pw2");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(RemoteAccountErrors.RoutesNotForwarded);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, ProxyHtml, "text/html", true)]
    [InlineData(HttpStatusCode.MethodNotAllowed, null, "text/html", true)]
    [InlineData(HttpStatusCode.NotFound, ApiError404, "application/json", false)]
    public async Task ListingFolders_SaysTheProxyIsTheProblemOnlyWhenItIs(HttpStatusCode status, string? body, string mediaType, bool proxy)
    {
        await UnlockedNodeAsync();
        var handler = new Scripted();
        var service = Service(handler);
        var account = await service.CreateAsync("Friend", "https://bee.example.com", "guest", "pw");
        handler.Answer = _ => Answer(status, body, mediaType);

        var act = () => service.ListAccessibleAsync(account.Id);

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        if (proxy) message.Should().Be(RemoteAccountErrors.RoutesNotForwarded);
        else message.Should().Be("List accessible folders failed (HTTP 404)");
    }

    [Fact]
    public async Task WrongPassword_SaysSo_AndTooManyAttemptsSaysWait()
    {
        await UnlockedNodeAsync();
        var handler = new Scripted { Answer = _ => Answer(HttpStatusCode.Unauthorized, """{"error":"Invalid credentials"}""", "application/json") };
        var service = Service(handler);

        (await ((Func<Task>)(() => service.CreateAsync("F", "https://bee.example.com", "g", "x"))).Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be(RemoteAccountErrors.WrongCredentials);

        handler.Answer = _ => Answer(HttpStatusCode.TooManyRequests, null);
        (await ((Func<Task>)(() => service.CreateAsync("F", "https://bee.example.com", "g", "x"))).Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be(RemoteAccountErrors.TooManyAttempts);
    }

    [Fact]
    public async Task AnAddressThatDoesNotAnswer_SaysSo_WithoutTheExceptionText()
    {
        await UnlockedNodeAsync();
        var handler = new Scripted { Answer = _ => throw new HttpRequestException("No such host is known. (secret.internal:443)") };

        var act = () => Service(handler).CreateAsync("F", "https://secret.internal", "g", "x");

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Be(RemoteAccountErrors.Unreachable).And.NotContain("secret.internal");
    }

    // ── refused addresses: the person is told what to type instead ───────────────────────────────

    [Theory]
    [InlineData("http://bee.example.com")]
    [InlineData("http://203.0.113.9")]
    [InlineData("HTTP://bee.example.com:8080")]
    public async Task AnHttpAddress_IsRefusedWithUseAnHttpsName(string baseUrl)
    {
        await UnlockedNodeAsync();
        var handler = new Scripted();

        var act = () => Service(handler).CreateAsync("F", baseUrl, "g", "x");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(RemoteAccountErrors.UseHttpsName).And.Contain("https://");
        handler.Urls.Should().BeEmpty("a refused address is never called");
    }

    [Theory]
    [InlineData("https://192.168.1.20")]
    [InlineData("https://10.0.0.5:8443")]
    [InlineData("https://172.16.4.4")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://100.64.1.1")]
    [InlineData("https://[fd00::1]")]
    public async Task APrivateIpAddress_IsRefusedWithUseAnHttpsName_AndTheAddressIsNotEchoed(string baseUrl)
    {
        await UnlockedNodeAsync();
        var handler = new Scripted();

        var act = () => Service(handler).CreateAsync("F", baseUrl, "g", "x");

        var message = (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        message.Should().Be(RemoteAccountErrors.PrivateAddress).And.Contain("https://");
        handler.Urls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("bee.example.com")]
    [InlineData("ftp://bee.example.com")]
    [InlineData("")]
    public async Task ANonsenseAddress_IsToldHowToWriteOne(string baseUrl)
    {
        await UnlockedNodeAsync();

        var act = () => Service(new Scripted()).CreateAsync("F", baseUrl, "g", "x");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(RemoteAccountErrors.InvalidAddress);
    }

    [Theory]
    [InlineData("http://localhost:5300")]
    [InlineData("http://127.0.0.1:5300")]
    [InlineData("https://bee.example.com")]
    [InlineData("https://203.0.113.9")]
    public void AcceptedAddresses_StayAccepted(string baseUrl) =>
        ((Action)(() => RemoteAccountService.ValidateBaseUrl(baseUrl))).Should().NotThrow();
}
