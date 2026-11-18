using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Tests;

[Collection("DerivationCounter")]
public class SharedDoorsAuthTests : TestFixture
{
    private sealed class RecordingNotifier : ISuperadminDoorsNotifier
    {
        public int ImmediateCount { get; private set; }
        public int BackgroundCount { get; private set; }

        public Task NotifyChangedAsync(bool immediate = false, CancellationToken ct = default)
        {
            if (immediate) ImmediateCount++;
            else BackgroundCount++;
            return Task.CompletedTask;
        }
    }

    private UserRepository _users = null!;
    private KeySlotRepository _slots = null!;
    private RoleRepository _roles = null!;
    private NodeIdentityRepository _nodes = null!;
    private AccountsHomeRepository _homeRepo = null!;
    private SharedDoorsStateRepository _stateRepo = null!;
    private WhitelistRepository _whitelist = null!;
    private FolderAccessService _folderAccess = null!;
    private RecordingNotifier _notifier = new();
    private UserService _userService = null!;
    private KeyManagementService _keyMgmt = null!;
    private Guid _homeNodeId;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("localinstaller", "SharedNode", "InstallerPass1");
        await Session.UnlockAsync("InstallerPass1");

        _users = new UserRepository(Factory);
        _slots = new KeySlotRepository(Factory);
        _roles = new RoleRepository(Factory);
        _nodes = new NodeIdentityRepository(Factory);
        _homeRepo = new AccountsHomeRepository(Factory);
        _stateRepo = new SharedDoorsStateRepository(Factory);
        _whitelist = new WhitelistRepository(Factory);
        _folderAccess = new FolderAccessService(new ServiceCollection()
            .AddSingleton<IDbConnectionFactory>(_ => Factory)
            .AddScoped<IUserRepository>(_ => _users)
            .AddScoped<IRoleRepository>(_ => _roles)
            .AddScoped<IRoleAclRepository>(_ => new RoleAclRepository(Factory))
            .AddScoped<IFolderAclRepository>(_ => new FolderAclRepository(Factory))
            .AddScoped<IFolderRepository>(_ => new FolderRepository(Factory, ScopeHolder))
            .AddScoped(_ => ScopeHolder)
            .BuildServiceProvider());

        _homeNodeId = Guid.NewGuid();
        var (homePub, _) = Ed25519Signer.GenerateKeyPair();
        await _whitelist.CreateAsync(new WhitelistEntry
        {
            NodeId = _homeNodeId,
            DisplayName = "PrimaryAccounts",
            Ed25519PublicKey = homePub,
            Status = WhitelistStatuses.Active,
            IsSuperadmin = true,
            Ownership = OwnershipKinds.Shared,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await _homeRepo.UpsertAsync(new AccountsHomeRecord(_homeNodeId, 10, _homeNodeId, Guid.NewGuid(), DateTime.UtcNow));
        await _stateRepo.SetSelfOwnershipAsync(OwnershipKinds.Shared, 10, _homeNodeId);

        _notifier = new RecordingNotifier();
        _userService = new UserService(
            _users, _slots, Session, _roles, _folderAccess,
            agentRepo: new AgentRepository(Factory),
            factory: Factory,
            accountsHomeRepo: _homeRepo,
            whitelistRepo: _whitelist,
            nodeIdentityRepo: _nodes,
            sharedDoorsStateRepo: _stateRepo,
            doorsNotifier: _notifier);
        _keyMgmt = new KeyManagementService(
            _slots, Session, _users, Factory,
            accountsHomeRepo: _homeRepo,
            whitelistRepo: _whitelist,
            nodeIdentityRepo: _nodes,
            doorsNotifier: _notifier);
    }

    private async Task<User> SeedReplicatedUserAsync(string username, string password)
    {
        var dek = Session.GetMasterDek();
        var salt = KeyDerivation.GenerateSalt();
        var kek = KeyDerivation.DeriveKek(password, salt);
        var sealedDoor = Doors.NewPasswordSlot(
            KeySlotTypes.User, kek, salt,
            CryptoConstants.DefaultArgonMemory, CryptoConstants.DefaultArgonIterations, CryptoConstants.DefaultArgonParallelism,
            dek);
        Array.Clear(kek);
        Array.Clear(dek);

        var id = await _users.CreateAsync(new User
        {
            PersonId = Guid.NewGuid(),
            Username = username,
            DisplayName = username.ToUpperInvariant(),
            PasswordHash = "",
            Role = UserRoles.Superadmin,
            IsActive = true,
            Replicated = true,
            CreatedAt = DateTime.UtcNow,
        });
        sealedDoor.OwnerUserId = id;
        sealedDoor.CreatedByUserId = id;
        var slotId = await _slots.CreateAsync(sealedDoor);
        var u = (await _users.GetByIdAsync(id))!;
        u.KeySlotId = slotId;
        await _users.UpdateAsync(u);
        return (await _users.GetByIdAsync(id))!;
    }

    [Fact]
    public async Task ReplicatedUser_AuthenticatesByOpeningOwnDoor_RejectsOtherUsersPassword_AndKeeps1Argon2Parity()
    {
        var alice = await SeedReplicatedUserAsync("alice", "AlicePass1");
        var bob = await SeedReplicatedUserAsync("bob", "BobPass22");

        // Warm dummy hash before counting derivations.
        UserService.BurnPasswordVerification("warmup");

        // 1. Unknown user: 1 Argon2 derivation.
        using (var c0 = KeyDerivation.BeginScopedCountForTests())
        {
            (await _userService.AuthenticateAsync("nobody", "Whatever1")).Should().BeNull();
            c0.Count.Should().Be(1);
        }

        // 2. Wrong password on replicated user: 1 Argon2 derivation.
        using (var c1 = KeyDerivation.BeginScopedCountForTests())
        {
            (await _userService.AuthenticateAsync("alice", "WrongPass9")).Should().BeNull();
            c1.Count.Should().Be(1);
        }

        // 3. Another replicated superadmin's valid password MUST NOT authenticate alice: 1 Argon2 derivation.
        using (var c2 = KeyDerivation.BeginScopedCountForTests())
        {
            (await _userService.AuthenticateAsync("alice", "BobPass22")).Should().BeNull();
            c2.Count.Should().Be(1);
        }

        // 4. Alice's right password authenticates alice via her v2 door: 1 Argon2 derivation + returns DEK.
        using (var c3 = KeyDerivation.BeginScopedCountForTests())
        {
            var (authed, dek) = await _userService.AuthenticateWithDekAsync("alice", "AlicePass1");
            c3.Count.Should().Be(1);
            authed.Should().NotBeNull();
            authed!.Id.Should().Be(alice.Id);
            dek.Should().NotBeNull();
            Array.Clear(dek!);
        }

        // Bob opens his own door too.
        (await _userService.AuthenticateAsync("bob", "BobPass22"))!.Id.Should().Be(bob.Id);
    }

    [Fact]
    public async Task ReplicatedUser_RefusesAllMutationsNamingAccountsNode_AndRecoverAccessHasNoOracle()
    {
        var alice = await SeedReplicatedUserAsync("alice", "AlicePass1");
        var regular = await _userService.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.User);
        var localAdmin = await _users.GetByUsernameAsync("localinstaller");
        localAdmin.Should().NotBeNull();
        var ownedRecoveryKey = await _keyMgmt.AddRecoveryKeyAsync(localAdmin!.Id);

        // Pre-2.12 ownerless recovery slot (the only kind of local recovery slot not tied to a single local owner).
        var (legacyRecoveryKey, legacySlotId) = await _keyMgmt.IssueRecoveryKeyAsync(localAdmin.Id, "pre-2.12");
        using (var conn = Factory.CreateConnection())
        {
            await Dapper.SqlMapper.ExecuteAsync(
                conn,
                "UPDATE tbl_key_slot SET owner_user_id = NULL WHERE slot_id = @Id",
                new { Id = legacySlotId });
        }

        var acts = new Func<Task>[]
        {
            () => _userService.ProvisionMissingKeySlotAsync(alice, "AlicePass1"),
            () => _userService.SetPasswordHintAsync(alice.Id, "new hint"),
            () => _userService.ChangePasswordAsync(alice.Id, "AlicePass1", "AliceNew1"),
            () => _userService.AdminChangePasswordAsync(alice.Id, "AliceNew1"),
            () => _userService.UpdateUserAsync(alice.Id, "Alice Renamed", UserRoles.Superadmin),
            () => _userService.DemoteSuperadminAsync(alice.Id, UserRoles.User, allowOpenRole: true),
            () => _userService.DeleteUserAsync(alice.Id),
            () => _keyMgmt.ChangePasswordAsync("AlicePass1", "AliceNew1"),
            () => _userService.CreateUserAsync("dave", "Dave", "DavePass1", UserRoles.Superadmin),
            () => _userService.UpdateUserAsync(regular.Id, "Carol", UserRoles.Superadmin),
        };

        foreach (var act in acts)
        {
            var ex = await act.Should().ThrowAsync<ReplicatedUserException>();
            ex.Which.Message.Should().Contain("PrimaryAccounts");
            ex.Which.Message.Should().Contain(_homeNodeId.ToString("D"));
        }

        // Recover-access with WRONG key returns WrongKey (no oracle distinguishing replicated from unknown).
        var wrongKeyRes = await _userService.ResetPasswordWithRecoveryKeyAsync("alice", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "AliceNew1");
        wrongKeyRes.Outcome.Should().Be(RecoveryResetOutcome.WrongKey);

        // Recover-access with another superadmin's personal (2.12) key returns NotEligible (no oracle).
        var otherOwnerRes = await _userService.ResetPasswordWithRecoveryKeyAsync("alice", ownedRecoveryKey, "AliceNew1");
        otherOwnerRes.Outcome.Should().Be(RecoveryResetOutcome.NotEligible);

        // Recover-access with an ownerless legacy key returns ReplicatedUser naming accounts home.
        var validKeyRes = await _userService.ResetPasswordWithRecoveryKeyAsync("alice", legacyRecoveryKey, "AliceNew1");
        validKeyRes.Outcome.Should().Be(RecoveryResetOutcome.ReplicatedUser);
        validKeyRes.AccountsHomeName.Should().Be("PrimaryAccounts");
        validKeyRes.AccountsHomeNodeId.Should().Be(_homeNodeId);
    }

    [Fact]
    public async Task AccountsNode_PublishesImmediatelyOnPasswordChangeDemotionDeletionAndPromotionFirstSignIn()
    {
        var selfId = (await _nodes.GetAsync())!.NodeId;
        await _homeRepo.UpsertAsync(new AccountsHomeRecord(selfId, 20, selfId, Guid.NewGuid(), DateTime.UtcNow));

        // 1. Create regular user and promote without password -> does not notify yet (no v2 door yet).
        var bob = await _userService.CreateUserAsync("bob", "Bob", "BobPass1", UserRoles.User);
        _notifier.ImmediateCount.Should().Be(0);

        await _userService.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin);
        _notifier.ImmediateCount.Should().Be(0);

        // 2. First sign-in provisions v2 door -> notifies immediately!
        var loadedBob = (await _users.GetByIdAsync(bob.Id))!;
        (await _userService.ProvisionMissingKeySlotAsync(loadedBob, "BobPass1")).Should().BeTrue();
        _notifier.ImmediateCount.Should().Be(1);

        // 3. Password change -> notifies immediately.
        await _userService.ChangePasswordAsync(bob.Id, "BobPass1", "BobPass2", passwordHint: "two");
        _notifier.ImmediateCount.Should().Be(2);

        // 4. Admin reset -> notifies immediately.
        await _userService.AdminChangePasswordAsync(bob.Id, "BobPass3");
        _notifier.ImmediateCount.Should().Be(3);

        // 5. Demotion -> notifies immediately.
        var carol = await _userService.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        _notifier.ImmediateCount.Should().Be(4);
        await _userService.DemoteSuperadminAsync(bob.Id, UserRoles.User, allowOpenRole: true);
        _notifier.ImmediateCount.Should().Be(5);

        // 6. Deletion -> notifies immediately.
        await _userService.DeleteUserAsync(carol.Id);
        _notifier.ImmediateCount.Should().Be(6);
    }
}
