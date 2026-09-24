using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Api.Models;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Regression tests for ProtectedUnlockCache.
///  - Finding M8: the cache must not survive a vault lock. Before the fix, nothing cleared the cache
///    when the session locked (SessionEndpoints only wiped the master DEK), so a protected article's
///    passphrase — once verified — kept working through the unlock cache's whole TTL even across an
///    explicit lock/unlock cycle, defeating the passphrase as a real second factor.
///  - The cache is scoped to one browser login (X-Web-Session), not to the user: unlocking in one
///    browser must not open the article in another browser or on another device signed in as the
///    same user.
/// </summary>
public class ProtectedArticleUnlockCacheTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string Password = "integrationPassword";
    private const string ArticlePassphrase = "correct-horse";

    public async Task InitializeAsync()
    {
        _client = BrowserClient("browser-a");
        await _factory.InitializeNodeAsync(password: Password);
    }

    // A Web-proxy client for one browser login: the Web app forwards a random per-sign-in id as
    // X-Web-Session (see InternalKeyHandler); the API keys the unlock cache by it.
    private HttpClient BrowserClient(string? webSession)
    {
        var client = _factory.CreateClient();
        if (webSession != null)
            client.DefaultRequestHeaders.Add("X-Web-Session", webSession);
        return client;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> CreateAndProtectArticleAsync()
    {
        var create = await _client.PostAsJsonAsync("/api/articles", new
        {
            title = "Protected Unlock Cache Test",
            treePath = "/Tests",
            content = "top secret plaintext"
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var article = await create.Content.ReadFromJsonAsync<ArticleResponse>();
        article.Should().NotBeNull();

        // Protecting an article ALSO primes the unlock cache with the just-proven passphrase
        // (see ArticleEndpoints' /protect handler) — exactly the state a real user leaves behind
        // after adding protection, without a separate /unlock round-trip.
        var protect = await _client.PostAsJsonAsync($"/api/articles/{article!.Id}/protect",
            new { passphrase = ArticlePassphrase });
        protect.StatusCode.Should().Be(HttpStatusCode.OK);

        return article.Id;
    }

    [Fact]
    public async Task EditContent_ServesCachedPlaintext_BeforeLock()
    {
        var unlock = await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password });
        unlock.EnsureSuccessStatusCode();

        var articleId = await CreateAndProtectArticleAsync();

        var editContent = await _client.GetAsync($"/api/articles/{articleId}/edit-content");
        editContent.EnsureSuccessStatusCode();
        var body = await editContent.Content.ReadFromJsonAsync<EditContentResponse>();

        body.Should().NotBeNull();
        body!.Protected.Should().BeTrue();
        body.Unlocked.Should().BeTrue("the passphrase was just verified via /protect and cached");
        body.Content.Should().Be("top secret plaintext");
    }

    [Fact]
    public async Task EditContent_NoLongerServesCachedPlaintext_AfterLockAndReunlock()
    {
        var unlock = await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password });
        unlock.EnsureSuccessStatusCode();

        var articleId = await CreateAndProtectArticleAsync();

        // Sanity check: cache hit works before locking (mirrors the previous test).
        var beforeLock = await _client.GetAsync($"/api/articles/{articleId}/edit-content");
        (await beforeLock.Content.ReadFromJsonAsync<EditContentResponse>())!.Unlocked.Should().BeTrue();

        // Explicit lock (CreateClient() already sends X-User-Role: superadmin, which /lock requires).
        var lockResp = await _client.PostAsync("/api/session/lock", null);
        lockResp.EnsureSuccessStatusCode();

        // Re-unlock the VAULT with the master password — this must NOT resurrect the article's
        // cached passphrase. Before the M8 fix it did, because nothing had cleared
        // ProtectedUnlockCache: only the master DEK was wiped and re-derived.
        var reunlock = await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password });
        reunlock.EnsureSuccessStatusCode();

        var afterUnlock = await _client.GetAsync($"/api/articles/{articleId}/edit-content");
        afterUnlock.EnsureSuccessStatusCode();
        var body = await afterUnlock.Content.ReadFromJsonAsync<EditContentResponse>();

        body.Should().NotBeNull();
        body!.Protected.Should().BeTrue();
        body.Unlocked.Should().BeFalse(
            "the lock must have cleared ProtectedUnlockCache — a passphrase verified before the " +
            "lock must not silently keep working after the vault is unlocked again");
        body.Content.Should().BeNull();
    }

    [Fact]
    public async Task Unlock_ReportsCountdown_AndEditContentReportsTheRemainingWindow()
    {
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        var articleId = await CreateAndProtectArticleAsync();

        var unlock = await _client.PostAsJsonAsync($"/api/articles/{articleId}/unlock",
            new { passphrase = ArticlePassphrase });
        unlock.EnsureSuccessStatusCode();
        var unlocked = await unlock.Content.ReadFromJsonAsync<UnlockArticleResponse>();
        unlocked!.Content.Should().Be("top secret plaintext");
        var ttlSeconds = (int)BeeMemoryBank.Api.Services.ProtectedUnlockCache.Ttl.TotalSeconds;
        ttlSeconds.Should().Be(15 * 60);
        unlocked.UnlockExpiresInSeconds.Should().BeInRange(ttlSeconds - 5, ttlSeconds);

        var edit = await (await _client.GetAsync($"/api/articles/{articleId}/edit-content"))
            .Content.ReadFromJsonAsync<EditContentResponse>();
        edit!.Unlocked.Should().BeTrue();
        edit.UnlockExpiresInSeconds.Should().BeInRange(1, ttlSeconds);
    }

    [Fact]
    public async Task UnlockInOneBrowser_DoesNotOpenTheArticleInAnotherBrowserOfTheSameUser()
    {
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        var articleId = await CreateAndProtectArticleAsync();
        (await _client.PostAsJsonAsync($"/api/articles/{articleId}/unlock",
            new { passphrase = ArticlePassphrase })).EnsureSuccessStatusCode();

        // Same user (same identity headers), different browser login.
        using var otherBrowser = BrowserClient("browser-b");
        var other = await (await otherBrowser.GetAsync($"/api/articles/{articleId}/edit-content"))
            .Content.ReadFromJsonAsync<EditContentResponse>();
        other!.Protected.Should().BeTrue();
        other.Unlocked.Should().BeFalse("the unlock happened in browser-a, not browser-b");
        other.Content.Should().BeNull();

        // Re-locking in browser-b must not affect browser-a's window either.
        (await otherBrowser.PostAsync($"/api/articles/{articleId}/relock", null)).EnsureSuccessStatusCode();
        var same = await (await _client.GetAsync($"/api/articles/{articleId}/edit-content"))
            .Content.ReadFromJsonAsync<EditContentResponse>();
        same!.Unlocked.Should().BeTrue("browser-a's own unlock is still within its window");
    }

    [Fact]
    public async Task RequestWithoutWebSession_IsNeverServedFromTheCache()
    {
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        var articleId = await CreateAndProtectArticleAsync();
        (await _client.PostAsJsonAsync($"/api/articles/{articleId}/unlock",
            new { passphrase = ArticlePassphrase })).EnsureSuccessStatusCode();

        // A browser request with no login-session id must not fall back to a user-wide entry.
        using var noSession = BrowserClient(null);
        var edit = await (await noSession.GetAsync($"/api/articles/{articleId}/edit-content"))
            .Content.ReadFromJsonAsync<EditContentResponse>();
        edit!.Unlocked.Should().BeFalse();

        var unlock = await noSession.PostAsJsonAsync($"/api/articles/{articleId}/unlock",
            new { passphrase = ArticlePassphrase });
        unlock.EnsureSuccessStatusCode();
        var body = await unlock.Content.ReadFromJsonAsync<UnlockArticleResponse>();
        body!.Content.Should().Be("top secret plaintext", "unlocking itself still works");
        body.UnlockExpiresInSeconds.Should().BeNull("nothing was cached, so there is no window to count down");

        var after = await (await noSession.GetAsync($"/api/articles/{articleId}/edit-content"))
            .Content.ReadFromJsonAsync<EditContentResponse>();
        after!.Unlocked.Should().BeFalse();
    }
}
