using System.Net;
using System.Net.Http.Json;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// M3 regression tests: <c>POST /api/chat/stream</c> used to have neither a rate nor a size
/// limit, even though AI chat is on by default for every user with access and can trigger several
/// OpenRouter calls per turn — any user could burn the admin's configured OpenRouter credits
/// without bound. Both new checks run BEFORE the vault-unlocked check (see MapStreamEndpoint), so
/// they're reachable and testable without a real OpenRouter key or an unlocked session.
/// </summary>
public class ChatStreamRateLimitTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private const string Password = "chatRateLimitTestPassword";

    public async Task InitializeAsync()
    {
        await _factory.InitializeNodeAsync(password: Password);
    }

    public Task DisposeAsync()
    {
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    private HttpClient CreateUserClient(int userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());
        return client;
    }

    [Fact]
    public async Task Stream_OverLongMessage_Returns400_BeforeAnyOtherCheck()
    {
        using var client = CreateUserClient(1);
        var hugeMessage = new string('a', 100_001); // MaxMessageLength + 1

        var resp = await client.PostAsJsonAsync("/api/chat/stream", new { message = hugeMessage });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Stream_ExceedsPerUserRateLimit_Returns429()
    {
        // Every call short-circuits at "Vault is locked" (409) once past the rate limiter — the
        // vault is deliberately left locked so this test needs no OpenRouter key/model config.
        // A distinct user id keeps this test's budget isolated from any other test sharing the
        // process-wide singleton limiter.
        using var client = CreateUserClient(1001);

        HttpResponseMessage? last = null;
        for (var i = 0; i < 30; i++)
        {
            last = await client.PostAsJsonAsync("/api/chat/stream", new { message = "hi" });
            last.StatusCode.Should().Be(HttpStatusCode.Conflict, "vault is locked, but that's AFTER the rate limiter — not yet exhausted");
        }

        var throttled = await client.PostAsJsonAsync("/api/chat/stream", new { message = "hi" });
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Stream_RateLimit_IsIsolatedPerUser()
    {
        using var userA = CreateUserClient(1002);
        using var userB = CreateUserClient(1003);

        for (var i = 0; i < 30; i++)
            await userA.PostAsJsonAsync("/api/chat/stream", new { message = "hi" });
        var userAThrottled = await userA.PostAsJsonAsync("/api/chat/stream", new { message = "hi" });
        userAThrottled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // A different user's budget must be untouched by user A exhausting theirs.
        var userBResp = await userB.PostAsJsonAsync("/api/chat/stream", new { message = "hi" });
        userBResp.StatusCode.Should().Be(HttpStatusCode.Conflict, "user B's own budget is still fresh");
    }
}
