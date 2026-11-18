using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Crypto;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Release 2.12, F4: three things that hand somebody the vault, or a copy of it, are on the audit log with who did them and what they touched, and no entry
/// holds a secret: a recovery key issued, a snapshot downloaded, a node joined.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class SensitiveActionsAuditTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminAudit12345";
    private const string CarolPassword = "CarolAudit12345";

    private readonly BmbWebApplicationFactory _api = new();
    private HttpClient _machine = null!;
    private int _adminId;

    public async Task InitializeAsync()
    {
        _machine = _api.CreateClient();
        await _api.InitializeNodeAsync(displayName: "AuditNode", password: AdminPassword);
        (await _machine.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        using var scope = _api.Services.CreateScope();
        _adminId = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!.Id;
    }

    public Task DisposeAsync()
    {
        _machine.Dispose();
        ((IDisposable)_api).Dispose();
        return Task.CompletedTask;
    }

    /// <summary>The Web layer's request for the admin: internal key, the person's id, no role the API would read.</summary>
    private HttpClient AsAdmin()
    {
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Remove("X-User-Role");
        client.DefaultRequestHeaders.Add("X-User-Id", _adminId.ToString());
        return client;
    }

    private async Task<List<(string EntityType, string EntityId, string Action, string ActorType, string Details)>> AuditAsync(string action)
    {
        using var scope = _api.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return (await conn.QueryAsync<(string, string, string, string, string)>(
            "SELECT entity_type, entity_id, action, actor_type, details FROM tbl_audit_log WHERE action = @action ORDER BY id", new { action })).ToList();
    }

    private async Task<string> WholeAuditLogAsync()
    {
        using var scope = _api.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return string.Join("\n", await conn.QueryAsync<string>("SELECT entity_type || '|' || entity_id || '|' || action || '|' || actor_type || '|' || details FROM tbl_audit_log"));
    }

    // ---------------------------------------------------------------- a recovery key issued

    [Fact]
    public async Task IssuingARecoveryKey_IsOnRecord_WithTheWhoAndTheSlot_AndNeverTheKey()
    {
        using var admin = AsAdmin();

        var response = await admin.PostAsync("/api/keys/add-recovery", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var key = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryKey").GetString();
        key.Should().NotBeNullOrEmpty();
        var entries = await AuditAsync("recovery_key_issued");
        entries.Should().ContainSingle();
        entries[0].EntityType.Should().Be("key_slot");
        entries[0].Details.Should().Contain($"user {_adminId}").And.Contain($"slot #{entries[0].EntityId}");
        (await WholeAuditLogAsync()).Should().NotContain(key!, "the key is the one secret that is shown once; nothing that holds it is kept");
    }

    [Fact]
    public async Task ARecoveryKeyThatWasRefused_LeavesNoEntry()
    {
        (await _machine.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();
        using var admin = AsAdmin();

        (await admin.PostAsync("/api/keys/add-recovery", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await AuditAsync("recovery_key_issued")).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- a snapshot downloaded

    [Fact]
    public async Task DownloadingASnapshot_IsOnRecord_WithTheWhoAndTheFile()
    {
        using var admin = AsAdmin();
        var created = await admin.PostAsync("/api/snapshots", null);
        created.EnsureSuccessStatusCode();
        var fileName = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fileName").GetString()!;
        (await AuditAsync("snapshot_downloaded")).Should().BeEmpty("creating a snapshot is not taking a copy away");

        var download = await admin.GetAsync($"/api/snapshots/{fileName}/download");

        download.StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = await AuditAsync("snapshot_downloaded");
        entries.Should().ContainSingle();
        entries[0].EntityType.Should().Be("snapshot");
        entries[0].EntityId.Should().Be(fileName);
        entries[0].Details.Should().Contain($"user {_adminId}");
    }

    [Fact]
    public async Task ADownloadThatFindsNoFile_AndOneThatTheLockedNodeRefuses_LeaveNoEntry()
    {
        using var admin = AsAdmin();
        (await admin.GetAsync("/api/snapshots/no-such-snapshot.tar.gz/download")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await _machine.PostAsync("/api/session/lock", null)).EnsureSuccessStatusCode();
        (await admin.GetAsync("/api/snapshots/anything.tar.gz/download")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await AuditAsync("snapshot_downloaded")).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- a node joined

    private static object JoinBody(string password, Guid nodeId, byte[] publicKey, string displayName) => new
    {
        masterPassword = password,
        nodeId,
        displayName,
        ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
        apiAddress = (string?)null,
        protocolVersion = BeeMemoryBank.Sync.Metadata.EventClasses.SealedProtocol,
    };

    [Fact]
    public async Task AJoin_IsOnRecord_WithTheNodeTheNameAndWhoseSlotThePasswordOpened()
    {
        var carolCreated = await _machine.PostAsJsonAsync("/api/users", new
        {
            username = "carol", password = CarolPassword, displayName = "Carol", role = "superadmin", passwordHint = "the colour of the sea",
        });
        carolCreated.EnsureSuccessStatusCode();
        var carolId = (await carolCreated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        var nodeId = Guid.NewGuid();
        using var joiner = _api.CreateDefaultClient();   // a joining node has no internal key

        var response = await joiner.PostAsJsonAsync("/api/join", JoinBody(CarolPassword, nodeId, publicKey, "Carol's phone"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = await AuditAsync("node_joined");
        entries.Should().ContainSingle();
        entries[0].EntityType.Should().Be("whitelist");
        entries[0].EntityId.Should().Be(nodeId.ToString());
        entries[0].Details.Should().Contain("Carol's phone").And.Contain(nodeId.ToString())
            .And.Contain($"the slot of user 'carol' (#{carolId})").And.NotContain("re-joined");
        var log = await WholeAuditLogAsync();
        log.Should().NotContain(CarolPassword).And.NotContain(Convert.ToBase64String(publicKey), "no password and no key bytes in an entry");
    }

    [Fact]
    public async Task ARejoinWithTheSameKey_SaysSo_AndAWrongPasswordLeavesNoEntry()
    {
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        var nodeId = Guid.NewGuid();
        using var joiner = _api.CreateDefaultClient();

        (await joiner.PostAsJsonAsync("/api/join", JoinBody("not the password 1", nodeId, publicKey, "Phone"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await AuditAsync("node_joined")).Should().BeEmpty("a refused join joined nobody");

        (await joiner.PostAsJsonAsync("/api/join", JoinBody(AdminPassword, nodeId, publicKey, "Phone"))).EnsureSuccessStatusCode();
        (await joiner.PostAsJsonAsync("/api/join", JoinBody(AdminPassword, nodeId, publicKey, "Phone"))).EnsureSuccessStatusCode();

        var entries = await AuditAsync("node_joined");
        entries.Should().HaveCount(2);
        entries[0].Details.Should().Contain("joined").And.NotContain("re-joined").And.Contain("the slot of user 'admin'");
        entries[1].Details.Should().Contain("re-joined");
    }

    [Fact]
    public async Task ANameThePeerChose_CannotWriteALineOfItsOwnIntoTheLog()
    {
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        using var joiner = _api.CreateDefaultClient();

        (await joiner.PostAsJsonAsync("/api/join", JoinBody(AdminPassword, Guid.NewGuid(), publicKey, "Phone\r\nFAKE|entry|x" + new string('z', 300)))).EnsureSuccessStatusCode();

        var details = (await AuditAsync("node_joined")).Single().Details;
        details.Should().NotContain("\n").And.NotContain("\r");
        details.Length.Should().BeLessThan(400, "a name is cut at 100 characters");
    }
}
