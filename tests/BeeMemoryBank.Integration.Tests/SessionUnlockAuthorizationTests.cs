using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// POST /api/session/unlock must accept only credentials that are actually allowed to unlock the
/// shared, process-wide vault session (SessionService.IsUnlocked — one flag for the whole node):
/// a superadmin's own password, or a recovery key (which belongs to no user account at all). It
/// previously accepted the password for ANY row in tbl_key_slot, including — in principle — an
/// ordinary user's "user" slot.
///
/// <para>
/// Under the current invariants (see UserService.CreateUserAsync / RewrapOrProvisionKeySlotAsync /
/// ProvisionMissingKeySlotAsync) a "user"-type slot is only ever created for, and only ever kept
/// for, a superadmin — a demotion deletes the slot immediately (UpdateUserAsync). So a non-
/// superadmin holding a "user" slot cannot happen through any normal code path today. These tests
/// build that state directly (bypassing UserService) to exercise the defence-in-depth check added
/// in SessionService.UnlockCoreAsync for exactly this scenario — a future bug, a hand-edited
/// database row, or an import from an untrusted source must not be able to reintroduce it.
/// </para>
/// </summary>
public class SessionUnlockAuthorizationTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string AdminPassword = "AdminPass123";

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: AdminPassword);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Unlock_WithSuperadminPassword_Succeeds()
    {
        var resp = await _client.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword });
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the superadmin's own password must keep unlocking the vault");

        var status = await _client.GetAsync("/api/session/status");
        (await status.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isUnlocked").GetBoolean()
            .Should().BeTrue();
    }

    [Fact]
    public async Task Unlock_WithWrongPassword_Returns401()
    {
        var resp = await _client.PostAsJsonAsync("/api/session/unlock", new { password = "totallyWrongPassword1" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unlock_WithRecoveryKey_Succeeds()
    {
        // Mint a recovery key while unlocked as the superadmin.
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword }))
            .EnsureSuccessStatusCode();

        var recoveryResp = await _client.PostAsync("/api/keys/add-recovery", content: null);
        recoveryResp.EnsureSuccessStatusCode();
        var recoveryKey = (await recoveryResp.Content.ReadFromJsonAsync<RecoveryKeyResponse>())!.RecoveryKey;

        // Lock the session directly (no HTTP endpoint call needed to prove the point) then
        // unlock again using ONLY the recovery key — the user's only way back in if the master
        // password is lost must keep working.
        using (var scope = _factory.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<SessionService>().Lock();

        var unlockResp = await _client.PostAsJsonAsync("/api/session/unlock", new { password = recoveryKey });
        unlockResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the recovery key is not tied to any user account and must always be able to unlock the vault");
    }

    [Fact]
    public async Task Unlock_WithNonSuperadminsKeySlotPassword_FailsIndistinguishablyFromWrongPassword()
    {
        const string bobPassword = "BobsSecretPassword1";

        // Build the invariant-violation state directly: a "user" slot owned by a non-superadmin.
        using (var scope = _factory.Services.CreateScope())
        {
            var keySlotRepo = scope.ServiceProvider.GetRequiredService<IKeySlotRepository>();
            var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            var adminSlot = (await keySlotRepo.GetAllAsync()).Single(s => s.SlotType == "user");
            var adminKek = KeyDerivation.DeriveKek(
                AdminPassword, adminSlot.Salt!,
                adminSlot.ArgonMemory!.Value, adminSlot.ArgonIterations!.Value, adminSlot.ArgonParallelism!.Value);
            var masterDek = MasterKeyManager.UnwrapMasterDek(adminSlot.EncryptedMasterDek, adminSlot.IV, adminKek);

            var bobSalt = KeyDerivation.GenerateSalt();
            var bobKek = KeyDerivation.DeriveKek(bobPassword, bobSalt);
            var (bobEncDek, bobIv) = MasterKeyManager.WrapMasterDek(masterDek, bobKek);
            Array.Clear(masterDek);

            var bobSlotId = await keySlotRepo.CreateAsync(new MasterKeyStore
            {
                SlotType = "user",
                EncryptedMasterDek = bobEncDek,
                IV = bobIv,
                Salt = bobSalt,
                ArgonMemory = CryptoConstants.DefaultArgonMemory,
                ArgonIterations = CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
                CreatedAt = DateTime.UtcNow
            });

            await userRepo.CreateAsync(new User
            {
                Username = "bob",
                DisplayName = "Bob",
                PasswordHash = "",
                Role = UserRoles.User,
                KeySlotId = bobSlotId,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
        }

        var bobResp = await _client.PostAsJsonAsync("/api/session/unlock", new { password = bobPassword });
        var wrongResp = await _client.PostAsJsonAsync("/api/session/unlock", new { password = "definitelyWrongPassword1" });

        bobResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a non-superadmin's key slot must not unlock the shared vault session even though the password is cryptographically correct for that slot");
        wrongResp.StatusCode.Should().Be(bobResp.StatusCode);

        var bobBody = await bobResp.Content.ReadFromJsonAsync<ErrorResponse>();
        var wrongBody = await wrongResp.Content.ReadFromJsonAsync<ErrorResponse>();
        bobBody!.Error.Should().Be(wrongBody!.Error,
            "a correct-password-but-not-permitted attempt must read exactly like a wrong password — " +
            "otherwise the endpoint becomes an oracle for guessing whether a given password belongs to some user");

        // Must be genuinely locked, not just reporting 401 while secretly unlocked underneath.
        var status = await _client.GetAsync("/api/session/status");
        (await status.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isUnlocked").GetBoolean()
            .Should().BeFalse("Bob's rejected attempt must not have left the vault unlocked");
    }

    [Fact]
    public async Task Join_WithNonSuperadminsKeySlotPassword_IsRejected()
    {
        // /api/join runs its OWN slot loop rather than going through SessionService, so the
        // restriction had to be applied there separately — and it matters more there: joining
        // returns a wrapped master DEK and mesh membership (a larger capability than unlocking
        // this one node), and unlike /api/session/unlock the endpoint deliberately skips the
        // internal-key gate, so a reverse proxy is expected to forward it from the outside.
        const string bobPassword = "BobsSecretPassword1";

        using (var scope = _factory.Services.CreateScope())
        {
            var keySlotRepo = scope.ServiceProvider.GetRequiredService<IKeySlotRepository>();
            var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();

            var adminSlot = (await keySlotRepo.GetAllAsync()).Single(s => s.SlotType == "user");
            var adminKek = KeyDerivation.DeriveKek(
                AdminPassword, adminSlot.Salt!,
                adminSlot.ArgonMemory!.Value, adminSlot.ArgonIterations!.Value, adminSlot.ArgonParallelism!.Value);
            var masterDek = MasterKeyManager.UnwrapMasterDek(adminSlot.EncryptedMasterDek, adminSlot.IV, adminKek);

            var bobSalt = KeyDerivation.GenerateSalt();
            var bobKek = KeyDerivation.DeriveKek(bobPassword, bobSalt);
            var (bobEncDek, bobIv) = MasterKeyManager.WrapMasterDek(masterDek, bobKek);
            Array.Clear(masterDek);

            var bobSlotId = await keySlotRepo.CreateAsync(new MasterKeyStore
            {
                SlotType = "user",
                EncryptedMasterDek = bobEncDek,
                IV = bobIv,
                Salt = bobSalt,
                ArgonMemory = CryptoConstants.DefaultArgonMemory,
                ArgonIterations = CryptoConstants.DefaultArgonIterations,
                ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
                CreatedAt = DateTime.UtcNow
            });

            await userRepo.CreateAsync(new User
            {
                Username = "bob",
                DisplayName = "Bob",
                PasswordHash = "",
                Role = UserRoles.User,
                KeySlotId = bobSlotId,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
        }

        // /api/join needs the responding node's own session unlocked before it will admit anyone
        // (it logs a whitelist_add event), so unlock as the superadmin first — otherwise the
        // admin control below fails for a reason unrelated to what this test is about.
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword }))
            .EnsureSuccessStatusCode();

        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        var joinBody = new
        {
            masterPassword = bobPassword,
            nodeId = Guid.NewGuid(),
            displayName = "BobsLaptop",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null
        };

        var resp = await _client.PostAsJsonAsync("/api/join", joinBody);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an ordinary user's key slot must not be able to join a node to the mesh, which would " +
            "hand its holder the wrapped master DEK");

        // And the superadmin's own password must still join, so the check did not just break join.
        var (adminNodePublicKey, _) = Ed25519Signer.GenerateKeyPair();
        var adminJoin = await _client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = AdminPassword,
            nodeId = Guid.NewGuid(),
            displayName = "AdminsLaptop",
            ed25519PublicKeyB64 = Convert.ToBase64String(adminNodePublicKey),
            apiAddress = (string?)null
        });
        adminJoin.StatusCode.Should().Be(HttpStatusCode.OK,
            "the superadmin must still be able to join new nodes");
    }
}
