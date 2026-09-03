using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// L3: /api/join must validate the master password against EVERY password-bearing key slot on the
/// node, not just the first one <see cref="IKeySlotRepository.GetAllAsync"/> happens to return.
///
/// <para>
/// A node with more than one superadmin carries one "user" key slot per superadmin, each
/// independently wrapping the SAME master DEK with that user's own password-derived KEK (see
/// UserService's promote-to-superadmin path). Before the fix, JoinEndpoints picked
/// <c>slots.FirstOrDefault(s => s.SlotType == "user")</c> and validated only against that one slot
/// -- so every superadmin except whichever one happened to sort first got "invalid master
/// password" (401) when trying to join a new node with their OWN correct password.
/// </para>
/// </summary>
public class JoinMultiUserSlotTests : IAsyncLifetime
{
    private BmbWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private const string FirstPassword = "firstAdminPassword1!";
    private const string SecondPassword = "secondAdminPassword2!";

    public async Task InitializeAsync()
    {
        _factory = new BmbWebApplicationFactory();
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync("JoinMultiSlotNode", FirstPassword);

        // Add a second superadmin with an INDEPENDENT password, wrapping the SAME master DEK --
        // mirrors what promoting a second user to superadmin leaves behind: a second "user" slot
        // that unwraps the identical master DEK bytes via a different password/salt/KEK.
        using var scope = _factory.Services.CreateScope();
        var keySlotRepo = scope.ServiceProvider.GetRequiredService<IKeySlotRepository>();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        var slots = await keySlotRepo.GetAllAsync();
        var firstSlot = slots.Single(s => s.SlotType == "user");

        var firstKek = KeyDerivation.DeriveKek(
            FirstPassword, firstSlot.Salt!,
            firstSlot.ArgonMemory!.Value, firstSlot.ArgonIterations!.Value, firstSlot.ArgonParallelism!.Value);
        var masterDek = MasterKeyManager.UnwrapMasterDek(firstSlot.EncryptedMasterDek, firstSlot.IV, firstKek);

        var secondSalt = KeyDerivation.GenerateSalt();
        var secondKek = KeyDerivation.DeriveKek(SecondPassword, secondSalt);
        var (secondEncDek, secondIv) = MasterKeyManager.WrapMasterDek(masterDek, secondKek);
        Array.Clear(masterDek);

        var secondSlotId = await keySlotRepo.CreateAsync(new MasterKeyStore
        {
            SlotType = "user",
            EncryptedMasterDek = secondEncDek,
            IV = secondIv,
            Salt = secondSalt,
            ArgonMemory = CryptoConstants.DefaultArgonMemory,
            ArgonIterations = CryptoConstants.DefaultArgonIterations,
            ArgonParallelism = CryptoConstants.DefaultArgonParallelism,
            CreatedAt = DateTime.UtcNow
        });

        await userRepo.CreateAsync(new User
        {
            Username = "second-admin",
            DisplayName = "Second Admin",
            Role = UserRoles.Superadmin,
            KeySlotId = secondSlotId,
            CreatedAt = DateTime.UtcNow
        });

        // /api/join's whitelist-add event is logged via EventLogger, which needs the master DEK
        // (SessionService.GetMasterDek) to encrypt the event payload -- unrelated to the L3 fix
        // itself, but the responding node's session still has to be unlocked for /api/join to
        // succeed at all, exactly as the existing two-node sync tests do before joining.
        var unlockResp = await _client.PostAsJsonAsync("/api/session/unlock", new { Password = FirstPassword });
        unlockResp.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Join_SucceedsWithTheFirstSuperadminsPassword()
    {
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();

        var resp = await _client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = FirstPassword,
            nodeId = Guid.NewGuid(),
            displayName = "JoiningNode1",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>The regression case: the SECOND superadmin's own password must also work.</summary>
    [Fact]
    public async Task Join_SucceedsWithASecondSuperadminsOwnPassword()
    {
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();

        var resp = await _client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = SecondPassword,
            nodeId = Guid.NewGuid(),
            displayName = "JoiningNode2",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Join_StillRejectsAWrongPassword()
    {
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();

        var resp = await _client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = "definitely-not-a-valid-password",
            nodeId = Guid.NewGuid(),
            displayName = "JoiningNode3",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
