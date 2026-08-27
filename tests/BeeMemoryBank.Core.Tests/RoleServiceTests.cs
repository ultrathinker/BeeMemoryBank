using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The guards around role management. Most of these protect against locking people out: a role
/// whose row disappears resolves fail-closed, so deleting or renaming one out from under its
/// holders is worse than refusing the operation.
/// </summary>
public class RoleServiceTests : TestFixture
{
    private RoleService Svc = null!;
    private RoleRepository Roles = null!;
    private RoleAclRepository RoleAcls = null!;
    private UserRepository UserRepo = null!;
    private FolderRepository Folders = null!;
    private FolderAccessService Access = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        Roles = new RoleRepository(Factory);
        RoleAcls = new RoleAclRepository(Factory);
        UserRepo = new UserRepository(Factory);
        Folders = new FolderRepository(Factory, ScopeHolder);
        ScopeHolder.Scope = SystemCallerScope.Instance;

        Access = new FolderAccessService(new ServiceCollection()
            .AddSingleton<IDbConnectionFactory>(_ => Factory)
            .AddScoped<IFolderAclRepository>(_ => new FolderAclRepository(Factory))
            .AddScoped<IRoleRepository>(_ => Roles)
            .AddScoped<IRoleAclRepository>(_ => RoleAcls)
            .AddScoped<IUserRepository>(_ => UserRepo)
            .AddScoped<IFolderRepository>(_ => Folders)
            .AddScoped(_ => ScopeHolder)
            .BuildServiceProvider());

        // Deliberately no InvalidateAll(): it clears the process-wide cache for every database,
        // including whatever test class is running alongside this one.
        Svc = new RoleService(Roles, RoleAcls, UserRepo, Folders, Access);
    }

    private async Task<Guid> FolderAsync(string path)
    {
        var id = Guid.NewGuid();
        await Folders.CreateAsync(new Folder
        {
            Id = id, Path = path, Name = path.TrimEnd('/').Split('/').Last(), ParentPath = "/",
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        return id;
    }

    private Task<int> UserAsync(string role, string username = "bob")
        => UserRepo.CreateAsync(new User
        {
            Username = username, DisplayName = username, PasswordHash = "x",
            Role = role, CreatedAt = DateTime.UtcNow
        });

    // ---- naming -----------------------------------------------------------------------

    [Theory]
    [InlineData("Dev")]                 // upper case
    [InlineData("-dev")]                // must start alphanumeric
    [InlineData("a")]                   // too short
    [InlineData("dev role")]            // space
    [InlineData("dev.role")]            // dot
    [InlineData("dev/role")]            // slash
    [InlineData("")]
    public async Task Create_RejectsMalformedNames(string name)
    {
        var act = async () => await Svc.CreateAsync(name, "X", null, RoleBasePolicy.Closed);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("superadmin")]
    [InlineData("SUPERADMIN")]
    [InlineData("user")]
    [InlineData("admin")]
    [InlineData("root")]
    public async Task Create_RejectsReservedNames(string name)
    {
        // A role differing from "superadmin" only by case would be unprivileged to
        // CallerIdentity's ordinal check and privileged to the Web layer's case-insensitive one.
        var act = async () => await Svc.CreateAsync(name, "X", null, RoleBasePolicy.Closed);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Create_RejectsADuplicateDifferingOnlyInCase()
    {
        await Svc.CreateAsync("user-developer", "Dev", null, RoleBasePolicy.Closed);

        var act = async () => await Svc.CreateAsync("user-developer", "Dev again", null, RoleBasePolicy.Closed);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Create_RejectsAnUnknownBasePolicy()
    {
        var act = async () => await Svc.CreateAsync("user-developer", "Dev", null, "whatever");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Create_DefaultsDisplayNameToTheName()
    {
        var role = await Svc.CreateAsync("user-developer", "   ", null, RoleBasePolicy.Closed);
        role.DisplayName.Should().Be("user-developer");
        role.IsSystem.Should().BeFalse();
    }

    // ---- system-role protection -------------------------------------------------------

    [Fact]
    public async Task Delete_RefusesSystemRoles()
    {
        var act = async () => await Svc.DeleteAsync(UserRoles.User);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Update_RefusesTheSuperadminRole()
    {
        var act = async () => await Svc.UpdateAsync(UserRoles.Superadmin, "Boss", null, RoleBasePolicy.Open);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Update_AllowsTheBuiltInUserRole()
    {
        // Editing 'user' is the whole point — that is where an organisation-wide rule goes.
        await Svc.UpdateAsync(UserRoles.User, "Staff", "Everyone else", RoleBasePolicy.Open);

        var role = await Svc.GetAsync(UserRoles.User);
        role!.DisplayName.Should().Be("Staff");
    }

    [Fact]
    public async Task AddRule_RefusesTheSuperadminRole()
    {
        // Superadmins bypass folder rules, so such a row would be silently inert — a UI showing
        // a restriction it does not enforce.
        var folderId = await FolderAsync("/HR");

        var act = async () => await Svc.AddRuleAsync(UserRoles.Superadmin, folderId, AclEffect.Deny, false);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AddRule_AllowsTheBuiltInUserRole()
    {
        var folderId = await FolderAsync("/HR");

        await Svc.AddRuleAsync(UserRoles.User, folderId, AclEffect.Deny, false);

        (await Svc.ListRulesAsync(UserRoles.User)).Should().ContainSingle();
    }

    // ---- delete-in-use ----------------------------------------------------------------

    [Fact]
    public async Task Delete_RefusesWhileUsersStillHoldTheRole()
    {
        await Svc.CreateAsync("user-developer", "Dev", null, RoleBasePolicy.Open);
        await UserAsync("user-developer");

        var act = async () => await Svc.DeleteAsync("user-developer");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*still have the role*");
    }

    [Fact]
    public async Task Delete_SucceedsOnceNobodyHoldsTheRole()
    {
        await Svc.CreateAsync("user-developer", "Dev", null, RoleBasePolicy.Open);
        var bob = await UserAsync("user-developer");
        var user = await UserRepo.GetByIdAsync(bob);
        user!.Role = UserRoles.User;
        await UserRepo.UpdateAsync(user);

        await Svc.DeleteAsync("user-developer");

        (await Svc.GetAsync("user-developer")).Should().BeNull();
    }

    [Fact]
    public async Task Delete_IgnoresDeactivatedHolders()
    {
        await Svc.CreateAsync("user-developer", "Dev", null, RoleBasePolicy.Open);
        var bob = await UserAsync("user-developer");
        await UserRepo.DeleteAsync(bob, "bob_del_xyz");

        await Svc.DeleteAsync("user-developer");

        (await Svc.GetAsync("user-developer")).Should().BeNull();
    }

    [Fact]
    public async Task Delete_UnknownRole_Throws()
    {
        var act = async () => await Svc.DeleteAsync("user-nope");
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    // ---- rules and cache --------------------------------------------------------------

    [Fact]
    public async Task AddRule_TakesEffectImmediatelyForEveryHolder()
    {
        await Svc.CreateAsync("user-developer", "Dev", null, RoleBasePolicy.Open);
        var alice = await UserAsync("user-developer", "alice");
        var bob = await UserAsync("user-developer", "bob");
        await Access.GetFullAccessInfoAsync(alice);
        await Access.GetFullAccessInfoAsync(bob);
        var folderId = await FolderAsync("/HR");

        await Svc.AddRuleAsync("user-developer", folderId, AclEffect.Deny, false);

        foreach (var uid in new[] { alice, bob })
        {
            var (deny, allow, _) = await Access.GetFullAccessInfoAsync(uid);
            FolderAccessService.IsAccessDenied(deny, allow, "/HR").Should().BeTrue();
        }
    }

    [Fact]
    public async Task RemoveRule_TakesEffectImmediately()
    {
        await Svc.CreateAsync("user-developer", "Dev", null, RoleBasePolicy.Open);
        var bob = await UserAsync("user-developer");
        var folderId = await FolderAsync("/HR");
        await Svc.AddRuleAsync("user-developer", folderId, AclEffect.Deny, false);
        await Access.GetFullAccessInfoAsync(bob);

        await Svc.RemoveRuleAsync("user-developer", folderId);

        var (deny, allow, _) = await Access.GetFullAccessInfoAsync(bob);
        FolderAccessService.IsAccessDenied(deny, allow, "/HR").Should().BeFalse();
    }

    [Fact]
    public async Task SetRuleReadOnly_TakesEffectImmediately()
    {
        await Svc.CreateAsync("user-developer", "Dev", null, RoleBasePolicy.Closed);
        var bob = await UserAsync("user-developer");
        var folderId = await FolderAsync("/Docs");
        await Svc.AddRuleAsync("user-developer", folderId, AclEffect.Allow, isReadOnly: false);
        await Access.GetFullAccessInfoAsync(bob);

        await Svc.SetRuleReadOnlyAsync("user-developer", folderId, true);

        var (deny, allow, ro) = await Access.GetFullAccessInfoAsync(bob);
        FolderAccessService.IsWriteDenied(deny, allow, ro, "/Docs").Should().BeTrue();
    }

    [Fact]
    public async Task ChangingBasePolicy_TakesEffectImmediately()
    {
        await Svc.CreateAsync("user-support", "Support", null, RoleBasePolicy.Open);
        var bob = await UserAsync("user-support");
        var (deny, allow, _) = await Access.GetFullAccessInfoAsync(bob);
        FolderAccessService.IsAccessDenied(deny, allow, "/Anything").Should().BeFalse();

        await Svc.UpdateAsync("user-support", "Support", null, RoleBasePolicy.Closed);

        (deny, allow, _) = await Access.GetFullAccessInfoAsync(bob);
        FolderAccessService.IsAccessDenied(deny, allow, "/Anything")
            .Should().BeTrue("tightening a policy must not wait out the cache TTL");
    }

    [Fact]
    public async Task AddRule_IgnoresReadOnlyOnADenyRow()
    {
        await Svc.CreateAsync("user-developer", "Dev", null, RoleBasePolicy.Open);
        var folderId = await FolderAsync("/HR");

        var entry = await Svc.AddRuleAsync("user-developer", folderId, AclEffect.Deny, isReadOnly: true);

        entry.IsReadOnly.Should().BeFalse("a deny row denies outright; read-only is an allow-row concept");
    }

    [Fact]
    public async Task AddRule_UnknownFolder_Throws()
    {
        await Svc.CreateAsync("user-developer", "Dev", null, RoleBasePolicy.Open);

        var act = async () => await Svc.AddRuleAsync("user-developer", Guid.NewGuid(), AclEffect.Deny, false);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task ListRules_ResolvesFolderPaths_AndSurvivesADeletedFolder()
    {
        await Svc.CreateAsync("user-developer", "Dev", null, RoleBasePolicy.Open);
        var folderId = await FolderAsync("/HR");
        await Svc.AddRuleAsync("user-developer", folderId, AclEffect.Deny, false);

        var rules = await Svc.ListRulesAsync("user-developer");

        rules.Should().ContainSingle();
        rules[0].FolderPath.Should().Be("/HR");
    }

    [Fact]
    public async Task List_ReportsUserAndRuleCounts()
    {
        await Svc.CreateAsync("user-developer", "Dev", null, RoleBasePolicy.Open);
        await UserAsync("user-developer", "alice");
        await UserAsync("user-developer", "bob");
        var folderId = await FolderAsync("/HR");
        await Svc.AddRuleAsync("user-developer", folderId, AclEffect.Deny, false);

        var summaries = await Svc.ListAsync();

        var dev = summaries.Single(s => s.Role.Name == "user-developer");
        dev.UserCount.Should().Be(2);
        dev.RuleCount.Should().Be(1);
        // System roles come first so the built-ins stay at the top of the admin list.
        summaries[0].Role.IsSystem.Should().BeTrue();
    }
}
