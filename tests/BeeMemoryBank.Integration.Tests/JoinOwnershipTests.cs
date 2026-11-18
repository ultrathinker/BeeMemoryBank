using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Join attribution &amp; ownership rules on <c>/api/join</c>:
/// distinct superadmin slots attribute personal ownership to the opener; identical passwords without <c>login</c>
/// are ambiguous (<c>unknown</c> + <c>needs_review</c>); passing <c>login</c> disambiguates; legacy un-migrated
/// <c>password</c> slots are always ambiguous; <c>ownership: "shared"</c> sets shared; and <c>RemoteNode</c> returns
/// the host's own ownership from its event log.
/// </summary>
public class JoinOwnershipTests : IAsyncLifetime
{
    private BmbWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private const string SharedPassword = "SamePasswordAcrossAdmins1!";

    public async Task InitializeAsync()
    {
        _factory = new BmbWebApplicationFactory();
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync("HostNode", SharedPassword);

        using var scope = _factory.Services.CreateScope();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var admin = (await userRepo.GetByUsernameAsync("admin"))!;
        admin.DisplayName = "Primary Admin";
        await userRepo.UpdateAsync(admin);

        var unlock = await _client.PostAsJsonAsync("/api/session/unlock", new { Password = SharedPassword });
        unlock.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Single_opener_attributes_personal_ownership_and_returns_JoinedAsDisplayName()
    {
        var joinerId = Guid.NewGuid();
        var body = await JoinAsync(joinerId, "Alice Laptop", SharedPassword);

        body.JoinedAsDisplayName.Should().Be("Primary Admin");

        using var scope = _factory.Services.CreateScope();
        var wl = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var admin = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!;
        var row = (await wl.GetByNodeIdAsync(joinerId))!;
        row.Ownership.Should().Be(OwnershipKinds.Personal);
        row.OwnerPersonId.Should().Be(admin.PersonId);
        row.OwnerLabel.Should().Be("Primary Admin");
        row.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task Two_superadmins_sharing_password_are_ambiguous_without_login_and_attributed_with_login()
    {
        var secondUser = await AddSuperadminWithPasswordAsync("alice", "Alice Display", SharedPassword);

        // Without login: tries all slots, finds >1 match -> unknown + needs_review, JoinedAsDisplayName null.
        var ambiguousId = Guid.NewGuid();
        var ambResp = await JoinAsync(ambiguousId, "Unknown Box", SharedPassword);
        ambResp.JoinedAsDisplayName.Should().BeNull();

        using (var scope = _factory.Services.CreateScope())
        {
            var row = (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(ambiguousId))!;
            row.Ownership.Should().Be(OwnershipKinds.Unknown);
            row.OwnerPersonId.Should().BeNull();
            row.OwnerLabel.Should().BeNull();
            row.NeedsReview.Should().BeTrue();
        }

        // With login: tries alice's slot first -> attributed to Alice.
        var namedId = Guid.NewGuid();
        var namedResp = await JoinAsync(namedId, "Alice Phone", SharedPassword, login: "alice");
        namedResp.JoinedAsDisplayName.Should().Be("Alice Display");

        using (var scope = _factory.Services.CreateScope())
        {
            var row = (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(namedId))!;
            row.Ownership.Should().Be(OwnershipKinds.Personal);
            row.OwnerPersonId.Should().Be(secondUser.PersonId);
            row.OwnerLabel.Should().Be("Alice Display");
            row.NeedsReview.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Distinct_passwords_without_login_attribute_second_superadmin_cleanly()
    {
        var bobPassword = "DistinctBobPassword2!";
        var bob = await AddSuperadminWithPasswordAsync("bob", "Bob Builder", bobPassword);

        var bobDeviceId = Guid.NewGuid();
        var resp = await JoinAsync(bobDeviceId, "Bob Desktop", bobPassword);
        resp.JoinedAsDisplayName.Should().Be("Bob Builder");

        using var scope = _factory.Services.CreateScope();
        var row = (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(bobDeviceId))!;
        row.Ownership.Should().Be(OwnershipKinds.Personal);
        row.OwnerPersonId.Should().Be(bob.PersonId);
        row.OwnerLabel.Should().Be("Bob Builder");
        row.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task Legacy_password_slot_marks_join_unknown_and_needs_review()
    {
        const string legacyPw = "LegacySlotPassword9!";
        using (var scope = _factory.Services.CreateScope())
        {
            var keySlotRepo = scope.ServiceProvider.GetRequiredService<IKeySlotRepository>();
            var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var admin = (await userRepo.GetByUsernameAsync("admin"))!;
            var slots = await keySlotRepo.GetAllAsync();
            var existingUserSlot = slots.Single(s => s.SlotId == admin.KeySlotId);
            var kek = KeyDerivation.DeriveKek(SharedPassword, existingUserSlot.Salt!,
                existingUserSlot.ArgonMemory!.Value, existingUserSlot.ArgonIterations!.Value, existingUserSlot.ArgonParallelism!.Value);
            var dek = Doors.OpenSlot(existingUserSlot, kek);
            Array.Clear(kek);
            var legSalt = KeyDerivation.GenerateSalt();
            var (encDek, iv) = MasterKeyManager.WrapMasterDek(dek, KeyDerivation.DeriveKek(legacyPw, legSalt));
            Array.Clear(dek);

            await keySlotRepo.CreateAsync(new MasterKeyStore
            {
                SlotType = "password",
                EncryptedMasterDek = encDek,
                IV = iv,
                Salt = legSalt,
                ArgonMemory = CryptoConstants.DefaultArgonMemory,
                ArgonIterations = CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
                CreatedAt = DateTime.UtcNow,
            });
        }

        var legacyJoinerId = Guid.NewGuid();
        var resp = await JoinAsync(legacyJoinerId, "Legacy Joined", legacyPw);
        resp.JoinedAsDisplayName.Should().BeNull();

        using var checkScope = _factory.Services.CreateScope();
        var row = (await checkScope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(legacyJoinerId))!;
        row.Ownership.Should().Be(OwnershipKinds.Unknown);
        row.NeedsReview.Should().BeTrue();
    }

    [Fact]
    public async Task Shared_ownership_request_and_host_own_ownership_travel_in_JoinResponse()
    {
        var peerAuthor = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();
            var hostId = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
            var payload = JsonSerializer.Serialize(new WhitelistUpdatePayload(
                hostId, null, null, null, null, null, OwnershipKinds.Shared, null, null));
            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_event (event_id, node_id, event_type, lamport_ts, payload, signature, apply_state, created_at)
                  VALUES (@eid, @nid, @etype, 42, @payload, X'00', @state, @ts)",
                new
                {
                    eid = Guid.NewGuid().ToString("D"),
                    nid = peerAuthor.ToString("D"),
                    etype = EventTypes.WhitelistUpdate,
                    payload,
                    state = EventApplyStates.Applied,
                    ts = DateTime.UtcNow.ToString("O"),
                });
        }

        var serverId = Guid.NewGuid();
        var resp = await JoinAsync(serverId, "Shared Backup", SharedPassword, ownership: OwnershipKinds.Shared);
        resp.RemoteNode.Ownership.Should().Be(OwnershipKinds.Shared);
        resp.RemoteNode.OwnershipVersion.Should().NotBeNull();
        resp.RemoteNode.OwnershipVersion!.LamportTs.Should().Be(42);
        resp.Whitelist.Should().Contain(w => w.NodeId == serverId && w.Ownership == OwnershipKinds.Shared);

        using var verifyScope = _factory.Services.CreateScope();
        var row = (await verifyScope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(serverId))!;
        row.Ownership.Should().Be(OwnershipKinds.Shared);
        row.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task Opener_with_empty_person_id_marks_join_unknown_and_needs_review()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();
            using var conn = db.CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE tbl_user SET person_id = @empty WHERE username = 'admin'",
                new { empty = Guid.Empty.ToString("D") });
        }

        var joinerId = Guid.NewGuid();
        var resp = await JoinAsync(joinerId, "EmptyOpenerDevice", SharedPassword);
        resp.Whitelist.Should().Contain(w => w.NodeId == joinerId && w.Ownership == OwnershipKinds.Unknown && w.OwnerPersonId == null);

        using var checkScope = _factory.Services.CreateScope();
        var row = (await checkScope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(joinerId))!;
        row.Ownership.Should().Be(OwnershipKinds.Unknown);
        row.OwnerPersonId.Should().BeNull();
        row.OwnerLabel.Should().BeNull();
        row.NeedsReview.Should().BeTrue();
    }

    private async Task<User> AddSuperadminWithPasswordAsync(string username, string displayName, string password)
    {
        using var scope = _factory.Services.CreateScope();
        var keySlotRepo = scope.ServiceProvider.GetRequiredService<IKeySlotRepository>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        var firstSlot = (await keySlotRepo.GetAllAsync()).First(s => s.SlotType == "user");
        var firstKek = KeyDerivation.DeriveKek(SharedPassword, firstSlot.Salt!,
            firstSlot.ArgonMemory!.Value, firstSlot.ArgonIterations!.Value, firstSlot.ArgonParallelism!.Value);
        var masterDek = Doors.OpenSlot(firstSlot, firstKek);
        Array.Clear(firstKek);

        var salt = KeyDerivation.GenerateSalt();
        var userKek = KeyDerivation.DeriveKek(password, salt);
        var newSlot = Doors.NewPasswordSlot(
            KeySlotTypes.User,
            userKek,
            salt,
            CryptoConstants.DefaultArgonMemory,
            CryptoConstants.DefaultArgonIterations,
            CryptoConstants.DefaultArgonParallelism,
            masterDek);
        Array.Clear(userKek);
        Array.Clear(masterDek);

        var slotId = await keySlotRepo.CreateAsync(newSlot);

        var id = await userRepo.CreateAsync(new User
        {
            Username = username,
            DisplayName = displayName,
            Role = UserRoles.Superadmin,
            KeySlotId = slotId,
            CreatedAt = DateTime.UtcNow,
        });
        return (await userRepo.GetByIdAsync(id))!;
    }

    private async Task<JoinResponse> JoinAsync(Guid nodeId, string displayName, string password,
        string? login = null, string? ownership = null)
    {
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        var resp = await _client.PostAsJsonAsync("/api/join", new JoinRequest(
            MasterPassword: password,
            NodeId: nodeId,
            DisplayName: displayName,
            Ed25519PublicKeyB64: Convert.ToBase64String(publicKey),
            ProtocolVersion: SyncProtocolVersion.Current,
            Capabilities: MetadataCapabilities.Advertised.ToArray(),
            Login: login,
            Ownership: ownership));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<JoinResponse>())!;
    }
}
