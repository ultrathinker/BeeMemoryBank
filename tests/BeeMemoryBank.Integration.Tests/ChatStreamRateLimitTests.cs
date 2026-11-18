using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    /// A client for a REAL user, as the Web layer sends one: the API reads the caller's role from its own table (a forwarded id that names nobody is not signed in),
    /// so each test has the user it rate-limits. The limiter is a process-wide singleton keyed by user id, and ids restart at 1 in every database, so the row is
    /// written with an id of its own (the high ids these tests always used) to keep the budget isolated from every other test in the process.
    /// </summary>
    private async Task<HttpClient> CreateUserClientAsync(int userId)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Interfaces.IDbConnectionFactory>();
            using var conn = db.CreateConnection();
            await Dapper.SqlMapper.ExecuteAsync(conn,
                @"INSERT INTO tbl_user (id, username, display_name, password_hash, role, is_active, created_at, security_stamp, chat_access)
                  VALUES (@userId, @name, @name, 'not-a-hash', 'user', 1, @now, 'stamp', 1)",
                new { userId, name = "limit-user-" + userId, now = DateTime.UtcNow.ToString("o") });
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-User-Role");
        client.DefaultRequestHeaders.Add("X-User-Role", "user");
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());
        return client;
    }

    [Fact]
    public async Task Stream_OverLongMessage_Returns400_BeforeAnyOtherCheck()
    {
        using var client = await CreateUserClientAsync(1000);
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
        using var client = await CreateUserClientAsync(1001);

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
        using var userA = await CreateUserClientAsync(1002);
        using var userB = await CreateUserClientAsync(1003);

        for (var i = 0; i < 30; i++)
            await userA.PostAsJsonAsync("/api/chat/stream", new { message = "hi" });
        var userAThrottled = await userA.PostAsJsonAsync("/api/chat/stream", new { message = "hi" });
        userAThrottled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // A different user's budget must be untouched by user A exhausting theirs.
        var userBResp = await userB.PostAsJsonAsync("/api/chat/stream", new { message = "hi" });
        userBResp.StatusCode.Should().Be(HttpStatusCode.Conflict, "user B's own budget is still fresh");
    }
}
