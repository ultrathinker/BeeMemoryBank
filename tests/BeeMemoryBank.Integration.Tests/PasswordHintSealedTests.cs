using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.PreUnlock;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-336, the case the feature exists for: every node set up since 2.6.2 is encrypted and CLOSED after a restart, and the
/// person who forgot the password needs the hint exactly then. The lookup reads the derived pre-unlock mirror, makes no database
/// connection and needs no key. The plain-node path (a locked session over an always-readable database) is
/// PasswordHintEndpointTests.
/// </summary>
public sealed class PasswordHintSealedTests
{
    private const string Password = "sealedHintPassword1";
    private const string Hint = "the blue folder by the window";

    private static string NewDir() => Path.Combine(Path.GetTempPath(), "bmb_hint_sealed_" + Guid.NewGuid().ToString("N"));

    /// <summary>The real setup path: /api/init/standalone writes the vault and seals it, then the host stops.</summary>
    private static async Task<string> SetupSealedNodeAsync(string dir)
    {
        using var first = new BmbWebApplicationFactory(dir) { KeepData = true };
        using var client = first.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/init/standalone", new
        {
            adminUsername = "admin", displayName = "Sealed node", password = Password, passwordHint = Hint,
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return dir;
    }

    private static async Task<(string? Hint, string Body)> LookupAsync(HttpClient client, string username, string ip)
    {
        using var response = await client.PostAsJsonAsync("/api/session/password-hint", new { username, clientIp = ip });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var parsed = JsonSerializer.Deserialize<PasswordHintResponse>(body, JsonSerializerOptions.Web)!;
        return (parsed.Hint, body);
    }

    private static async Task UnlockAsync(SealedApiFactory node, HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/session/unlock", new { password = Password });
        response.EnsureSuccessStatusCode();
        // The unlock publishes Importing before it returns; wait for the post-unlock rewrite before a lock can race it.
        var until = DateTime.UtcNow.AddSeconds(10);
        while (node.Services.GetRequiredService<IVaultGate>().State != VaultState.Open && DateTime.UtcNow < until)
            await Task.Delay(50);
    }

    private static async Task LockAsync(SealedApiFactory node, HttpClient client)
    {
        using var response = await client.PostAsync("/api/session/lock", content: null);
        response.EnsureSuccessStatusCode();
        node.Services.GetRequiredService<IVaultGate>().State.Should().Be(VaultState.Closed);
        node.Services.GetRequiredService<SessionService>().IsUnlocked.Should().BeFalse();
    }

    [Fact]
    public async Task ASealedNodeRestarted_AnswersTheAdminsHintWhileClosed_WithoutOpeningTheDatabase()
    {
        var dir = NewDir();
        await SetupSealedNodeAsync(dir);

        using var node = new SealedApiFactory(dir);
        using var client = node.CreateClient();

        node.Services.GetRequiredService<IVaultGate>().State.Should().Be(VaultState.Closed, "a sealed node starts closed after a restart");
        node.Services.GetRequiredService<SessionService>().IsUnlocked.Should().BeFalse();
        node.Counting.Should().NotBeNull();
        node.Counting!.Requests.Should().Be(0, "nothing has opened a connection yet");

        var (hint, _) = await LookupAsync(client, "admin", "10.30.0.1");

        hint.Should().Be(Hint);
        node.Counting.Requests.Should().Be(0, "the closed lookup reads only the pre-unlock mirror");
        node.Services.GetRequiredService<IVaultGate>().State.Should().Be(VaultState.Closed);
    }

    [Fact]
    public async Task TheMirrorFollowsAUserCreatedChangedAndDeletedAfterTheSetup()
    {
        var dir = NewDir();
        await SetupSealedNodeAsync(dir);

        using var node = new SealedApiFactory(dir);
        using var client = node.CreateClient();

        await UnlockAsync(node, client);
        using var created = await client.PostAsJsonAsync("/api/users", new
        {
            username = "bob", displayName = "Bob", password = "BobPass1x", role = "user", passwordHint = "the first hint",
        });
        created.EnsureSuccessStatusCode();
        var bobId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        await LockAsync(node, client);
        (await LookupAsync(client, "bob", "10.30.0.2")).Hint.Should().Be("the first hint", "a user created later is mirrored at creation");

        await UnlockAsync(node, client);
        (await client.PostAsJsonAsync($"/api/users/{bobId}/change-password",
            new { newPassword = "BobPass2x", passwordHint = "the second hint" })).EnsureSuccessStatusCode();
        await LockAsync(node, client);
        (await LookupAsync(client, "bob", "10.30.0.3")).Hint.Should().Be("the second hint", "a replaced hint reaches the mirror");

        await UnlockAsync(node, client);
        (await client.DeleteAsync($"/api/users/{bobId}")).EnsureSuccessStatusCode();
        await LockAsync(node, client);
        (await LookupAsync(client, "bob", "10.30.0.4")).Hint.Should().BeNull("a deleted user gets the generic answer");

        // An account without a hint (the state of every account that existed before hints, after an upgrade) is simply absent.
        await UnlockAsync(node, client);
        using (var scope = node.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<UserService>().CreateUserAsync("carol", "Carol", "CarolPass1x", UserRoles.User);
        await LockAsync(node, client);
        (await LookupAsync(client, "carol", "10.30.0.5")).Hint.Should().BeNull();
    }

    [Fact]
    public async Task ACorruptOrMissingMirror_IsAnsweredGenerically_AndDoesNotStopTheUnlock_AndIsRebuilt()
    {
        var dir = NewDir();
        await SetupSealedNodeAsync(dir);
        var mirrorPath = Path.Combine(dir, PasswordHintFile.FileName);
        File.Exists(mirrorPath).Should().BeTrue("the setup that sealed the node wrote the mirror");

        using var node = new SealedApiFactory(dir);
        using var client = node.CreateClient();

        await File.WriteAllTextAsync(mirrorPath, "a half write, or a disk that lost the rest");
        var (corruptHint, corruptBody) = await LookupAsync(client, "admin", "10.30.0.6");
        corruptHint.Should().BeNull();
        corruptBody.Should().NotContain("Exception").And.NotContain("   at ", "a damaged derived file is not a stack trace for the browser");

        File.Delete(mirrorPath);
        (await LookupAsync(client, "admin", "10.30.0.7")).Hint.Should().BeNull("a missing mirror is the generic answer, not an error");

        await UnlockAsync(node, client);
        await LockAsync(node, client);

        (await LookupAsync(client, "admin", "10.30.0.8")).Hint.Should().Be(Hint, "the lock rewrites the mirror from the database");
    }
}