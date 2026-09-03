using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Pins what actually happens to a WRITE when the vault is locked, against the real event logger.
///
/// <para>This matters because the classification of which tools need an unlocked session was
/// derived from what the article/media repositories touch — and that is not the whole story.
/// Every write funnels through <c>EventLogger.AppendEventAsync</c>, which SIGNS the event with the
/// node's Ed25519 key. On any node initialized after the key-wrapping change
/// (<c>Ed25519PrivateKeyV == 1</c>, which is every node <c>InitializationService</c> creates), that
/// signature needs the master DEK, so `session.GetMasterDek()` throws while locked. A soft delete
/// writes no ciphertext of its own and a metadata-only update re-encrypts nothing — but both log
/// an event, so both need the vault open regardless.</para>
///
/// <para>Tests that construct <c>ArticleService</c> with a <c>NullEventLogger</c> cannot see this:
/// the null logger no-ops, the write "succeeds while locked", and the test proves the opposite of
/// what production does. These tests deliberately go through the full application host so the real
/// logger is in play.</para>
/// </summary>
public class WriteWhileLockedTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string Password = "LockWritePass123";

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password }))
            .EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> CreateArticleAsync(string title)
    {
        var resp = await _client.PostAsJsonAsync("/api/articles", new
        {
            title,
            treePath = "/LockWrite",
            content = "body",
            tags = Array.Empty<string>()
        });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return Guid.Parse(body.GetProperty("id").GetString()!);
    }

    private async Task LockAsync()
    {
        var resp = await _client.PostAsync("/api/session/lock", null);
        // Some builds expose lock only to a superadmin web session; fall back to asserting the
        // session really is locked rather than assuming the endpoint shape.
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Could not lock the session for the test: {resp.StatusCode}");

        var status = await _client.GetAsync("/api/session/status");
        (await status.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isUnlocked").GetBoolean()
            .Should().BeFalse("the test needs a genuinely locked vault");
    }

    [Fact]
    public async Task SoftDelete_WhileLocked_DoesNotSilentlySucceed()
    {
        var id = await CreateArticleAsync("Delete While Locked");
        await LockAsync();

        var resp = await _client.DeleteAsync($"/api/articles/{id}");

        // The point is NOT which status code comes back — it is that the delete must not be
        // reported as done. Signing its delete event needs the master DEK, so on a locked vault
        // this cannot complete.
        resp.IsSuccessStatusCode.Should().BeFalse(
            "a soft delete still logs a signed event, and signing needs the master DEK");
    }

    [Fact]
    public async Task MetadataOnlyUpdate_WhileLocked_DoesNotSilentlySucceed()
    {
        var id = await CreateArticleAsync("Rename While Locked");
        await LockAsync();

        var resp = await _client.PutAsJsonAsync($"/api/articles/{id}", new
        {
            title = "Renamed While Locked"
        });

        resp.IsSuccessStatusCode.Should().BeFalse(
            "a title-only update writes no ciphertext, but it still logs a signed event and that " +
            "needs the master DEK");
    }
}
