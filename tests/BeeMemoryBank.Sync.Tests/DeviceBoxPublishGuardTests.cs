using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Recovery;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Guard for plan 6.3: every path that gives a master password a key slot publishes it as this
/// device's box through the one common point, <see cref="IRecoveryBoxPublisher"/>. One test per path
/// the plan names; a path that stops publishing turns its own test red.
/// </summary>
public class DeviceBoxPublishGuardTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminPass1";
    private const string NewPassword = "NewPassword2";

    private readonly ConcreteFixture _f = new();
    private readonly RecordingPublisher _recorder = new();
    private UserRepository _userRepo = null!;
    private KeySlotRepository _slots = null!;
    private FolderAccessService _folderAccess = null!;

    public async Task InitializeAsync()
    {
        await _f.InitializeAsync();
        await _f.InitService.InitializeAsync("admin", "GuardNode", AdminPassword);
        await _f.Session.UnlockAsync(AdminPassword);
        _userRepo = new UserRepository(_f.Factory);
        _slots = new KeySlotRepository(_f.Factory);
        var scopeHolder = new CallerScopeHolder();
        _folderAccess = new FolderAccessService(new ServiceCollection()
            .AddSingleton<IDbConnectionFactory>(_ => _f.Factory)
            .AddScoped<IUserRepository>(_ => _userRepo)
            .AddScoped<IRoleRepository>(_ => new RoleRepository(_f.Factory))
            .AddScoped<IRoleAclRepository>(_ => new RoleAclRepository(_f.Factory))
            .AddScoped<IFolderAclRepository>(_ => new FolderAclRepository(_f.Factory))
            .AddScoped<IFolderRepository>(_ => new FolderRepository(_f.Factory, scopeHolder))
            .AddScoped(_ => scopeHolder)
            .BuildServiceProvider());
    }

    public Task DisposeAsync() => _f.DisposeAsync();

    private UserService Users(IRecoveryBoxPublisher publisher) =>
        new(_userRepo, _slots, _f.Session, new RoleRepository(_f.Factory), _folderAccess, recoveryBoxes: publisher);

    private async Task<User> Admin() => (await _userRepo.GetByUsernameAsync("admin"))!;

    [Fact]
    public async Task UserService_ChangePassword_Publishes()
    {
        var admin = await Admin();

        await Users(_recorder).ChangePasswordAsync(admin.Id, AdminPassword, NewPassword);

        await AssertPublishedCurrentSlotAsync(admin.Id, NewPassword);
    }

    [Fact]
    public async Task UserService_AdminChangePassword_Publishes()
    {
        var admin = await Admin();

        await Users(_recorder).AdminChangePasswordAsync(admin.Id, NewPassword);

        await AssertPublishedCurrentSlotAsync(admin.Id, NewPassword);
    }

    [Fact]
    public async Task UserService_ProvisionMissingKeySlot_Publishes()
    {
        var users = Users(new NullRecoveryBoxPublisher());
        var bob = await users.CreateUserAsync("bob", "Bob", "BobPass12", UserRoles.User);
        await users.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin);
        bob = (await _userRepo.GetByIdAsync(bob.Id))!;

        (await Users(_recorder).ProvisionMissingKeySlotAsync(bob, "BobPass12")).Should().BeTrue();

        await AssertPublishedCurrentSlotAsync(bob.Id, "BobPass12");
    }

    [Fact]
    public async Task UserService_CreateSuperadmin_Publishes_RegularUserDoesNot()
    {
        var users = Users(_recorder);

        await users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.User);
        _recorder.Calls.Should().BeEmpty("a regular user holds no key slot");

        var dave = await users.CreateUserAsync("dave", "Dave", "DavePass12", UserRoles.Superadmin);
        await AssertPublishedCurrentSlotAsync(dave.Id, "DavePass12");
    }

    [Fact]
    public async Task KeyManagementService_LegacyChangePassword_Publishes()
    {
        // KeyEndpoints /change-password without a signed-in user (older mobile builds).
        var keys = new KeyManagementService(_slots, _f.Session, _userRepo, _f.Factory, _recorder);

        await keys.ChangePasswordAsync(AdminPassword, NewPassword);

        await AssertPublishedCurrentSlotAsync((await Admin()).Id, NewPassword);
    }

    [Fact]
    public async Task LazySlotRewrap_Publishes_TheNewKey()
    {
        // A peer rotated the DEK; this node applied it but the admin's slot still wraps the old key.
        var oldDek = _f.Session.GetMasterDek();
        var newDek = MasterKeyManager.GenerateMasterDek();
        var (chainDek, chainIv) = MasterKeyManager.WrapMasterDek(newDek, oldDek);
        var rotationId = Guid.NewGuid().ToString();
        var stateRepo = new DekRotationStateRepository(_f.Factory);
        var now = DateTime.UtcNow.ToString("O");
        await stateRepo.UpsertAsync(new DekRotationStateRow(rotationId, DekRotationState.Applied, rotationId, now,
            now, null, null, null, null, null, null, now, now));
        using (var conn = _f.Factory.CreateConnection())
            await conn.ExecuteAsync(
                "UPDATE tbl_dek_rotation_state SET chain_encrypted_new_dek = @E, chain_iv = @I WHERE event_id = @Id",
                new { E = Convert.ToBase64String(chainDek), I = Convert.ToBase64String(chainIv), Id = rotationId });

        var slot = (await _slots.GetAllAsync()).Single(s => s.SlotType == "user");
        var kek = KeyDerivation.DeriveKek(AdminPassword, slot.Salt!);
        var scopes = new ServiceCollection()
            .AddScoped<IDekRotationStateRepository>(_ => stateRepo)
            .AddScoped<IRecoveryBoxPublisher>(_ => _recorder)
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var lazy = new LazySlotRewrapService(scopes, _f.Factory, _slots, NullLogger<LazySlotRewrapService>.Instance);

        var result = await lazy.TryRewrapAsync(slot, kek, oldDek, MasterKeyManager.ComputeSentinel(newDek));

        result.Success.Should().BeTrue();
        var call = _recorder.Calls.Should().ContainSingle().Subject;
        call.Dek.Should().Equal(newDek);
        call.Slot.SlotId.Should().Be(slot.SlotId);
        RecoveryBoxCrypto.Unwrap(AdminPassword, RecoveryBoxKdf.Device64, call.Slot.Salt!, call.Slot.EncryptedMasterDek, call.Slot.IV)
            .Should().Equal(newDek, "the published box must hold the key the slot now wraps");
    }

    [Fact]
    public async Task RealPublisher_WritesAnActiveBox_AndASignedEvent_SupersedingTheOldOne()
    {
        var events = new RecoveryEventPublisher(_f.NodeRepo, _f.EventLogRepo, _f.Clock, _f.Session, _f.EventApplier, new SyncTrigger(), new FixedOwnStanding(false));
        var users = Users(new DeviceBoxPublisher(events, _f.NodeRepo, NullLogger<DeviceBoxPublisher>.Instance));
        var admin = await Admin();
        var identity = (await _f.NodeRepo.GetAsync())!;
        var dek = _f.Session.GetMasterDek();

        await users.ChangePasswordAsync(admin.Id, AdminPassword, NewPassword);
        await users.ChangePasswordAsync(admin.Id, NewPassword, "ThirdPassword3");

        using var conn = _f.Factory.CreateConnection();
        var boxes = (await conn.QueryAsync<(string Status, string Preset, string Fp, byte[] Salt, byte[] Wrapped, byte[] Iv, string Author)>(
            @"SELECT status, kdf_preset, dek_fingerprint, salt, wrapped, iv, author_node_id FROM tbl_recovery_box
              WHERE kind = 'device' ORDER BY lamport_ts")).ToList();
        boxes.Should().HaveCount(2);
        boxes[0].Status.Should().Be("R", "the newer box of this node supersedes the older one");
        var active = boxes[1];
        active.Status.Should().Be("A");
        active.Author.Should().BeEquivalentTo(identity.NodeId.ToString());
        active.Fp.Should().Be(DekFingerprint.Of(dek));
        RecoveryBoxCrypto.Unwrap("ThirdPassword3", active.Preset, active.Salt, active.Wrapped, active.Iv).Should().Equal(dek);
        RecoveryBoxCrypto.TryUnwrap(NewPassword, active.Preset, active.Salt, active.Wrapped, active.Iv).Should().BeNull();

        // The superseded box keeps no key material, in its row or in the log (F7): only the active box's event is served.
        boxes[0].Wrapped.Should().BeEmpty();
        boxes[0].Salt.Should().BeEmpty();
        var logged = (await _f.EventLogRepo.GetRecentAsync(100, 0, EventTypes.RecoveryBoxSet)).Where(e => e.EventType == EventTypes.RecoveryBoxSet).ToList();
        logged.Should().ContainSingle();
        logged.Should().OnlyContain(e => Ed25519Signer.Verify(identity.Ed25519PublicKey, EventSignature.BuildPayload(e), e.Signature));
    }

    [Theory]
    [InlineData("recovery")]
    [InlineData("os_auto_unlock")]
    public async Task RealPublisher_IgnoresSlotsThatAreNotAPassword(string slotType)
    {
        var events = new RecoveryEventPublisher(_f.NodeRepo, _f.EventLogRepo, _f.Clock, _f.Session, _f.EventApplier, new SyncTrigger(), new FixedOwnStanding(false));
        var publisher = new DeviceBoxPublisher(events, _f.NodeRepo, NullLogger<DeviceBoxPublisher>.Instance);
        var slot = (await _slots.GetAllAsync()).Single(s => s.SlotType == "user");
        slot.SlotType = slotType;

        await publisher.PublishDeviceBoxAsync(slot, _f.Session.GetMasterDek());

        using var conn = _f.Factory.CreateConnection();
        (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_recovery_box")).Should().Be(0);
    }

    private async Task AssertPublishedCurrentSlotAsync(int userId, string password)
    {
        var user = (await _userRepo.GetByIdAsync(userId))!;
        var call = _recorder.Calls.Should().ContainSingle().Subject;
        call.Slot.SlotId.Should().Be(user.KeySlotId!.Value, "the published slot is the one the user now holds");
        var dek = _f.Session.GetMasterDek();
        call.Dek.Should().Equal(dek);
        RecoveryBoxCrypto.Unwrap(password, RecoveryBoxKdf.Device64, call.Slot.Salt!, call.Slot.EncryptedMasterDek, call.Slot.IV)
            .Should().Equal(dek);
    }

    private sealed class RecordingPublisher : IRecoveryBoxPublisher
    {
        public List<(MasterKeyStore Slot, byte[] Dek)> Calls { get; } = [];

        public Task PublishDeviceBoxAsync(MasterKeyStore slot, byte[] dek)
        {
            Calls.Add((slot, (byte[])dek.Clone()));
            return Task.CompletedTask;
        }
    }

    private sealed class ConcreteFixture : SyncTestFixture { }
}
