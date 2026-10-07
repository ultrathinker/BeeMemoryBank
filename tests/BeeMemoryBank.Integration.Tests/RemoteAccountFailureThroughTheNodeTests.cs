using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A guest's remote account through the real node: the other node (scripted here) answers like a reverse proxy
/// that does not forward the guest routes, and the Remote Accounts page shows the sentence that says what to fix
/// instead of "HTTP 404". The background poller and the add-account endpoint are the two places it is seen.
/// </summary>
public sealed class RemoteAccountFailureThroughTheNodeTests : IAsyncLifetime
{
    private const string Password = "RemoteTestPass1";
    private const string ProxyHtml = "<html><body>404 Not Found</body></html>";

    private readonly BmbWebApplicationFactory _node = new();
    private readonly OtherNode _other = new();
    private HttpClient _client = null!;

    /// <summary>The owner's side: whatever the test wants a proxy or an API to answer, per route.</summary>
    private sealed class OtherNode : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = Token;

        public static HttpResponseMessage Token(HttpRequestMessage _) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"token":"bmbrt_t","expiresAt":"2030-01-01T00:00:00Z","userId":1,"username":"guest"}""", Encoding.UTF8, "application/json"),
        };

        public static HttpResponseMessage Html(HttpStatusCode status) =>
            new(status) { Content = new StringContent(ProxyHtml, Encoding.UTF8, "text/html") };

        public static HttpResponseMessage ApiError(HttpStatusCode status, string text) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(new { error = text }), Encoding.UTF8, "application/json") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(Answer(request));
    }

    public async Task InitializeAsync()
    {
        _node.RouteOutboundHttpThrough(_other);
        _client = _node.CreateClient();
        await _node.InitializeNodeAsync(password: Password);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        ((IDisposable)_node).Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> AddAccountAsync()
    {
        _other.Answer = OtherNode.Token;
        using var created = await _client.PostAsJsonAsync("/api/remote-accounts",
            new { displayName = "Friend", baseUrl = "https://bee.example.com", username = "guest", password = "pw" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task SubscribeAsync(Guid accountId, string mountPath)
    {
        using var sub = await _client.PostAsJsonAsync("/api/remote-accounts/subscriptions", new
        {
            remoteAccountId = accountId, remoteFolderId = Guid.NewGuid(), remoteFolderPath = "/Shared", mountPath,
        });
        sub.EnsureSuccessStatusCode();
    }

    private async Task<(string Status, string? Error)> PollAsync(Guid accountId)
    {
        var scheduler = new RemoteAccountSyncScheduler(_node.Services, NullLogger<RemoteAccountSyncScheduler>.Instance);
        await scheduler.RunOnceAsync(CancellationToken.None);

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/remote-accounts");
        var account = list.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == accountId);
        return (account.GetProperty("lastSyncStatus").GetString()!,
            account.GetProperty("lastError").ValueKind == JsonValueKind.Null ? null : account.GetProperty("lastError").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task APollAnsweredByAProxy_SaysTheGuestRoutesAreNotForwarded_NotAccessLost(HttpStatusCode status)
    {
        var account = await AddAccountAsync();
        await SubscribeAsync(account, "/Mirror");
        _other.Answer = _ => OtherNode.Html(status);

        var (state, error) = await PollAsync(account);

        state.Should().Be("error");
        error.Should().Be(RemoteAccountErrors.RoutesNotForwarded);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "Folder /Shared not found")]
    [InlineData(HttpStatusCode.Forbidden, "Access denied")]
    public async Task APollAnsweredByTheOwnersApi_StillMeansAccessLost(HttpStatusCode status, string text)
    {
        var account = await AddAccountAsync();
        await SubscribeAsync(account, "/Mirror");
        _other.Answer = _ => OtherNode.ApiError(status, text);

        var (state, error) = await PollAsync(account);

        state.Should().Be("access_lost");
        error.Should().Contain($"HTTP {(int)status}").And.Contain("detach");
    }

    [Fact]
    public async Task APollOfALockedOwner_SaysTheOwnerHasToUnlock()
    {
        var account = await AddAccountAsync();
        await SubscribeAsync(account, "/Mirror");
        _other.Answer = _ => OtherNode.ApiError(HttpStatusCode.Locked, "Owner session is locked");

        var (state, error) = await PollAsync(account);

        state.Should().Be("error");
        error.Should().Be(RemoteAccountErrors.OwnerLocked);
    }

    [Fact]
    public async Task AddingAnAccount_ThroughAProxyThatDoesNotForwardSignIn_ShowsTheSentence()
    {
        _other.Answer = _ => OtherNode.Html(HttpStatusCode.NotFound);

        using var response = await _client.PostAsJsonAsync("/api/remote-accounts",
            new { displayName = "Friend", baseUrl = "https://bee.example.com", username = "guest", password = "pw" });

        response.IsSuccessStatusCode.Should().BeFalse();
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Be(RemoteAccountErrors.RoutesNotForwarded);
    }

    [Theory]
    [InlineData("http://bee.example.com", RemoteAccountErrors.UseHttpsName)]
    [InlineData("https://192.168.0.7", RemoteAccountErrors.PrivateAddress)]
    public async Task AddingAnAccount_AtARefusedAddress_TellsThePersonToUseAnHttpsName(string baseUrl, string expected)
    {
        using var response = await _client.PostAsJsonAsync("/api/remote-accounts",
            new { displayName = "Friend", baseUrl, username = "guest", password = "pw" });

        response.IsSuccessStatusCode.Should().BeFalse();
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        error.Should().Be(expected).And.Contain("https://");
    }

    [Fact]
    public async Task TheOtherNodeNotAnswering_IsAnAnswerToo_NotAServerError()
    {
        _other.Answer = _ => throw new HttpRequestException("Connection refused");

        using var response = await _client.PostAsJsonAsync("/api/remote-accounts",
            new { displayName = "Friend", baseUrl = "https://bee.example.com", username = "guest", password = "pw" });

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Be(RemoteAccountErrors.Unreachable);
    }
}
