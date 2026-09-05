using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Joining a node that is initialized but still LOCKED must fail with a clear, actionable answer,
/// not the bare "Session is locked" 403 that used to surface from deep inside LogWhitelistAddAsync
/// (recording the new peer signs a whitelist_add event, which needs the host's master DEK). Found
/// on the test mesh: a fresh standalone node accepts /api/init/standalone but stays locked, so the
/// second node's join failed with an error pointing at the wrong end of the connection.
/// </summary>
public class JoinLockedNodeTests : IAsyncLifetime
{
    private BmbWebApplicationFactory _host = null!;
    private HttpClient _client = null!;
    private const string MasterPassword = "lockedJoinPassword123";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        _host = new BmbWebApplicationFactory();
        // Initialize but deliberately do NOT unlock — this is the state a fresh standalone node is
        // in right after /api/init/standalone.
        await _host.InitializeNodeAsync("LockedHost", MasterPassword);
        _client = _host.CreateClient();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _host.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Join_AgainstALockedHost_Returns409_WithAnActionableMessage()
    {
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();

        var resp = await _client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = MasterPassword,
            nodeId = Guid.NewGuid(),
            displayName = "Joiner",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null
        }, JsonOpts);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "the password is valid, but a locked host cannot sign the membership event");

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var error = body.GetProperty("error").GetString();
        error.Should().Contain("locked");
        error.Should().Contain("nlock", "the message tells the operator what to do — unlock and retry");
    }

    [Fact]
    public async Task Join_WithAWrongPassword_StillReturns401_NotTheLockedMessage()
    {
        // The lock guard must sit AFTER password validation, so a wrong password is still a plain
        // 401 and never leaks that the host happens to be locked.
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();

        var resp = await _client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = "the-wrong-password",
            nodeId = Guid.NewGuid(),
            displayName = "Joiner",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null
        }, JsonOpts);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
