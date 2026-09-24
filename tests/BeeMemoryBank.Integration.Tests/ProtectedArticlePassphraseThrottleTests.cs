using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Api.Models;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Wrong passphrases against one protected article are throttled per caller: each attempt costs a
/// full Argon2id derivation, so unthrottled guessing is both a brute-force channel and a way to
/// drain the node. A correct passphrase clears the budget, so ordinary use never trips it.
/// </summary>
public class ProtectedArticlePassphraseThrottleTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string Password = "throttleTestPassword";
    private const string Passphrase = "correct-horse";

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Guid> CreateProtectedArticleAsync()
    {
        var create = await _client.PostAsJsonAsync("/api/articles",
            new { title = "Throttle", treePath = "/Tests", content = "secret" });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await create.Content.ReadFromJsonAsync<ArticleResponse>())!.Id;
        (await _client.PostAsJsonAsync($"/api/articles/{id}/protect", new { passphrase = Passphrase }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        return id;
    }

    private Task<HttpResponseMessage> UnlockAsync(Guid id, string passphrase) =>
        _client.PostAsJsonAsync($"/api/articles/{id}/unlock", new { passphrase });

    [Fact]
    public async Task WrongPassphrases_AreThrottled_After10()
    {
        var id = await CreateProtectedArticleAsync();

        for (var i = 0; i < 10; i++)
            (await UnlockAsync(id, "wrong-" + i)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await UnlockAsync(id, "wrong-again")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        // Even the right passphrase waits out the window: otherwise the throttle would be an oracle.
        (await UnlockAsync(id, Passphrase)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task CorrectPassphrase_ResetsTheBudget()
    {
        var id = await CreateProtectedArticleAsync();

        for (var i = 0; i < 9; i++)
            (await UnlockAsync(id, "wrong-" + i)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await UnlockAsync(id, Passphrase)).StatusCode.Should().Be(HttpStatusCode.OK);

        for (var i = 0; i < 9; i++)
            (await UnlockAsync(id, "wrong-" + i)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await UnlockAsync(id, Passphrase)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
