using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Release 2.12, holes H4 and H5. Taking superadmin rights away, and deleting a user, are ONE transaction: role (or the account), the person's door, the folder rules they
/// get, remote tokens, agents and the security stamp change together or not at all. A demotion that does not say what the person may see afterwards is refused.
/// </summary>
public class UserDemotionTests : TestFixture
{
    private UserService Users = null!;
    private UserRepository UserRepo = null!;
    private KeySlotRepository KeySlots = null!;
    private RoleRepository Roles = null!;
    private FolderAccessService FolderAccess = null!;
    private AgentRepository AgentRepo = null!;
    private RemoteApiTokenRepository Tokens = null!;
    private FolderAclRepository UserAcls = null!;
    private FolderRepository Folders = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "AdminPass1");
        await Session.UnlockAsync("AdminPass1");

        UserRepo = new UserRepository(Factory);
        KeySlots = new KeySlotRepository(Factory);
        Roles = new RoleRepository(Factory);
        UserAcls = new FolderAclRepository(Factory);
        Folders = new FolderRepository(Factory, ScopeHolder);
        FolderAccess = new FolderAccessService(new ServiceCollection()
            .AddSingleton<IDbConnectionFactory>(_ => Factory)
            .AddScoped<IUserRepository>(_ => UserRepo)
            .AddScoped<IRoleRepository>(_ => Roles)
            .AddScoped<IRoleAclRepository>(_ => new RoleAclRepository(Factory))
            .AddScoped<IFolderAclRepository>(_ => UserAcls)
            .AddScoped<IFolderRepository>(_ => Folders)
            .AddScoped(_ => ScopeHolder)
            .BuildServiceProvider());
        AgentRepo = new AgentRepository(Factory);
        Tokens = new RemoteApiTokenRepository(Factory);
        Users = new UserService(UserRepo, KeySlots, Session, Roles, FolderAccess, remoteTokenRepo: Tokens, agentRepo: AgentRepo, factory: Factory);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<User> SuperadminAsync(string name) => await Users.CreateUserAsync(name, name, name + "Pass1", UserRoles.Superadmin);

    private async Task<Guid> FolderAsync(string path)
    {
        var id = Guid.NewGuid();
        await Folders.CreateAsync(new Folder
        {
            Id = id, Path = path, Name = path.TrimEnd('/').Split('/').Last(), ParentPath = "/", Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return id;
    }

    private async Task<int> AgentAsync(int ownerId, bool wrapped)
    {
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var masterDek = Session.GetMasterDek();
        try
        {
            var (ciphertext, iv, salt) = AgentKeyHelper.EncryptDekV1(apiKey, masterDek);
            return await AgentRepo.CreateAsync(new Agent
            {
                Name = "agent-" + Guid.NewGuid().ToString("N")[..6],
                KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey), KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
                EncryptedDek = wrapped ? ciphertext : null, DekIV = wrapped ? iv : null, Salt = wrapped ? salt : null, KdfVersion = wrapped ? 1 : 0,
                Status = "A", CreatedAt = DateTime.UtcNow, OwnerUserId = ownerId,
            });
        }
        finally { Array.Clear(masterDek); }
    }

    private Task TokenAsync(int userId) => Tokens.CreateAsync(new RemoteApiToken
    {
        Id = Guid.NewGuid(), UserId = userId, TokenHash = Guid.NewGuid().ToString("N"), Label = "t", CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(30),
    });

    private async Task<int> TokenCountAsync(int userId) => (await Tokens.ListByUserAsync(userId)).Count;

    private async Task<(string Status, bool Wrapped)> AgentRowAsync(int agentId)
    {
        using var conn = Factory.CreateConnection();
        var row = await conn.QuerySingleAsync<(string Status, int Wrapped)>(
            "SELECT status AS Status, CASE WHEN encrypted_dek IS NULL AND dek_iv IS NULL AND salt IS NULL THEN 0 ELSE 1 END AS Wrapped FROM tbl_agent WHERE id = @agentId", new { agentId });
        return (row.Status, row.Wrapped == 1);
    }

    private async Task<string> StampAsync(int userId) => (await UserRepo.GetSecurityStampAsync(userId))!;

    private async Task<List<FolderAclEntry>> RulesAsync(int userId) => await UserAcls.GetByUserIdAsync(userId);

    private async Task<List<(string Action, string EntityId, string Details)>> RecoveryRevokeAuditAsync()
    {
        using var conn = Factory.CreateConnection();
        return (await conn.QueryAsync<(string, string, string)>(
            "SELECT action, entity_id, details FROM tbl_audit_log WHERE action = 'recovery_key_revoked' ORDER BY id")).ToList();
    }

    private async Task<List<int>> RecoverySlotIdsOfAsync(int userId) =>
        (await KeySlots.GetAllAsync()).Where(s => s.SlotType == KeySlotTypes.Recovery && s.OwnerUserId == userId).Select(s => s.SlotId).ToList();

    /// <summary>Removes a person's password door directly, to reach the boundary where a recovery key is the only door a person can open.</summary>
    private async Task StripUserSlotAsync(int userId)
    {
        var u = (await UserRepo.GetByIdAsync(userId))!;
        if (u.KeySlotId is { } sid) { await KeySlots.DeleteAsync(sid); await UserRepo.ClearKeySlotAsync(sid); }
    }

    private async Task ClosedRoleAsync(string name, RoleBasePolicyKind policy = RoleBasePolicyKind.Closed) =>
        await Roles.CreateAsync(new Role
        {
            Name = name, DisplayName = name, IsSystem = false,
            BasePolicy = policy == RoleBasePolicyKind.Closed ? RoleBasePolicy.Closed : RoleBasePolicy.Open,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

    private enum RoleBasePolicyKind { Closed, Open }

    // ---------------------------------------------------------------- H5: tokens and agents do not survive

    [Fact]
    public async Task Demotion_DeletesTheRemoteTokens_AndRevokesTheAgents_OfTheDemotedUser()
    {
        var carol = await SuperadminAsync("carol");
        var wrapped = await AgentAsync(carol.Id, wrapped: true);
        var plain = await AgentAsync(carol.Id, wrapped: false);
        await TokenAsync(carol.Id);
        await TokenAsync(carol.Id);

        await Users.DemoteSuperadminAsync(carol.Id, UserRoles.User, allowOpenRole: true);

        (await TokenCountAsync(carol.Id)).Should().Be(0, "a remote token of a person who lost their rights does not outlive them");
        (await AgentRowAsync(wrapped)).Should().Be(("D", false), "revoked, and the wrapped master key is gone");
        (await AgentRowAsync(plain)).Should().Be(("D", false), "an agent without a wrapped key is revoked too: it would otherwise go on reading as the demoted person");
        (await AgentRepo.GetByIdAsync(wrapped)).Should().BeNull();
        (await AgentRepo.GetByIdAsync(plain)).Should().BeNull();
    }

    [Fact]
    public async Task Demotion_LeavesTheTokensAndAgentsOfOthersAlone()
    {
        var carol = await SuperadminAsync("carol");
        var dave = await SuperadminAsync("dave");
        var daveAgent = await AgentAsync(dave.Id, wrapped: true);
        await TokenAsync(dave.Id);

        await Users.DemoteSuperadminAsync(carol.Id, UserRoles.User, allowOpenRole: true);

        (await TokenCountAsync(dave.Id)).Should().Be(1);
        (await AgentRowAsync(daveAgent)).Should().Be(("A", true));
    }

    // Release 2.12 (WS-B2): a recovery key is personal, so it goes with the person who loses their rights.
    [Fact]
    public async Task Demotion_RevokesThePersonsRecoveryKeys_AndLeavesAnothersAlone()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var carol = await SuperadminAsync("carol");
        var adminKey = await KeyManagement.AddRecoveryKeyAsync(admin.Id);
        var carolKey = await KeyManagement.AddRecoveryKeyAsync(carol.Id);

        await Users.DemoteSuperadminAsync(carol.Id, UserRoles.User, allowOpenRole: true);

        var recovery = (await KeySlots.GetAllAsync()).Where(s => s.SlotType == KeySlotTypes.Recovery).ToList();
        recovery.Should().NotContain(s => s.OwnerUserId == carol.Id, "carol's recovery keys go with her");
        recovery.Should().Contain(s => s.OwnerUserId == admin.Id, "admin's recovery key stays");
        Session.Lock();
        (await Session.UnlockAsync(carolKey)).Should().BeFalse("carol's revoked key no longer opens the vault");
        (await Session.UnlockAsync(adminKey)).Should().BeTrue("admin's key still opens the vault");
    }

    [Fact]
    public async Task Deletion_RevokesThePersonsRecoveryKeys_AndLeavesAnothersAlone()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var carol = await SuperadminAsync("carol");
        var adminKey = await KeyManagement.AddRecoveryKeyAsync(admin.Id);
        var carolKey = await KeyManagement.AddRecoveryKeyAsync(carol.Id);

        await Users.DeleteUserAsync(carol.Id);

        var recovery = (await KeySlots.GetAllAsync()).Where(s => s.SlotType == KeySlotTypes.Recovery).ToList();
        recovery.Should().NotContain(s => s.OwnerUserId == carol.Id, "carol's recovery keys go with her");
        recovery.Should().Contain(s => s.OwnerUserId == admin.Id, "admin's recovery key stays");
        Session.Lock();
        (await Session.UnlockAsync(carolKey)).Should().BeFalse("carol's revoked key no longer opens the vault");
        (await Session.UnlockAsync(adminKey)).Should().BeTrue("admin's key still opens the vault");
    }

    [Fact]
    public async Task Deletion_DeletesTheRemoteTokens_AndRevokesTheAgents_OfAnyUser_NotOnlyASuperadmin()
    {
        var bob = await Users.CreateUserAsync("bob", "Bob", "BobPass1", UserRoles.User);
        var agent = await AgentAsync(bob.Id, wrapped: false);
        await TokenAsync(bob.Id);

        await Users.DeleteUserAsync(bob.Id);

        (await TokenCountAsync(bob.Id)).Should().Be(0);
        (await AgentRowAsync(agent)).Should().Be(("D", false), "an ordinary user's agent is revoked with its owner");
        (await UserRepo.GetByIdAsync(bob.Id)).Should().BeNull("the account itself is gone (inactive, name released)");
    }

    [Fact]
    public async Task Deletion_RevokesTheAgentsOfADeletedSuperadmin_AndDropsTheirDoor()
    {
        var keeper = await SuperadminAsync("keeper");
        var carol = await SuperadminAsync("carol");
        var agent = await AgentAsync(carol.Id, wrapped: true);
        await TokenAsync(carol.Id);
        var slots = (await KeySlots.GetAllAsync()).Count;

        await Users.DeleteUserAsync(carol.Id);

        (await AgentRowAsync(agent)).Should().Be(("D", false));
        (await TokenCountAsync(carol.Id)).Should().Be(0);
        (await KeySlots.GetAllAsync()).Should().HaveCount(slots - 1, "the deleted person's door is gone");
        keeper.KeySlotId.Should().NotBeNull();
    }

    [Fact]
    public async Task ADeletionThatIsRefused_TouchesNothing()
    {
        var solo = (await UserRepo.ListActiveAsync()).Single(u => u.Role == UserRoles.Superadmin);
        var agent = await AgentAsync(solo.Id, wrapped: true);
        await TokenAsync(solo.Id);

        var act = async () => await Users.DeleteUserAsync(solo.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*last superadmin*");
        (await AgentRowAsync(agent)).Should().Be(("A", true));
        (await TokenCountAsync(solo.Id)).Should().Be(1);
    }

    // ---------------------------------------------------------------- one transaction

    /// <summary>A key-slot repository whose delete fails: the last statement of the demotion, after everything else it did inside the transaction.</summary>
    private sealed class FailingSlotDelete(IKeySlotRepository inner) : IKeySlotRepository
    {
        public Task<List<MasterKeyStore>> GetAllAsync() => inner.GetAllAsync();
        public Task<int> CreateAsync(MasterKeyStore slot, IDbTransaction? transaction = null) => inner.CreateAsync(slot, transaction);
        public Task DeleteAsync(int slotId, IDbTransaction? transaction = null) => throw new InvalidOperationException("the disk said no");
        public Task UpdateSlotKeyAsync(int slotId, byte[] encryptedDek, byte[] iv) => inner.UpdateSlotKeyAsync(slotId, encryptedDek, iv);
        public Task<bool> ReplaceDoorAsync(MasterKeyStore door, byte[]? expectedEncryptedMasterDek, IDbTransaction? transaction = null) => inner.ReplaceDoorAsync(door, expectedEncryptedMasterDek, transaction);
        public Task SetDekFingerprintIfUnknownAsync(int slotId, byte[] encryptedMasterDek, string dekFingerprint) => inner.SetDekFingerprintIfUnknownAsync(slotId, encryptedMasterDek, dekFingerprint);
    }

    [Fact]
    public async Task ADemotionThatFailsHalfway_ChangesNothing()
    {
        var carol = await SuperadminAsync("carol");
        var agent = await AgentAsync(carol.Id, wrapped: true);
        await TokenAsync(carol.Id);
        var folder = await FolderAsync("/Public");
        var stampBefore = await StampAsync(carol.Id);
        var failing = new UserService(UserRepo, new FailingSlotDelete(KeySlots), Session, Roles, FolderAccess, remoteTokenRepo: Tokens, agentRepo: AgentRepo, factory: Factory);

        var act = async () => await failing.DemoteSuperadminAsync(carol.Id, UserRoles.User, [new DemotionFolderRule(folder, AclEffect.Allow)]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*the disk said no*");
        var after = (await UserRepo.GetByIdAsync(carol.Id))!;
        after.Role.Should().Be(UserRoles.Superadmin, "the role is written in the same transaction as the door's deletion");
        after.KeySlotId.Should().Be(carol.KeySlotId);
        (await StampAsync(carol.Id)).Should().Be(stampBefore);
        (await RulesAsync(carol.Id)).Should().BeEmpty("the folder rules are part of the same transaction");
        (await TokenCountAsync(carol.Id)).Should().Be(1);
        (await AgentRowAsync(agent)).Should().Be(("A", true));
        (await KeySlots.GetAllAsync()).Should().Contain(s => s.SlotId == carol.KeySlotId, "the door is still there");
    }

    [Fact]
    public async Task ADeletionThatFailsHalfway_ChangesNothing()
    {
        var keeper = await SuperadminAsync("keeper");
        var carol = await SuperadminAsync("carol");
        var agent = await AgentAsync(carol.Id, wrapped: true);
        await TokenAsync(carol.Id);
        var failing = new UserService(UserRepo, new FailingSlotDelete(KeySlots), Session, Roles, FolderAccess, remoteTokenRepo: Tokens, agentRepo: AgentRepo, factory: Factory);

        var act = async () => await failing.DeleteUserAsync(carol.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*the disk said no*");
        (await UserRepo.GetByIdAsync(carol.Id)).Should().NotBeNull("still an active user");
        (await TokenCountAsync(carol.Id)).Should().Be(1);
        (await AgentRowAsync(agent)).Should().Be(("A", true));
        keeper.Should().NotBeNull();
    }

    [Fact]
    public async Task ADemotion_ChangesRoleDoorStampAndRulesTogether_AndTheVeryNextFolderLookupSeesTheRules()
    {
        var carol = await SuperadminAsync("carol");
        var pub = await FolderAsync("/Public");
        var secret = await FolderAsync("/Secret");
        var stampBefore = await StampAsync(carol.Id);
        // Warm the folder-rule cache with the superadmin's empty answer: the demotion must not leave it behind.
        var (warmDeny, warmAllow, _) = await FolderAccess.GetFullAccessInfoAsync(carol.Id);
        FolderAccessService.IsAccessDenied(warmDeny, warmAllow, "/Secret").Should().BeFalse();

        await Users.DemoteSuperadminAsync(carol.Id, UserRoles.User, [new DemotionFolderRule(pub, AclEffect.Allow, IsReadOnly: true)]);

        var after = (await UserRepo.GetByIdAsync(carol.Id))!;
        after.Role.Should().Be(UserRoles.User);
        after.KeySlotId.Should().BeNull("their door is deleted");
        (await StampAsync(carol.Id)).Should().NotBe(stampBefore, "every cookie of theirs is rejected at its next check");
        (await RulesAsync(carol.Id)).Should().ContainSingle(r => r.FolderId == pub && r.Effect == AclEffect.Allow && r.IsReadOnly);

        // No window: the first thing the person's request sees is the restricted view.
        var (deny, allow, readOnly) = await FolderAccess.GetFullAccessInfoAsync(carol.Id);
        FolderAccessService.IsAccessDenied(deny, allow, "/Secret").Should().BeTrue();
        FolderAccessService.IsAccessDenied(deny, allow, "/Public/Notes").Should().BeFalse();
        readOnly.Should().Contain("/Public");
        secret.Should().NotBe(Guid.Empty);
    }

    /// <summary>A user repository that answers the first lookup of one user with a snapshot taken earlier: what a demotion sees when the person's password changed after it loaded them.</summary>
    private sealed class StaleFirstLookup(IUserRepository inner, User stale) : IUserRepository
    {
        private bool _served;
        public Task<User?> GetByIdAsync(int id)
        {
            if (!_served && id == stale.Id) { _served = true; return Task.FromResult<User?>(stale); }
            return inner.GetByIdAsync(id);
        }
        public Task<User?> GetByUsernameAsync(string username) => inner.GetByUsernameAsync(username);
        public Task<List<User>> ListActiveAsync() => inner.ListActiveAsync();
        public Task<int> CreateAsync(User user) => inner.CreateAsync(user);
        public Task UpdateAsync(User user) => inner.UpdateAsync(user);
        public Task DeleteAsync(int id, string releasedUsername) => inner.DeleteAsync(id, releasedUsername);
        public Task UpdateLastLoginAsync(int id) => inner.UpdateLastLoginAsync(id);
        public Task RepointKeySlotAsync(int oldSlotId, int newSlotId) => inner.RepointKeySlotAsync(oldSlotId, newSlotId);
        public Task<bool> TryAssignKeySlotAsync(int userId, int slotId) => inner.TryAssignKeySlotAsync(userId, slotId);
        public Task<bool> TryReplacePasswordAsync(int userId, string expectedRole, int? expectedKeySlotId, int? newKeySlotId, string passwordHash, bool setHint, string? passwordHint) => inner.TryReplacePasswordAsync(userId, expectedRole, expectedKeySlotId, newKeySlotId, passwordHash, setHint, passwordHint);
        public Task ClearKeySlotAsync(int slotId) => inner.ClearKeySlotAsync(slotId);
        public Task<List<int>> GetUserIdsByRoleAsync(string role) => inner.GetUserIdsByRoleAsync(role);
        public Task<Dictionary<string, int>> CountActiveUsersPerRoleAsync() => inner.CountActiveUsersPerRoleAsync();
        public Task<string?> GetSecurityStampAsync(int id) => inner.GetSecurityStampAsync(id);
        public Task<string?> GetPasswordHintAsync(string username) => inner.GetPasswordHintAsync(username);
        public Task SetPasswordHintAsync(int id, string? hint) => inner.SetPasswordHintAsync(id, hint);
        public Task<string> BumpSecurityStampAsync(int id) => inner.BumpSecurityStampAsync(id);
    }

    [Fact]
    public async Task APasswordChangeBetweenTheLoadAndTheCommit_DoesNotLeaveTheNewDoorBehind()
    {
        var carol = await SuperadminAsync("carol");
        var stale = (await UserRepo.GetByIdAsync(carol.Id))!;          // loaded before the change below
        await Users.AdminChangePasswordAsync(carol.Id, "CarolNewPass1");   // the slot is replaced: the row names a NEW one
        var newSlot = (await UserRepo.GetByIdAsync(carol.Id))!.KeySlotId!.Value;
        newSlot.Should().NotBe(stale.KeySlotId!.Value);
        var racing = new UserService(new StaleFirstLookup(UserRepo, stale), KeySlots, Session, Roles, FolderAccess, remoteTokenRepo: Tokens, agentRepo: AgentRepo, factory: Factory);

        await racing.DemoteSuperadminAsync(carol.Id, UserRoles.User, allowOpenRole: true);

        (await KeySlots.GetAllAsync()).Should().NotContain(s => s.SlotId == newSlot, "the door the person holds NOW is deleted, not only the one the call was loaded with");
        Session.Lock();
        (await Session.UnlockAsync("CarolNewPass1")).Should().BeFalse("their password opens nothing any more");
    }

    [Theory]
    [InlineData(false)]   // an admin's reset
    [InlineData(true)]    // the person's own change
    public async Task APasswordChangeLoadedBeforeADemotion_DoesNotWriteTheSuperadminRoleBackOrBringTheDoorBack(bool ownChange)
    {
        var carol = await SuperadminAsync("carol");
        var stale = (await UserRepo.GetByIdAsync(carol.Id))!;   // what the password change loaded, before the demotion below
        await Users.DemoteSuperadminAsync(carol.Id, UserRoles.User, allowOpenRole: true);
        var demoted = (await UserRepo.GetByIdAsync(carol.Id))!;
        demoted.Role.Should().Be(UserRoles.User);
        var racing = new UserService(new StaleFirstLookup(UserRepo, stale), KeySlots, Session, Roles, FolderAccess, remoteTokenRepo: Tokens, agentRepo: AgentRepo, factory: Factory);

        var change = ownChange
            ? () => racing.ChangePasswordAsync(carol.Id, "carolPass1", "CarolNewPass1")
            : (Func<Task>)(() => racing.AdminChangePasswordAsync(carol.Id, "CarolNewPass1"));
        await change.Should().ThrowAsync<InvalidOperationException>("the account changed under the replacement: nothing is written, and the caller is told to look again");

        var after = (await UserRepo.GetByIdAsync(carol.Id))!;
        after.Role.Should().Be(UserRoles.User, "a write made from the old object must not give the superadmin role back");
        after.KeySlotId.Should().BeNull("nor a door");
        after.PasswordHash.Should().Be(demoted.PasswordHash, "the replacement did not happen");
        (await KeySlots.GetAllAsync()).Count(s => s.SlotType == KeySlotTypes.User).Should().Be(1, "only the administrator's door is left: the one the replacement made was dropped");
        Session.Lock();
        (await Session.UnlockAsync("CarolNewPass1")).Should().BeFalse("the new password opens nothing");
    }

    // ---------------------------------------------------------------- H4: an explicit access choice

    [Fact]
    public async Task ADemotionThatSaysNothingAboutAccess_IsRefused_AndChangesNothing()
    {
        var carol = await SuperadminAsync("carol");
        var agent = await AgentAsync(carol.Id, wrapped: true);
        var stampBefore = await StampAsync(carol.Id);

        var act = async () => await Users.DemoteSuperadminAsync(carol.Id, UserRoles.User);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Say which folders*");
        (await UserRepo.GetByIdAsync(carol.Id))!.Role.Should().Be(UserRoles.Superadmin);
        (await StampAsync(carol.Id)).Should().Be(stampBefore);
        (await AgentRowAsync(agent)).Should().Be(("A", true));
    }

    [Fact]
    public async Task TheBuiltInOpenUserRole_IsAcceptedOnlyWhenTheCallerSaysSo()
    {
        var carol = await SuperadminAsync("carol");
        var dave = await SuperadminAsync("dave");

        var refused = async () => await Users.DemoteSuperadminAsync(carol.Id, UserRoles.User, allowOpenRole: false);
        await refused.Should().ThrowAsync<InvalidOperationException>();

        await Users.DemoteSuperadminAsync(dave.Id, UserRoles.User, allowOpenRole: true);
        (await UserRepo.GetByIdAsync(dave.Id))!.Role.Should().Be(UserRoles.User);
        (await UserRepo.GetByIdAsync(carol.Id))!.Role.Should().Be(UserRoles.Superadmin);
    }

    [Fact]
    public async Task ARoleThatIsNotOpen_IsAnAccessChoiceOnItsOwn()
    {
        await ClosedRoleAsync("user-closed");
        var carol = await SuperadminAsync("carol");

        await Users.DemoteSuperadminAsync(carol.Id, "user-closed");

        (await UserRepo.GetByIdAsync(carol.Id))!.Role.Should().Be("user-closed");
        var (deny, allow, _) = await FolderAccess.GetFullAccessInfoAsync(carol.Id);
        FolderAccessService.IsAccessDenied(deny, allow, "/Anything").Should().BeTrue("a closed role sees nothing until it is told what to");
    }

    [Fact]
    public async Task AnOpenCustomRole_NeedsTheExplicitConfirmationToo_UnlessItHasAllowRules()
    {
        await ClosedRoleAsync("user-open", RoleBasePolicyKind.Open);
        await ClosedRoleAsync("user-open-listed", RoleBasePolicyKind.Open);
        var listed = await FolderAsync("/Listed");
        await new RoleAclRepository(Factory).AddAsync(new RoleAclEntry
        {
            RoleName = "user-open-listed", FolderId = listed, Effect = AclEffect.Allow, CreatedAt = DateTime.UtcNow,
        });
        var carol = await SuperadminAsync("carol");
        var dave = await SuperadminAsync("dave");

        var refused = async () => await Users.DemoteSuperadminAsync(carol.Id, "user-open");
        await refused.Should().ThrowAsync<InvalidOperationException>();

        await Users.DemoteSuperadminAsync(dave.Id, "user-open-listed");
        (await UserRepo.GetByIdAsync(dave.Id))!.Role.Should().Be("user-open-listed", "an allow list is a choice");
    }

    [Fact]
    public async Task FolderRules_ReplaceTheRowsLeftFromBeforeAPromotion_AndAreOnlyForTheBuiltInUserRole()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.User);
        var old = await FolderAsync("/Old");
        var fresh = await FolderAsync("/Fresh");
        await UserAcls.AddAsync(new FolderAclEntry { UserId = carol.Id, FolderId = old, Effect = AclEffect.Allow, CreatedAt = DateTime.UtcNow });
        await Users.UpdateUserAsync(carol.Id, "Carol", UserRoles.Superadmin);   // the old row is inert for a superadmin
        await ClosedRoleAsync("user-closed");

        var withCustomRole = async () => await Users.DemoteSuperadminAsync(carol.Id, "user-closed", [new DemotionFolderRule(fresh, AclEffect.Allow)]);
        await withCustomRole.Should().ThrowAsync<ArgumentException>().WithMessage("*built-in 'user' role*");
        (await RulesAsync(carol.Id)).Should().ContainSingle(r => r.FolderId == old, "a refused demotion writes nothing");

        await Users.DemoteSuperadminAsync(carol.Id, UserRoles.User, [new DemotionFolderRule(fresh, AclEffect.Allow)]);

        (await RulesAsync(carol.Id)).Should().ContainSingle(r => r.FolderId == fresh, "the choice replaces the rows nobody had looked at");
    }

    [Fact]
    public async Task AFolderThatDoesNotExist_IsRefused_BeforeAnythingChanges()
    {
        var carol = await SuperadminAsync("carol");

        var act = async () => await Users.DemoteSuperadminAsync(carol.Id, UserRoles.User, [new DemotionFolderRule(Guid.NewGuid(), AclEffect.Allow)]);

        await act.Should().ThrowAsync<ArgumentException>();
        (await UserRepo.GetByIdAsync(carol.Id))!.Role.Should().Be(UserRoles.Superadmin);
    }

    [Fact]
    public async Task OnlyASuperadmin_CanBeDemoted_ToARoleThatIsNotSuperadmin_AndNotTheLastOne()
    {
        var bob = await Users.CreateUserAsync("bob", "Bob", "BobPass1", UserRoles.User);
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var carol = await SuperadminAsync("carol");

        await FluentActions.Awaiting(() => Users.DemoteSuperadminAsync(bob.Id, UserRoles.User, allowOpenRole: true))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a superadmin*");
        await FluentActions.Awaiting(() => Users.DemoteSuperadminAsync(carol.Id, UserRoles.Superadmin, allowOpenRole: true))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Users.DemoteSuperadminAsync(carol.Id, "no-such-role", allowOpenRole: true))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Users.DemoteSuperadminAsync(9999, UserRoles.User, allowOpenRole: true))
            .Should().ThrowAsync<KeyNotFoundException>();

        await Users.DemoteSuperadminAsync(carol.Id, UserRoles.User, allowOpenRole: true);
        await FluentActions.Awaiting(() => Users.DemoteSuperadminAsync(admin.Id, UserRoles.User, allowOpenRole: true))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*last superadmin*");
    }

    [Fact]
    public async Task ARoleStringWrittenOnItsOwn_CanNoLongerTakeSuperadminRightsAway()
    {
        var carol = await SuperadminAsync("carol");

        var act = async () => await Users.UpdateUserAsync(carol.Id, "Carol", UserRoles.User);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*explicit choice*");
        (await UserRepo.GetByIdAsync(carol.Id))!.Role.Should().Be(UserRoles.Superadmin);
    }

    [Fact]
    public async Task ADemotion_CarriesTheDisplayNameAndChatAccessOfTheDialog()
    {
        var carol = await SuperadminAsync("carol");

        await Users.DemoteSuperadminAsync(carol.Id, UserRoles.User, allowOpenRole: true, displayName: "  Carol B  ", chatAccess: false);

        var after = (await UserRepo.GetByIdAsync(carol.Id))!;
        after.DisplayName.Should().Be("Carol B");
        after.ChatAccess.Should().BeFalse();
    }

    [Fact]
    public async Task TheCallerStateCache_IsDroppedByTheDemotion_ByTheDeletion_AndByARoleChange()
    {
        var carol = await SuperadminAsync("carol");
        var dave = await SuperadminAsync("dave");
        var erin = await Users.CreateUserAsync("erin", "Erin", "ErinPass1", UserRoles.User);
        var db = Factory.DatabaseId;

        (await CallerStateCache.GetRoleAsync(db, carol.Id, UserRepo)).Should().Be(UserRoles.Superadmin);
        (await CallerStateCache.GetRoleAsync(db, dave.Id, UserRepo)).Should().Be(UserRoles.Superadmin);
        (await CallerStateCache.GetRoleAsync(db, erin.Id, UserRepo)).Should().Be(UserRoles.User);

        await Users.DemoteSuperadminAsync(carol.Id, UserRoles.User, allowOpenRole: true);
        await Users.DeleteUserAsync(dave.Id);
        await Users.UpdateUserAsync(erin.Id, "Erin", UserRoles.Superadmin);
        (await CallerStateCache.GetRoleAsync(db, erin.Id, UserRepo)).Should().Be(UserRoles.Superadmin, "a promotion drops the answer too");

        (await CallerStateCache.GetRoleAsync(db, carol.Id, UserRepo)).Should().Be(UserRoles.User, "not after the cache's time-to-live: the change dropped the answer");
        (await CallerStateCache.GetRoleAsync(db, dave.Id, UserRepo)).Should().BeNull("a deleted user is not a user");
    }

    // ---------------------------------------------------------------- The frame audits the recovery keys it revokes

    [Fact]
    public async Task Demotion_AuditsEachRecoveryKeyItRevokes_NamingTheOwnerAndTheAdministrator()
    {
        var carol = await SuperadminAsync("carol");
        await KeyManagement.AddRecoveryKeyAsync(carol.Id);
        await KeyManagement.AddRecoveryKeyAsync(carol.Id);
        var slotIds = await RecoverySlotIdsOfAsync(carol.Id);
        slotIds.Should().HaveCount(2);

        await Users.DemoteSuperadminAsync(carol.Id, UserRoles.User, allowOpenRole: true, actor: "7");

        var entries = await RecoveryRevokeAuditAsync();
        entries.Should().HaveCount(2, "each recovery key that goes with the person is audited, not just the demotion itself");
        entries.Select(e => e.EntityId).Should().BeEquivalentTo(slotIds.Select(id => id.ToString()));
        entries.Should().OnlyContain(e => e.Details.Contains($"owner user #{carol.Id}") && e.Details.Contains("user 7"));
        (await RecoverySlotIdsOfAsync(carol.Id)).Should().BeEmpty("the keys really were revoked");
    }

    [Fact]
    public async Task Deletion_AuditsEachRecoveryKeyItRevokes_NamingTheOwnerAndTheAdministrator()
    {
        var carol = await SuperadminAsync("carol");
        await KeyManagement.AddRecoveryKeyAsync(carol.Id);
        var slotIds = await RecoverySlotIdsOfAsync(carol.Id);

        await Users.DeleteUserAsync(carol.Id, actor: "7");

        var entry = (await RecoveryRevokeAuditAsync()).Should().ContainSingle().Which;
        entry.EntityId.Should().Be(slotIds.Single().ToString());
        entry.Details.Should().Contain($"owner user #{carol.Id}").And.Contain("user 7");
    }

    [Fact]
    public async Task Demotion_WithNoNamedActor_AuditsTheRecoveryKeyAsALocalCaller()
    {
        var carol = await SuperadminAsync("carol");
        await KeyManagement.AddRecoveryKeyAsync(carol.Id);

        await Users.DemoteSuperadminAsync(carol.Id, UserRoles.User, allowOpenRole: true);

        var entry = (await RecoveryRevokeAuditAsync()).Should().ContainSingle().Which;
        entry.Details.Should().Contain("a local caller").And.Contain($"owner user #{carol.Id}");
    }

    // ---------------------------------------------------------------- The frame's own last-person-door guard

    [Fact]
    public async Task Demotion_ThatWouldRemoveTheLastPersonDoor_IsRefused_AndTheKeyStays()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var carol = await SuperadminAsync("carol");
        await KeyManagement.AddRecoveryKeyAsync(carol.Id);
        var carolKey = (await RecoverySlotIdsOfAsync(carol.Id)).Single();
        // Two active superadmins, neither with a password door: carol's recovery key is the only door a person can open. The old
        // "another superadmin holds a key slot" guard is skipped (carol holds no user slot), so only the frame's own guard can catch this.
        await StripUserSlotAsync(admin.Id);
        await StripUserSlotAsync(carol.Id);

        var act = async () => await Users.DemoteSuperadminAsync(carol.Id, UserRoles.User, allowOpenRole: true);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*last door a person can open*");
        (await KeySlots.GetAllAsync()).Should().Contain(s => s.SlotId == carolKey, "the refusal revoked nothing");
        (await UserRepo.GetByIdAsync(carol.Id))!.Role.Should().Be(UserRoles.Superadmin, "the demotion did not happen");
    }

    [Fact]
    public async Task Deletion_ThatWouldRemoveTheLastPersonDoor_IsRefused_AndTheKeyStays()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var carol = await SuperadminAsync("carol");
        await KeyManagement.AddRecoveryKeyAsync(carol.Id);
        var carolKey = (await RecoverySlotIdsOfAsync(carol.Id)).Single();
        await StripUserSlotAsync(admin.Id);
        await StripUserSlotAsync(carol.Id);

        var act = async () => await Users.DeleteUserAsync(carol.Id);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*last door a person can open*");
        (await KeySlots.GetAllAsync()).Should().Contain(s => s.SlotId == carolKey, "the refusal revoked nothing");
        (await UserRepo.GetByIdAsync(carol.Id))!.IsActive.Should().BeTrue("the deletion did not happen");
    }

    [Fact]
    public async Task Demotion_WhenAnotherPersonsRecoveryKeyRemains_IsAllowed()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var carol = await SuperadminAsync("carol");
        await KeyManagement.AddRecoveryKeyAsync(admin.Id);
        await KeyManagement.AddRecoveryKeyAsync(carol.Id);
        await StripUserSlotAsync(admin.Id);
        await StripUserSlotAsync(carol.Id);

        await Users.DemoteSuperadminAsync(carol.Id, UserRoles.User, allowOpenRole: true);

        (await RecoverySlotIdsOfAsync(carol.Id)).Should().BeEmpty("carol's key went with her");
        (await RecoverySlotIdsOfAsync(admin.Id)).Should().HaveCount(1, "admin's recovery key is still a door a person can open");
    }

    // ---------------------------------------------------------------- 2.12: a deleted door and the quiet rotation

    private sealed class RepublishRecorder : IRecoveryBoxPublisher
    {
        public int Republished { get; private set; }
        public Task PublishDeviceBoxAsync(MasterKeyStore slot, byte[] dek) => Task.CompletedTask;
        public Task RepublishForDoorsAsync(byte[] dek)
        {
            Republished++;
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADemotionOrDeletion_RepublishesTheDeviceBox_AndEndsTheBridgeWhenItTookTheLastV1Door(bool demote)
    {
        var carol = await SuperadminAsync("carol");
        var slotId = carol.KeySlotId!.Value;
        using (var c = Factory.CreateConnection())
        {
            // Carol's door is the node's only v1 door (a straggler), so the node keeps the chain of an earlier rotation.
            await c.ExecuteAsync("UPDATE tbl_key_slot SET door_version = 1, door_public_key = NULL WHERE slot_id = @slotId", new { slotId });
            var now = DateTime.UtcNow.ToString("O");
            await c.ExecuteAsync(
                @"INSERT INTO tbl_dek_rotation_state (event_id, state, rotation_ts, created_at, updated_at, chain_encrypted_new_dek, chain_iv)
                  VALUES ('r1', 'APPLIED', @now, @now, @now, 'chain', 'iv')", new { now });
        }
        var recorder = new RepublishRecorder();
        var users = new UserService(UserRepo, KeySlots, Session, Roles, FolderAccess, remoteTokenRepo: Tokens, agentRepo: AgentRepo,
            recoveryBoxes: recorder, factory: Factory);

        if (demote) await users.DemoteSuperadminAsync(carol.Id, UserRoles.User, allowOpenRole: true);
        else await users.DeleteUserAsync(carol.Id);

        (await KeySlots.GetAllAsync()).Should().NotContain(s => s.SlotId == slotId);
        recorder.Republished.Should().Be(1, "the device box may have copied the deleted door");
        using (var c = Factory.CreateConnection())
            (await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_dek_rotation_state WHERE chain_encrypted_new_dek IS NOT NULL"))
                .Should().Be(0, "the last v1 door went in the same transaction, and with it the bridge");
    }
}
