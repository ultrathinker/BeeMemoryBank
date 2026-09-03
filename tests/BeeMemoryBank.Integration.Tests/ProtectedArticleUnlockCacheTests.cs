using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Api.Models;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Regression tests for finding M8: ProtectedUnlockCache must not survive a vault lock. Before
/// the fix, nothing cleared the cache when the session locked (SessionEndpoints only wiped the
/// master DEK), so a protected article's passphrase — once verified — kept working through the
/// unlock cache's whole TTL even across an explicit lock/unlock cycle, defeating the passphrase
/// as a real second factor.
/// </summary>
public class ProtectedArticleUnlockCacheTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string Password = "integrationPassword";
    private const string ArticlePassphrase = "correct-horse";

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
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
}
