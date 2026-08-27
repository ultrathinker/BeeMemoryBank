using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Covers the key-slot side of user management. A superadmin's key slot wraps the vault's
/// master DEK with a KEK derived from their plaintext password, so every path that hands a
/// user the superadmin role has to answer the question "where does the plaintext come from?".
/// </summary>
public class UserServiceTests : TestFixture
{
    private UserService Users = null!;
    private UserRepository UserRepo = null!;
    private KeySlotRepository KeySlots = null!;
    private RoleRepository Roles = null!;
    private FolderAccessService FolderAccess = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "AdminPass1");
        await Session.UnlockAsync("AdminPass1");

        UserRepo = new UserRepository(Factory);
        KeySlots = new KeySlotRepository(Factory);
        Roles = new RoleRepository(Factory);
        FolderAccess = new FolderAccessService(new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton<Core.Interfaces.IDbConnectionFactory>(_ => Factory)
            .AddScoped<Core.Interfaces.IUserRepository>(_ => UserRepo)
            .AddScoped<Core.Interfaces.IRoleRepository>(_ => Roles)
            .AddScoped<Core.Interfaces.IRoleAclRepository>(_ => new RoleAclRepository(Factory))
            .AddScoped<Core.Interfaces.IFolderAclRepository>(_ => new FolderAclRepository(Factory))
            .AddScoped<Core.Interfaces.IFolderRepository>(_ => new FolderRepository(Factory, ScopeHolder))
            .AddScoped(_ => ScopeHolder)
            .BuildServiceProvider());
        Users = new UserService(UserRepo, KeySlots, Session, Roles, FolderAccess);
    }

    private async Task<User> CreateRegularUserAsync(string username = "bob", string password = "BobPass1")
        => await Users.CreateUserAsync(username, username, password, UserRoles.User);

    [Fact]
    public async Task Promote_WithoutPassword_Succeeds_AndDefersKeySlot()
    {
        var bob = await CreateRegularUserAsync();

        await Users.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin);

        var updated = await UserRepo.GetByIdAsync(bob.Id);
        updated!.Role.Should().Be(UserRoles.Superadmin);
        updated.KeySlotId.Should().BeNull("the promoting admin never had Bob's plaintext password");
    }

    [Fact]
    public async Task Promote_LeavesLoginPasswordUntouched()
    {
        var bob = await CreateRegularUserAsync();

        await Users.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin);

        (await Users.AuthenticateAsync("bob", "BobPass1")).Should().NotBeNull();
    }

    [Fact]
    public async Task ProvisionMissingKeySlot_AtLogin_CreatesUnlockableSlot()
    {
        var bob = await CreateRegularUserAsync();
        await Users.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin);

        // What the login endpoint does once the password is in hand.
        var authenticated = await Users.AuthenticateAsync("bob", "BobPass1");
        var provisioned = await Users.ProvisionMissingKeySlotAsync(authenticated!, "BobPass1");

        provisioned.Should().BeTrue();
        (await UserRepo.GetByIdAsync(bob.Id))!.KeySlotId.Should().NotBeNull();

        Session.Lock();
        (await Session.UnlockAsync("BobPass1")).Should().BeTrue("Bob's own password now unwraps the DEK");
    }

    [Fact]
    public async Task ProvisionMissingKeySlot_IsIdempotent()
    {
        var bob = await CreateRegularUserAsync();
        await Users.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin);

        var user = (await UserRepo.GetByIdAsync(bob.Id))!;
        (await Users.ProvisionMissingKeySlotAsync(user, "BobPass1")).Should().BeTrue();
        var slotsAfterFirst = (await KeySlots.GetAllAsync()).Count;

        (await Users.ProvisionMissingKeySlotAsync(user, "BobPass1")).Should().BeFalse();
        (await KeySlots.GetAllAsync()).Should().HaveCount(slotsAfterFirst);
    }

    [Fact]
    public async Task ProvisionMissingKeySlot_SkipsNonSuperadmin()
    {
        var bob = await CreateRegularUserAsync();

        (await Users.ProvisionMissingKeySlotAsync(bob, "BobPass1")).Should().BeFalse();
        (await KeySlots.GetAllAsync()).Should().HaveCount(1, "only the admin's slot exists");
    }

    [Fact]
    public async Task ProvisionMissingKeySlot_WhileLocked_IsSkipped_NotFatal()
    {
        var bob = await CreateRegularUserAsync();
        await Users.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin);
        var user = (await UserRepo.GetByIdAsync(bob.Id))!;

        Session.Lock();

        (await Users.ProvisionMissingKeySlotAsync(user, "BobPass1")).Should().BeFalse();
        (await UserRepo.GetByIdAsync(bob.Id))!.KeySlotId.Should().BeNull();
    }

    [Fact]
    public async Task Promote_WithPassword_CreatesSlotImmediately_AndResetsLoginPassword()
    {
        var bob = await CreateRegularUserAsync();

        await Users.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin, "NewPass1");

        var updated = await UserRepo.GetByIdAsync(bob.Id);
        updated!.KeySlotId.Should().NotBeNull();

        // Slot password and login password are the same secret — they must not drift apart.
        (await Users.AuthenticateAsync("bob", "NewPass1")).Should().NotBeNull();
        (await Users.AuthenticateAsync("bob", "BobPass1")).Should().BeNull();

        Session.Lock();
        (await Session.UnlockAsync("NewPass1")).Should().BeTrue();
    }

    [Fact]
    public async Task AdminChangePassword_ProvisionsMissingSlotForSuperadmin()
    {
        var bob = await CreateRegularUserAsync();
        await Users.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin);

        await Users.AdminChangePasswordAsync(bob.Id, "ResetPass1");

        (await UserRepo.GetByIdAsync(bob.Id))!.KeySlotId.Should().NotBeNull();
        Session.Lock();
        (await Session.UnlockAsync("ResetPass1")).Should().BeTrue();
    }

    [Fact]
    public async Task AdminChangePassword_RewrapsAnExistingSlot_AndRetiresTheOldOne()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var oldSlotId = carol.KeySlotId!.Value;
        var slotCount = (await KeySlots.GetAllAsync()).Count;

        await Users.AdminChangePasswordAsync(carol.Id, "ResetPass1");

        var stored = (await UserRepo.GetByIdAsync(carol.Id))!;
        stored.KeySlotId.Should().NotBe(oldSlotId);
        (await KeySlots.GetAllAsync()).Should().HaveCount(slotCount, "the old slot must be retired, not kept alongside");

        Session.Lock();
        (await Session.UnlockAsync("CarolPass1")).Should().BeFalse("the old password must stop unwrapping the DEK");
        (await Session.UnlockAsync("ResetPass1")).Should().BeTrue();
    }

    [Fact]
    public async Task ChangePassword_RewrapsAnExistingSlot_AndRetiresTheOldOne()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var oldSlotId = carol.KeySlotId!.Value;
        var slotCount = (await KeySlots.GetAllAsync()).Count;

        await Users.ChangePasswordAsync(carol.Id, "CarolPass1", "SelfPass1");

        (await UserRepo.GetByIdAsync(carol.Id))!.KeySlotId.Should().NotBe(oldSlotId);
        (await KeySlots.GetAllAsync()).Should().HaveCount(slotCount);

        Session.Lock();
        (await Session.UnlockAsync("CarolPass1")).Should().BeFalse();
        (await Session.UnlockAsync("SelfPass1")).Should().BeTrue();
    }

    [Fact]
    public async Task AdminChangePassword_DoesNotGiveRegularUserASlot()
    {
        var bob = await CreateRegularUserAsync();

        await Users.AdminChangePasswordAsync(bob.Id, "ResetPass1");

        (await UserRepo.GetByIdAsync(bob.Id))!.KeySlotId.Should().BeNull();
        (await KeySlots.GetAllAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Demote_RemovesKeySlot()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        carol.KeySlotId.Should().NotBeNull();

        await Users.UpdateUserAsync(carol.Id, "Carol", UserRoles.User);

        (await UserRepo.GetByIdAsync(carol.Id))!.KeySlotId.Should().BeNull();
        Session.Lock();
        (await Session.UnlockAsync("CarolPass1")).Should().BeFalse();
        (await Session.UnlockAsync("AdminPass1")).Should().BeTrue();
    }

    [Fact]
    public async Task Demote_RefusesToDropTheLastKeySlot()
    {
        // Promote Bob so the "last superadmin" guard passes, but leave his slot unprovisioned —
        // the admin's slot is then the only one that can still open the vault.
        var bob = await CreateRegularUserAsync();
        await Users.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin);

        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var act = async () => await Users.UpdateUserAsync(admin.Id, "admin", UserRoles.User);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*only remaining way to unlock*");
        (await KeySlots.GetAllAsync()).Should().HaveCount(1);
        (await UserRepo.GetByIdAsync(admin.Id))!.Role.Should().Be(UserRoles.Superadmin);
    }

    [Fact]
    public async Task Demote_RefusesEvenWhenARecoverySlotPadsTheSlotCount()
    {
        // A recovery slot opens only with the recovery key, so it does not keep any superadmin
        // able to unlock with a password — counting rows in tbl_key_slot would wave this through.
        await KeyManagement.AddRecoveryKeyAsync();
        var bob = await CreateRegularUserAsync();
        await Users.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin);
        (await KeySlots.GetAllAsync()).Should().HaveCount(2, "admin's slot + the recovery slot");

        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var act = async () => await Users.UpdateUserAsync(admin.Id, "admin", UserRoles.User);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*only remaining way to unlock*");
        (await UserRepo.GetByIdAsync(admin.Id))!.KeySlotId.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_RefusesEvenWhenARecoverySlotPadsTheSlotCount()
    {
        await KeyManagement.AddRecoveryKeyAsync();
        var bob = await CreateRegularUserAsync();
        await Users.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin);

        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        var act = async () => await Users.DeleteUserAsync(admin.Id);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*only remaining way to unlock*");
        (await UserRepo.GetByIdAsync(admin.Id))!.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Demote_AllowedOnceTheOtherSuperadminHasProvisionedTheirSlot()
    {
        var bob = await CreateRegularUserAsync();
        await Users.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin);
        await Users.ProvisionMissingKeySlotAsync((await UserRepo.GetByIdAsync(bob.Id))!, "BobPass1");

        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;
        await Users.UpdateUserAsync(admin.Id, "admin", UserRoles.User);

        (await UserRepo.GetByIdAsync(admin.Id))!.KeySlotId.Should().BeNull();
        Session.Lock();
        (await Session.UnlockAsync("BobPass1")).Should().BeTrue();
        (await Session.UnlockAsync("AdminPass1")).Should().BeFalse();
    }

    [Fact]
    public async Task ProvisionMissingKeySlot_LosingARace_DiscardsItsOwnSlot()
    {
        var bob = await CreateRegularUserAsync();
        await Users.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin);
        var stale = (await UserRepo.GetByIdAsync(bob.Id))!;

        // A concurrent login provisions first; `stale` still says KeySlotId == null.
        await Users.ProvisionMissingKeySlotAsync((await UserRepo.GetByIdAsync(bob.Id))!, "BobPass1");
        var winner = (await UserRepo.GetByIdAsync(bob.Id))!.KeySlotId;
        var slotsAfterWinner = (await KeySlots.GetAllAsync()).Count;

        (await Users.ProvisionMissingKeySlotAsync(stale, "BobPass1")).Should().BeFalse();

        (await UserRepo.GetByIdAsync(bob.Id))!.KeySlotId.Should().Be(winner);
        (await KeySlots.GetAllAsync()).Should().HaveCount(slotsAfterWinner,
            "the loser's slot would otherwise linger and keep answering to that password forever");
    }

    [Fact]
    public async Task ProvisionMissingKeySlot_DoesNotResurrectAConcurrentlyDemotedUser()
    {
        var bob = await CreateRegularUserAsync();
        await Users.UpdateUserAsync(bob.Id, "Bob", UserRoles.Superadmin);
        var stale = (await UserRepo.GetByIdAsync(bob.Id))!;

        // An admin demotes Bob while his login is still deriving the KEK.
        await Users.UpdateUserAsync(bob.Id, "Bob", UserRoles.User);
        var slotsBefore = (await KeySlots.GetAllAsync()).Count;

        (await Users.ProvisionMissingKeySlotAsync(stale, "BobPass1")).Should().BeFalse();

        var stored = (await UserRepo.GetByIdAsync(bob.Id))!;
        stored.Role.Should().Be(UserRoles.User, "the stale in-flight login must not write the old role back");
        stored.KeySlotId.Should().BeNull();
        (await KeySlots.GetAllAsync()).Should().HaveCount(slotsBefore);
    }

    [Fact]
    public async Task RemoveSlot_ClearsTheOwningUsersDanglingReference()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);

        await KeyManagement.RemoveSlotAsync(carol.KeySlotId!.Value);

        (await UserRepo.GetByIdAsync(carol.Id))!.KeySlotId.Should().BeNull(
            "a dangling id makes her look provisioned and suppresses re-provisioning at next login");

        // And re-provisioning now works again.
        (await Users.ProvisionMissingKeySlotAsync((await UserRepo.GetByIdAsync(carol.Id))!, "CarolPass1"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Update_ReportsPasswordUnapplied_WhenTheRoleIsUnchanged()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);

        var applied = await Users.UpdateUserAsync(carol.Id, "Carol", UserRoles.Superadmin, "Ignored123");

        applied.Should().BeFalse("no promotion happened, so the password was ignored");
        (await Users.AuthenticateAsync("carol", "CarolPass1")).Should().NotBeNull();
        (await Users.AuthenticateAsync("carol", "Ignored123")).Should().BeNull();
    }

    [Fact]
    public async Task Demote_LastSuperadmin_StillRefused()
    {
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;

        var act = async () => await Users.UpdateUserAsync(admin.Id, "admin", UserRoles.User);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*last superadmin*");
    }

    [Fact]
    public async Task Promote_ReusesAnOrphanedSlotInsteadOfCreatingASecond()
    {
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var slotsBefore = (await KeySlots.GetAllAsync()).Count;

        // Simulate a demote that saved the role but never got to drop the slot.
        var stored = (await UserRepo.GetByIdAsync(carol.Id))!;
        stored.Role = UserRoles.User;
        await UserRepo.UpdateAsync(stored);

        await Users.UpdateUserAsync(carol.Id, "Carol", UserRoles.Superadmin);

        (await UserRepo.GetByIdAsync(carol.Id))!.KeySlotId.Should().Be(stored.KeySlotId);
        (await KeySlots.GetAllAsync()).Should().HaveCount(slotsBefore);
    }

    // ---- custom roles -----------------------------------------------------------------

    private async Task CreateCustomRoleAsync(string name = "user-developer")
        => await Roles.CreateAsync(new Role
        {
            Name = name, DisplayName = name, IsSystem = false,
            BasePolicy = RoleBasePolicy.Open, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

    [Fact]
    public async Task Create_AcceptsACustomRole()
    {
        await CreateCustomRoleAsync();

        var bob = await Users.CreateUserAsync("bob", "Bob", "BobPass1", "user-developer");

        bob.Role.Should().Be("user-developer");
        bob.KeySlotId.Should().BeNull("only superadmins hold a key slot");
    }

    [Fact]
    public async Task Create_StoresTheCanonicalRoleName_NotTheCallersCasing()
    {
        // tbl_role.name is COLLATE NOCASE so "Superadmin" resolves — but almost everything that
        // reads tbl_user.role compares it with an ordinal ==: CallerIdentity's X-User-Role check,
        // the key-slot branch here, FolderAccessService's superadmin bypass. Storing the caller's
        // casing produces a superadmin with no key slot whom the middleware treats as an ordinary
        // user.
        var bob = await Users.CreateUserAsync("bob", "Bob", "BobPass1", "SUPERADMIN");

        bob.Role.Should().Be(UserRoles.Superadmin);
        bob.KeySlotId.Should().NotBeNull("the key-slot branch keys off an ordinal role comparison");
    }

    [Fact]
    public async Task Update_TreatsADifferentlyCasedRoleAsNoChange()
    {
        var bob = await CreateRegularUserAsync();
        var stampBefore = await UserRepo.GetSecurityStampAsync(bob.Id);

        await Users.UpdateUserAsync(bob.Id, "Bob", "USER");

        (await UserRepo.GetByIdAsync(bob.Id))!.Role.Should().Be(UserRoles.User);
        (await UserRepo.GetSecurityStampAsync(bob.Id))
            .Should().Be(stampBefore, "a no-op role edit must not log everyone's sessions out");
    }

    [Fact]
    public async Task Create_RejectsARoleThatDoesNotExist()
    {
        // Roles are rows now, so the check is a lookup rather than a hard-coded pair — but an
        // unknown role must still never reach tbl_user, because it resolves fail-closed.
        var act = async () => await Users.CreateUserAsync("bob", "Bob", "BobPass1", "user-ghost");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Update_RejectsARoleThatDoesNotExist()
    {
        var bob = await CreateRegularUserAsync();

        var act = async () => await Users.UpdateUserAsync(bob.Id, "Bob", "user-ghost");

        await act.Should().ThrowAsync<ArgumentException>();
        (await UserRepo.GetByIdAsync(bob.Id))!.Role.Should().Be(UserRoles.User);
    }

    [Fact]
    public async Task DemotingToACustomRole_HitsTheSameLastSuperadminGuard()
    {
        // Before custom roles, "demote" and "change to the 'user' role" were the same thing, so
        // the guard tested for that exact target. Any move off superadmin is a demotion.
        await CreateCustomRoleAsync();
        var admin = (await UserRepo.GetByUsernameAsync("admin"))!;

        var act = async () => await Users.UpdateUserAsync(admin.Id, "admin", "user-developer");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*last superadmin*");
    }

    [Fact]
    public async Task DemotingToACustomRole_DropsTheKeySlot()
    {
        await CreateCustomRoleAsync();
        var carol = await Users.CreateUserAsync("carol", "Carol", "CarolPass1", UserRoles.Superadmin);
        var slotId = carol.KeySlotId!.Value;

        await Users.UpdateUserAsync(carol.Id, "Carol", "user-developer");

        var updated = (await UserRepo.GetByIdAsync(carol.Id))!;
        updated.Role.Should().Be("user-developer");
        updated.KeySlotId.Should().BeNull();
        (await KeySlots.GetAllAsync()).Should().NotContain(s => s.SlotId == slotId);
    }

    [Fact]
    public async Task ChangingRole_DropsTheCachedFolderRulesImmediately()
    {
        // The ACL cache is keyed per user and holds rules resolved through the PREVIOUS role.
        // Keeping the old (more permissive) answer for the cache TTL after a demotion is a
        // security bug, not a staleness annoyance.
        await CreateCustomRoleAsync("user-locked");
        var folderRepo = new FolderRepository(Factory, ScopeHolder);
        ScopeHolder.Scope = SystemCallerScope.Instance;
        var folderId = Guid.NewGuid();
        await folderRepo.CreateAsync(new Folder
        {
            Id = folderId, Path = "/HR", Name = "HR", ParentPath = "/",
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        await new RoleAclRepository(Factory).AddAsync(new RoleAclEntry
        {
            RoleName = "user-locked", FolderId = folderId,
            Effect = AclEffect.Deny, CreatedAt = DateTime.UtcNow
        });

        var bob = await CreateRegularUserAsync();
        var (deny, allow, _) = await FolderAccess.GetFullAccessInfoAsync(bob.Id);
        FolderAccessService.IsAccessDenied(deny, allow, "/HR").Should().BeFalse();

        await Users.UpdateUserAsync(bob.Id, "Bob", "user-locked");

        (deny, allow, _) = await FolderAccess.GetFullAccessInfoAsync(bob.Id);
        FolderAccessService.IsAccessDenied(deny, allow, "/HR").Should().BeTrue();
    }
}
