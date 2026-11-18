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
        call.Slot.DoorVersion.Should().Be(2, "the lazy rewrap writes a sealed (v2) door");
        RecoveryBoxCrypto.Unwrap(AdminPassword, RecoveryBoxKdf.Device64, call.Slot.Salt!, Published(call.Slot), call.Slot.IV)
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
        active.Wrapped.Should().HaveCount(RecoveryBoxKdf.WrappedDoorV2Size, "the box of a sealed door: 0x02 || door_pk || box (ADR 0012)");
        active.Wrapped[0].Should().Be(RecoveryBoxKdf.WrappedDoorV2);
        RecoveryBoxCrypto.Unwrap("ThirdPassword3", active.Preset, active.Salt, active.Wrapped, active.Iv).Should().Equal(dek);
        RecoveryBoxCrypto.TryUnwrap(NewPassword, active.Preset, active.Salt, active.Wrapped, active.Iv).Should().BeNull();

        // The superseded box keeps no key material, in its row or in the log: only the active box's event is served.
        boxes[0].Wrapped.Should().BeEmpty();
        boxes[0].Salt.Should().BeEmpty();
        var logged = (await _f.EventLogRepo.GetRecentAsync(100, 0, EventTypes.RecoveryBoxSet)).Where(e => e.EventType == EventTypes.RecoveryBoxSet).ToList();
        logged.Should().ContainSingle();
        logged.Should().OnlyContain(e => Ed25519Signer.Verify(identity.Ed25519PublicKey, EventSignature.BuildPayload(e), e.Signature));
    }

    [Fact]
    public async Task RealPublisher_StillPublishesAV1SlotAsItIs_AndTheApplierAndARestoreOpenIt()
    {
        var events = new RecoveryEventPublisher(_f.NodeRepo, _f.EventLogRepo, _f.Clock, _f.Session, _f.EventApplier, new SyncTrigger(), new FixedOwnStanding(false));
        var publisher = new DeviceBoxPublisher(events, _f.NodeRepo, NullLogger<DeviceBoxPublisher>.Instance);
        var dek = _f.Session.GetMasterDek();
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek("V1Password1", salt);
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(dek, kek);
        var v1 = new MasterKeyStore
        {
            SlotId = 99, SlotType = "user", EncryptedMasterDek = wrapped, IV = iv, Salt = salt, ArgonMemory = CryptoConstants.DefaultArgonMemory,
            ArgonIterations = CryptoConstants.DefaultArgonIterations, ArgonParallelism = CryptoConstants.DefaultArgonParallelism, DoorVersion = 1,
        };

        await publisher.PublishDeviceBoxAsync(v1, dek);

        using var conn = _f.Factory.CreateConnection();
        var active = await conn.QuerySingleAsync<(string Preset, byte[] Salt, byte[] Wrapped, byte[] Iv)>(
            "SELECT kdf_preset, salt, wrapped, iv FROM tbl_recovery_box WHERE kind = 'device' AND status = 'A'");
        active.Wrapped.Should().Equal(wrapped, "a v1 slot is still copied byte for byte");
        RecoveryBoxCrypto.Unwrap("V1Password1", active.Preset, active.Salt, active.Wrapped, active.Iv).Should().Equal(dek);
    }

    // ---- 2.12: the capability gate and the door a device box copies (plan WS-A4) -------------------------------------------------

    private async Task AddPeerAsync(string capabilities)
    {
        var peer = Guid.NewGuid();
        await _f.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = peer, DisplayName = "Peer", Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey, Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        using var conn = _f.Factory.CreateConnection();
        await conn.ExecuteAsync("UPDATE tbl_whitelist SET last_capabilities = @capabilities, last_capabilities_seen_at = @now WHERE node_id = @peer COLLATE NOCASE",
            new { capabilities, now = DateTime.UtcNow.ToString("O"), peer = peer.ToString() });
    }

    private DeviceBoxPublisher GatedPublisher() => new(
        new RecoveryEventPublisher(_f.NodeRepo, _f.EventLogRepo, _f.Clock, _f.Session, _f.EventApplier, new SyncTrigger(), new FixedOwnStanding(false)),
        _f.NodeRepo, NullLogger<DeviceBoxPublisher>.Instance, _f.WhitelistRepo, _slots, _f.Factory);

    private async Task<List<(string Status, string Preset, byte[] Salt, byte[] Wrapped, byte[] Iv)>> DeviceBoxesAsync()
    {
        using var conn = _f.Factory.CreateConnection();
        return (await conn.QueryAsync<(string, string, byte[], byte[], byte[])>(
            "SELECT status, kdf_preset, salt, wrapped, iv FROM tbl_recovery_box WHERE kind = 'device' ORDER BY lamport_ts")).ToList();
    }

    [Fact]
    public async Task WhileAPeerCannotOpenAV2Box_ThePasswordPublishesTheOldShape_AndARotationPublishesNothing()
    {
        await AddPeerAsync("metadata-envelope-v1,peer-endpoints-v1");
        var publisher = GatedPublisher();
        var admin = await Admin();
        var dek = _f.Session.GetMasterDek();

        await Users(publisher).ChangePasswordAsync(admin.Id, AdminPassword, NewPassword);

        var box = (await DeviceBoxesAsync()).Should().ContainSingle().Subject;
        box.Wrapped.Length.Should().BeOneOf(48, 49).And.NotBe(RecoveryBoxKdf.WrappedDoorV2Size, "a v1-shaped box, which every peer opens");
        RecoveryBoxCrypto.Unwrap(NewPassword, box.Preset, box.Salt, box.Wrapped, box.Iv).Should().Equal(dek);

        await publisher.RepublishForDoorsAsync(dek);
        await publisher.PublishDeviceBoxAsync((await _slots.GetAllAsync()).Single(s => s.SlotType == "user"), dek);

        (await DeviceBoxesAsync()).Should().ContainSingle("without the password and without the capability nothing is published");
    }

    [Fact]
    public async Task OnceEveryPeerOpensV2Boxes_TheDoorIsPublishedAsAV2Box()
    {
        await AddPeerAsync("metadata-envelope-v1,peer-endpoints-v1,door-box-v1");
        var admin = await Admin();
        var dek = _f.Session.GetMasterDek();

        await Users(GatedPublisher()).ChangePasswordAsync(admin.Id, AdminPassword, NewPassword);

        var box = (await DeviceBoxesAsync()).Should().ContainSingle().Subject;
        box.Wrapped.Should().HaveCount(RecoveryBoxKdf.WrappedDoorV2Size);
        RecoveryBoxCrypto.Unwrap(NewPassword, box.Preset, box.Salt, box.Wrapped, box.Iv).Should().Equal(dek);
    }

    [Fact]
    public async Task AfterARotation_TheBoxCopiesTheDoorOfTheLatestBox_ElseTheSuperadminWhoSignedInLast()
    {
        var users = Users(new NullRecoveryBoxPublisher());
        await users.CreateUserAsync("bob", "Bob", "BobPass12", UserRoles.Superadmin);
        using (var conn = _f.Factory.CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE tbl_user SET last_login_at = @t WHERE username = 'bob'", new { t = DateTime.UtcNow.ToString("O") });
            await conn.ExecuteAsync("UPDATE tbl_user SET last_login_at = @t WHERE username = 'admin'", new { t = DateTime.UtcNow.AddDays(-1).ToString("O") });
        }
        var publisher = GatedPublisher();
        var dek = _f.Session.GetMasterDek();
        var bob = (await _userRepo.GetByUsernameAsync("bob"))!;
        var bobSlot = (await _slots.GetAllAsync()).Single(s => s.SlotId == bob.KeySlotId);
        var adminId = (await Admin()).KeySlotId;
        var adminSlot = (await _slots.GetAllAsync()).Single(s => s.SlotId == adminId);

        await publisher.RepublishForDoorsAsync(dek);

        (await DeviceBoxesAsync()).Should().ContainSingle().Which.Salt.Should().Equal(bobSlot.Salt, "no box yet: the superadmin who signed in last");

        await publisher.PublishDeviceBoxAsync(adminSlot, dek);   // the admin's password becomes this device's box
        await publisher.RepublishForDoorsAsync(dek);

        var boxes = await DeviceBoxesAsync();
        boxes.Should().HaveCount(3);
        boxes[^1].Status.Should().Be("A");
        boxes[^1].Salt.Should().Equal(adminSlot.Salt, "the door whose salt the latest device box carries");
        RecoveryBoxCrypto.Unwrap(AdminPassword, boxes[^1].Preset, boxes[^1].Salt, boxes[^1].Wrapped, boxes[^1].Iv).Should().Equal(dek);
    }

    [Fact]
    public async Task AfterTheLatestDeviceBoxWasRetiredAndScrubbed_TheBoxStillCopiesTheDoorThatBoxCopied()
    {
        // a device box made inactive (superseded, or retired by a strong box) loses its salt (EventApplier ScrubBoxesAsync sets
        // salt = X''). Rule (1) matched the latest box by salt and so fell through to the superadmin who signed in last: another person's door.
        var users = Users(new NullRecoveryBoxPublisher());
        await users.CreateUserAsync("bob", "Bob", "BobPass12", UserRoles.Superadmin);
        using (var conn = _f.Factory.CreateConnection())
        {
            await conn.ExecuteAsync("UPDATE tbl_user SET last_login_at = @t WHERE username = 'bob'", new { t = DateTime.UtcNow.ToString("O") });
            await conn.ExecuteAsync("UPDATE tbl_user SET last_login_at = @t WHERE username = 'admin'", new { t = DateTime.UtcNow.AddDays(-1).ToString("O") });
        }
        var publisher = GatedPublisher();
        var dek = _f.Session.GetMasterDek();
        var adminId = (await Admin()).KeySlotId;
        var adminSlot = (await _slots.GetAllAsync()).Single(s => s.SlotId == adminId);
        await publisher.PublishDeviceBoxAsync(adminSlot, dek);
        using (var conn = _f.Factory.CreateConnection())
            // As a covering strong box retires it: inactive, the material scrubbed.
            await conn.ExecuteAsync("UPDATE tbl_recovery_box SET status = 'R', salt = X'', wrapped = X'', iv = X'' WHERE kind = 'device'");

        await publisher.RepublishForDoorsAsync(dek);

        var boxes = await DeviceBoxesAsync();
        boxes.Should().HaveCount(2);
        boxes[^1].Status.Should().Be("A");
        boxes[^1].Salt.Should().Equal(adminSlot.Salt, "the door the retired box copied, not the one of the superadmin who signed in last");
    }

    // ---- 2.12 cross review (finding 1): a demotion or deletion never leaves this node's device box opening with the leaver's password ------

    private UserService UsersWithDb(IRecoveryBoxPublisher publisher) =>
        new(_userRepo, _slots, _f.Session, new RoleRepository(_f.Factory), _folderAccess, recoveryBoxes: publisher, factory: _f.Factory);

    /// <summary>Rewrites a password door as the v1 wrap of an earlier release (same salt and password), as a superadmin who has not signed in since 2.12.</summary>
    private async Task MakeV1Async(int slotId, string password)
    {
        var slot = (await _slots.GetAllAsync()).Single(s => s.SlotId == slotId);
        var kek = KeyDerivation.DeriveKek(password, slot.Salt!);
        var (wrapped, iv) = MasterKeyManager.WrapMasterDek(_f.Session.GetMasterDek(), kek);
        Array.Clear(kek);
        using var c = _f.Factory.CreateConnection();
        await c.ExecuteAsync(
            "UPDATE tbl_key_slot SET encrypted_master_dek = @wrapped, iv = @iv, door_version = 1, door_public_key = NULL, dek_fp = NULL WHERE slot_id = @slotId",
            new { wrapped, iv, slotId });
    }

    private async Task<(string Preset, byte[] Salt, byte[] Wrapped, byte[] Iv)> ActiveDeviceBoxAsync()
    {
        var active = (await DeviceBoxesAsync()).Where(b => b.Status == "A").ToList();
        active.Should().ContainSingle("one active device box per node");
        return (active[0].Preset, active[0].Salt, active[0].Wrapped, active[0].Iv);
    }

    [Fact]
    public async Task DemotingTheOnlySuperadminWithASealedDoor_WhileAV1StragglerRemains_IsRefused_AndNothingChanges()
    {
        var users = UsersWithDb(GatedPublisher());
        var carol = await users.CreateUserAsync("carol", "Carol", "CarolPass12", UserRoles.Superadmin);   // her sealed door is this device's box now
        var admin = await Admin();
        await MakeV1Async(admin.KeySlotId!.Value, AdminPassword);                                          // the straggler: a v1 door only
        var dek = _f.Session.GetMasterDek();
        var before = await ActiveDeviceBoxAsync();
        RecoveryBoxCrypto.Unwrap("CarolPass12", before.Preset, before.Salt, before.Wrapped, before.Iv).Should().Equal(dek);

        var act = () => users.DemoteSuperadminAsync(carol.Id, UserRoles.User, allowOpenRole: true);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("sealed").And.Contain("sign in").And.Contain("Nothing was changed");
        var after = (await _userRepo.GetByIdAsync(carol.Id))!;
        after.Role.Should().Be(UserRoles.Superadmin);
        after.KeySlotId.Should().Be(carol.KeySlotId, "her door stays until another superadmin holds a sealed one");
        (await ActiveDeviceBoxAsync()).Salt.Should().Equal(before.Salt);
    }

    [Fact]
    public async Task DeletingTheOnlySuperadminWithASealedDoor_WhileAV1StragglerRemains_IsRefused()
    {
        var users = UsersWithDb(GatedPublisher());
        var carol = await users.CreateUserAsync("carol", "Carol", "CarolPass12", UserRoles.Superadmin);
        await MakeV1Async((await Admin()).KeySlotId!.Value, AdminPassword);

        var act = () => users.DeleteUserAsync(carol.Id);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("sealed");
        (await _userRepo.GetByIdAsync(carol.Id))!.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task WithAnotherSealedDoor_TheDemotionGoesThrough_AndTheActiveDeviceBoxNoLongerOpensWithTheLeaversPassword()
    {
        var users = UsersWithDb(GatedPublisher());
        var carol = await users.CreateUserAsync("carol", "Carol", "CarolPass12", UserRoles.Superadmin);   // the latest device box copies her door
        var dek = _f.Session.GetMasterDek();
        var before = await ActiveDeviceBoxAsync();
        RecoveryBoxCrypto.Unwrap("CarolPass12", before.Preset, before.Salt, before.Wrapped, before.Iv).Should().Equal(dek);

        await users.DemoteSuperadminAsync(carol.Id, UserRoles.User, allowOpenRole: true);

        var box = await ActiveDeviceBoxAsync();
        RecoveryBoxCrypto.TryUnwrap("CarolPass12", box.Preset, box.Salt, box.Wrapped, box.Iv).Should().BeNull("the leaver's password opens no active box of this node");
        RecoveryBoxCrypto.Unwrap(AdminPassword, box.Preset, box.Salt, box.Wrapped, box.Iv).Should().Equal(dek, "the box copies the door of the superadmin who stays");
    }

    [Fact]
    public async Task UpdateUser_ALeftoverDoorOfANonSuperadmin_IsKeptWhileNoOtherSuperadminHoldsASealedDoor()
    {
        // The leftover path of UpdateUserAsync (a door a pre-2.12 demotion left behind on a non-superadmin) deletes a door outside the frame.
        var users = UsersWithDb(GatedPublisher());
        var carol = await users.CreateUserAsync("carol", "Carol", "CarolPass12", UserRoles.Superadmin);
        using (var c = _f.Factory.CreateConnection())
            await c.ExecuteAsync("UPDATE tbl_user SET role = 'user' WHERE id = @id", new { id = carol.Id });
        await new RoleRepository(_f.Factory).CreateAsync(new Role
        {
            Name = "leftover-closed", DisplayName = "Closed", BasePolicy = RoleBasePolicy.Closed, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await MakeV1Async((await Admin()).KeySlotId!.Value, AdminPassword);

        var act = () => users.UpdateUserAsync(carol.Id, "Carol", "leftover-closed");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("sealed");
        (await _userRepo.GetByIdAsync(carol.Id))!.KeySlotId.Should().Be(carol.KeySlotId);
    }

    // ---- The slot cards of the phones delete a password door outside the frame ----------------------------------

    [Fact]
    public async Task RemoveSlot_TheOnlySealedPasswordDoorOfASuperadmin_IsRefusedWhileOnlyAV1StragglerRemains()
    {
        var publisher = GatedPublisher();
        var users = UsersWithDb(publisher);
        var carol = await users.CreateUserAsync("carol", "Carol", "CarolPass12", UserRoles.Superadmin);   // her sealed door is this device's box now
        await MakeV1Async((await Admin()).KeySlotId!.Value, AdminPassword);
        var keys = new KeyManagementService(_slots, _f.Session, _userRepo, _f.Factory, publisher);

        var act = () => keys.RemoveSlotAsync(carol.KeySlotId!.Value, "admin");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("sealed").And.Contain("Nothing was changed");
        (await _slots.GetAllAsync()).Should().Contain(s => s.SlotId == carol.KeySlotId);
    }

    [Fact]
    public async Task RemoveSlot_WithAnotherSealedDoor_GoesThrough_AndTheActiveBoxNoLongerOpensWithThatPassword()
    {
        var publisher = GatedPublisher();
        var users = UsersWithDb(publisher);
        var carol = await users.CreateUserAsync("carol", "Carol", "CarolPass12", UserRoles.Superadmin);
        var dek = _f.Session.GetMasterDek();
        var keys = new KeyManagementService(_slots, _f.Session, _userRepo, _f.Factory, publisher);

        await keys.RemoveSlotAsync(carol.KeySlotId!.Value, "admin");

        var box = await ActiveDeviceBoxAsync();
        RecoveryBoxCrypto.TryUnwrap("CarolPass12", box.Preset, box.Salt, box.Wrapped, box.Iv).Should().BeNull();
        RecoveryBoxCrypto.Unwrap(AdminPassword, box.Preset, box.Salt, box.Wrapped, box.Iv).Should().Equal(dek);
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
        call.Slot.DoorVersion.Should().Be(2, "a new password door is a sealed (v2) door");
        RecoveryBoxCrypto.Unwrap(password, RecoveryBoxKdf.Device64, call.Slot.Salt!, Published(call.Slot), call.Slot.IV)
            .Should().Equal(dek);
    }

    /// <summary>What DeviceBoxPublisher puts in <c>wrapped</c> for a slot: the v1 wrap itself, or 0x02 || door_pk || box for a v2 door.</summary>
    private static byte[] Published(MasterKeyStore slot) =>
        slot.DoorVersion == 2 ? RecoveryBoxKdf.DoorV2Wrapped(slot.DoorPublicKey!, slot.EncryptedMasterDek) : slot.EncryptedMasterDek;

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
