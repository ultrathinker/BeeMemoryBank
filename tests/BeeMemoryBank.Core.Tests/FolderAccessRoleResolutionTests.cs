using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The access engine's role half: a user's effective rules are the union of the rules on their
/// role and their own per-user rules, with the unchanged deny-wins matcher on top. These tests
/// go through the real repositories because the merge, the folder-id → path resolution and the
/// base-policy handling all live in <see cref="FolderAccessService.GetFullAccessInfoAsync"/>.
/// </summary>
public class FolderAccessRoleResolutionTests : TestFixture
{
    private FolderAccessService Access = null!;
    private RoleRepository Roles = null!;
    private RoleAclRepository RoleAcls = null!;
    private FolderAclRepository UserAcls = null!;
    private UserRepository UserRepo = null!;
    private FolderRepository Folders = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        Roles = new RoleRepository(Factory);
        RoleAcls = new RoleAclRepository(Factory);
        UserAcls = new FolderAclRepository(Factory);
        UserRepo = new UserRepository(Factory);
        Folders = new FolderRepository(Factory, ScopeHolder);
        ScopeHolder.Scope = SystemCallerScope.Instance;

        Access = new FolderAccessService(new ServiceCollection()
            .AddSingleton<IDbConnectionFactory>(_ => Factory)
            .AddScoped<IFolderAclRepository>(_ => UserAcls)
            .AddScoped<IRoleRepository>(_ => Roles)
            .AddScoped<IRoleAclRepository>(_ => RoleAcls)
            .AddScoped<IUserRepository>(_ => UserRepo)
            .AddScoped<IFolderRepository>(_ => Folders)
            .AddScoped<CallerScopeHolder>(_ => ScopeHolder)
            .BuildServiceProvider());

        // No cache clearing here on purpose. Keys are namespaced by database and each test gets
        // its own, so there is nothing to clear — and InvalidateAll() would empty the
        // process-wide dictionary for every OTHER test class running in parallel, which breaks
        // the tests below that deliberately depend on a warm cache.
    }

    private async Task<Guid> FolderAsync(string path)
    {
        var id = Guid.NewGuid();
        await Folders.CreateAsync(new Folder
        {
            Id = id,
            Path = path,
            Name = path.TrimEnd('/').Split('/').Last(),
            ParentPath = "/",
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        return id;
    }

    private async Task<int> UserAsync(string role, string username = "bob")
        => await UserRepo.CreateAsync(new User
        {
            Username = username, DisplayName = username, PasswordHash = "x",
            Role = role, CreatedAt = DateTime.UtcNow
        });

    private async Task RoleAsync(string name, string basePolicy)
        => await Roles.CreateAsync(new Role
        {
            Name = name, DisplayName = name, IsSystem = false,
            BasePolicy = basePolicy, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

    private Task AddRoleRuleAsync(string role, Guid folderId, AclEffect effect, bool readOnly = false)
        => RoleAcls.AddAsync(new RoleAclEntry
        {
            RoleName = role, FolderId = folderId, Effect = effect,
            IsReadOnly = readOnly, CreatedAt = DateTime.UtcNow
        });

    private Task AddUserRuleAsync(int userId, Guid folderId, AclEffect effect, bool readOnly = false)
        => UserAcls.AddAsync(new FolderAclEntry
        {
            UserId = userId, FolderId = folderId, Effect = effect,
            IsReadOnly = readOnly, CreatedAt = DateTime.UtcNow
        });

    private async Task<(HashSet<string> deny, HashSet<string> allow, HashSet<string> ro)> ResolveAsync(int userId)
    {
        Access.InvalidateCache(userId);
        return await Access.GetFullAccessInfoAsync(userId);
    }

    // ---- the headline fix -------------------------------------------------------------

    [Fact]
    public async Task DenyOnTheBuiltInUserRole_HidesTheFolderFromEveryRegularUser()
    {
        // This is the whole point of the feature: one rule on the 'user' role instead of one
        // rule per user, and no per-user edit to forget when the next account is created.
        var hr = await FolderAsync("/HR");
        await AddRoleRuleAsync(UserRoles.User, hr, AclEffect.Deny);
        var alice = await UserAsync(UserRoles.User, "alice");
        var bob = await UserAsync(UserRoles.User, "bob");

        foreach (var uid in new[] { alice, bob })
        {
            var (deny, allow, _) = await ResolveAsync(uid);
            FolderAccessService.IsAccessDenied(deny, allow, "/HR").Should().BeTrue();
            FolderAccessService.IsAccessDenied(deny, allow, "/HR/Salaries").Should().BeTrue();
            FolderAccessService.IsAccessDenied(deny, allow, "/Work").Should().BeFalse();
        }
    }

    [Fact]
    public async Task ARoleRuleAppliesToAUserCreatedAfterIt()
    {
        var hr = await FolderAsync("/HR");
        await AddRoleRuleAsync(UserRoles.User, hr, AclEffect.Deny);

        var newHire = await UserAsync(UserRoles.User, "newhire");

        var (deny, allow, _) = await ResolveAsync(newHire);
        FolderAccessService.IsAccessDenied(deny, allow, "/HR").Should().BeTrue();
    }

    // ---- union semantics --------------------------------------------------------------

    [Fact]
    public async Task RoleAndUserRules_AreUnioned()
    {
        var hr = await FolderAsync("/HR");
        var finance = await FolderAsync("/Finance");
        await AddRoleRuleAsync(UserRoles.User, hr, AclEffect.Deny);
        var bob = await UserAsync(UserRoles.User);
        await AddUserRuleAsync(bob, finance, AclEffect.Deny);

        var (deny, allow, _) = await ResolveAsync(bob);

        FolderAccessService.IsAccessDenied(deny, allow, "/HR").Should().BeTrue("role rule");
        FolderAccessService.IsAccessDenied(deny, allow, "/Finance").Should().BeTrue("per-user rule");
        FolderAccessService.IsAccessDenied(deny, allow, "/Work").Should().BeFalse();
    }

    [Fact]
    public async Task APerUserAllow_CannotReopenARoleDeny()
    {
        // Deny wins in the merged sets exactly as it does in the per-user-only sets. Without
        // this, an admin could accidentally punch a hole in an organisation-wide restriction by
        // granting one person access to the same folder.
        var hr = await FolderAsync("/HR");
        await AddRoleRuleAsync(UserRoles.User, hr, AclEffect.Deny);
        var bob = await UserAsync(UserRoles.User);
        await AddUserRuleAsync(bob, hr, AclEffect.Allow);

        var (deny, allow, _) = await ResolveAsync(bob);

        FolderAccessService.IsAccessDenied(deny, allow, "/HR").Should().BeTrue();
    }

    [Fact]
    public async Task ReadOnlyIsSticky_WhicheverSourceSetsIt()
    {
        var docs = await FolderAsync("/Docs");
        await AddRoleRuleAsync(UserRoles.User, docs, AclEffect.Allow, readOnly: true);
        var bob = await UserAsync(UserRoles.User);
        await AddUserRuleAsync(bob, docs, AclEffect.Allow, readOnly: false);

        var (deny, allow, ro) = await ResolveAsync(bob);

        FolderAccessService.IsAccessDenied(deny, allow, "/Docs").Should().BeFalse();
        FolderAccessService.IsWriteDenied(deny, allow, ro, "/Docs")
            .Should().BeTrue("the restrictive marking wins, like deny does");
    }

    [Fact]
    public async Task APerUserAllow_WidensARolesWhitelist()
    {
        var work = await FolderAsync("/Work");
        var scratch = await FolderAsync("/Scratch");
        await RoleAsync("user-developer", RoleBasePolicy.Closed);
        await AddRoleRuleAsync("user-developer", work, AclEffect.Allow);
        var bob = await UserAsync("user-developer");
        await AddUserRuleAsync(bob, scratch, AclEffect.Allow);

        var (deny, allow, _) = await ResolveAsync(bob);

        FolderAccessService.IsAccessDenied(deny, allow, "/Work").Should().BeFalse();
        FolderAccessService.IsAccessDenied(deny, allow, "/Scratch").Should().BeFalse();
        FolderAccessService.IsAccessDenied(deny, allow, "/HR").Should().BeTrue();
    }

    // ---- base policy ------------------------------------------------------------------

    [Fact]
    public async Task ClosedRoleWithNoRules_SeesNothing()
    {
        // A custom role assigned before anyone configured its rules must fail closed rather than
        // hand out the whole vault.
        await RoleAsync("user-tester", RoleBasePolicy.Closed);
        var bob = await UserAsync("user-tester");

        var (deny, allow, _) = await ResolveAsync(bob);

        FolderAccessService.IsAccessDenied(deny, allow, "/").Should().BeTrue();
        FolderAccessService.IsAccessDenied(deny, allow, "/Anything").Should().BeTrue();
    }

    [Fact]
    public async Task OpenRoleWithNoRules_SeesEverything()
    {
        await RoleAsync("user-support", RoleBasePolicy.Open);
        var bob = await UserAsync("user-support");

        var (deny, allow, _) = await ResolveAsync(bob);

        deny.Should().BeEmpty();
        allow.Should().BeEmpty();
        FolderAccessService.IsAccessDenied(deny, allow, "/Anything").Should().BeFalse();
    }

    [Fact]
    public async Task BuiltInUserRoleWithNoRules_StillSeesEverything()
    {
        // Back-compat: this is exactly what an untouched installation looks like after the
        // migration, and it must behave identically to before.
        var bob = await UserAsync(UserRoles.User);

        var (deny, allow, ro) = await ResolveAsync(bob);

        deny.Should().BeEmpty();
        allow.Should().BeEmpty();
        ro.Should().BeEmpty();
    }

    [Fact]
    public async Task ClosedRoleWithADenyButNoAllow_StillSeesNothing()
    {
        // 'closed' means "nothing until an allow row says otherwise"; adding a deny does not
        // count as configuring visibility.
        var hr = await FolderAsync("/HR");
        await RoleAsync("user-tester", RoleBasePolicy.Closed);
        await AddRoleRuleAsync("user-tester", hr, AclEffect.Deny);
        var bob = await UserAsync("user-tester");

        var (deny, allow, _) = await ResolveAsync(bob);

        FolderAccessService.IsAccessDenied(deny, allow, "/Work").Should().BeTrue();
    }

    [Fact]
    public async Task OpenRoleWithADeny_SeesEverythingElse()
    {
        var hr = await FolderAsync("/HR");
        await RoleAsync("user-support", RoleBasePolicy.Open);
        await AddRoleRuleAsync("user-support", hr, AclEffect.Deny);
        var bob = await UserAsync("user-support");

        var (deny, allow, _) = await ResolveAsync(bob);

        FolderAccessService.IsAccessDenied(deny, allow, "/HR").Should().BeTrue();
        FolderAccessService.IsAccessDenied(deny, allow, "/Work").Should().BeFalse();
    }

    // ---- fail-closed edges ------------------------------------------------------------

    [Fact]
    public async Task UnknownRole_ResolvesToDenyAll()
    {
        var bob = await UserAsync("user-ghost");

        var (deny, allow, _) = await ResolveAsync(bob);

        FolderAccessService.IsAccessDenied(deny, allow, "/Anything").Should().BeTrue();
    }

    [Fact]
    public async Task DeactivatedUser_ResolvesToDenyAll()
    {
        var bob = await UserAsync(UserRoles.User);
        await UserRepo.DeleteAsync(bob, "bob_del_xyz");

        var (deny, allow, _) = await ResolveAsync(bob);

        FolderAccessService.IsAccessDenied(deny, allow, "/Anything").Should().BeTrue();
    }

    [Fact]
    public async Task NullUserId_ResolvesToDenyAll()
    {
        // The endpoints that consume these sets directly (ArticleEndpoints, TreeEndpoints,
        // CopyEndpoints…) bypass CallerScopeMiddleware's own fail-closed default, so "no
        // identity" has to mean "no access" here too — empty sets would read as "unrestricted".
        var (deny, allow, _) = await Access.GetFullAccessInfoAsync(null);

        FolderAccessService.IsAccessDenied(deny, allow, "/Anything").Should().BeTrue();
    }

    [Fact]
    public async Task Superadmin_ResolvesToEmptySets_EvenWithStalePerUserRules()
    {
        var hr = await FolderAsync("/HR");
        var admin = await UserAsync(UserRoles.Superadmin, "root");
        await AddUserRuleAsync(admin, hr, AclEffect.Deny);

        var (deny, allow, ro) = await ResolveAsync(admin);

        deny.Should().BeEmpty("superadmins bypass folder rules everywhere else too");
        allow.Should().BeEmpty();
        ro.Should().BeEmpty();
    }

    [Fact]
    public async Task RulesOnTheSuperadminRole_AreInertForSuperadmins()
    {
        // The API refuses to create these, but if one ever exists it must not read as a working
        // restriction — that would be a UI that lies about what it enforces.
        var hr = await FolderAsync("/HR");
        await AddRoleRuleAsync(UserRoles.Superadmin, hr, AclEffect.Deny);
        var admin = await UserAsync(UserRoles.Superadmin, "root");

        var (deny, allow, _) = await ResolveAsync(admin);

        FolderAccessService.IsAccessDenied(deny, allow, "/HR").Should().BeFalse();
    }

    // ---- path resolution & cache ------------------------------------------------------

    [Fact]
    public async Task RoleRulesFollowAFolderRename()
    {
        // Rules store folder ids and resolve to paths on read, so a rename does not orphan them.
        var hr = await FolderAsync("/HR");
        await AddRoleRuleAsync(UserRoles.User, hr, AclEffect.Deny);
        var bob = await UserAsync(UserRoles.User);

        var folder = await Folders.GetByIdAsync(hr);
        folder!.Path = "/People";
        folder.Name = "People";
        await Folders.UpdateAsync(folder);

        var (deny, allow, _) = await ResolveAsync(bob);

        FolderAccessService.IsAccessDenied(deny, allow, "/People").Should().BeTrue();
        FolderAccessService.IsAccessDenied(deny, allow, "/HR").Should().BeFalse();
    }

    [Fact]
    public async Task InvalidateRole_ReachesEveryHolder()
    {
        var hr = await FolderAsync("/HR");
        var alice = await UserAsync(UserRoles.User, "alice");
        var bob = await UserAsync(UserRoles.User, "bob");
        // Warm both caches with the pre-change (unrestricted) answer.
        await Access.GetFullAccessInfoAsync(alice);
        await Access.GetFullAccessInfoAsync(bob);

        await AddRoleRuleAsync(UserRoles.User, hr, AclEffect.Deny);
        await Access.InvalidateRoleAsync(UserRoles.User);

        foreach (var uid in new[] { alice, bob })
        {
            var (deny, allow, _) = await Access.GetFullAccessInfoAsync(uid);
            FolderAccessService.IsAccessDenied(deny, allow, "/HR")
                .Should().BeTrue("editing a role must not wait out the 60s TTL");
        }
    }

    [Fact]
    public async Task InvalidateRole_LeavesOtherRolesAlone()
    {
        // Bob's cached answer has to be observably DIFFERENT from a fresh read, or the assertion
        // passes whether or not his entry survived: warm the cache, then add a rule behind the
        // cache's back so a reload would change the answer.
        await RoleAsync("user-tester", RoleBasePolicy.Open);
        var hr = await FolderAsync("/HR");
        var bob = await UserAsync(UserRoles.User, "bob");
        await Access.GetFullAccessInfoAsync(bob);
        await AddRoleRuleAsync(UserRoles.User, hr, AclEffect.Deny);

        await Access.InvalidateRoleAsync("user-tester");

        var (deny, allow, _) = await Access.GetFullAccessInfoAsync(bob);
        FolderAccessService.IsAccessDenied(deny, allow, "/HR")
            .Should().BeFalse("invalidating one role must not evict a user who holds a different one");
    }

    [Fact]
    public async Task InvalidateCacheForFolders_ReachesUsersViaTheirRole()
    {
        // Folder moves/renames invalidate by folder id. A user with no per-user rule at all is
        // still affected when their ROLE has a rule on that folder — the old code only walked
        // tbl_folder_acl_entry and would have left them with a stale path.
        var hr = await FolderAsync("/HR");
        await AddRoleRuleAsync(UserRoles.User, hr, AclEffect.Deny);
        var bob = await UserAsync(UserRoles.User);
        await Access.GetFullAccessInfoAsync(bob);

        var folder = await Folders.GetByIdAsync(hr);
        folder!.Path = "/People";
        folder.Name = "People";
        await Folders.UpdateAsync(folder);
        await Access.InvalidateCacheForFoldersAsync([hr]);

        var (deny, allow, _) = await Access.GetFullAccessInfoAsync(bob);
        FolderAccessService.IsAccessDenied(deny, allow, "/People").Should().BeTrue();
    }

    [Fact]
    public async Task ResolvedRules_AreCachedUntilInvalidated()
    {
        var hr = await FolderAsync("/HR");
        var bob = await UserAsync(UserRoles.User);
        await Access.GetFullAccessInfoAsync(bob);

        await AddRoleRuleAsync(UserRoles.User, hr, AclEffect.Deny);

        var (deny, allow, _) = await Access.GetFullAccessInfoAsync(bob);
        FolderAccessService.IsAccessDenied(deny, allow, "/HR")
            .Should().BeFalse("still the cached answer — this is what makes invalidation load-bearing");
    }
}
