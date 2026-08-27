using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Storage-level coverage for custom roles: that migration 009 lands with the two system roles
/// seeded, and that the role/role-ACL repositories round-trip. Business guards (reserved names,
/// delete-in-use) live in RoleService and are covered separately.
/// </summary>
public class RoleRepositoryTests : TestFixture
{
    private RoleRepository Roles = null!;
    private RoleAclRepository RoleAcls = null!;
    private UserRepository UserRepo = null!;
    private FolderRepository Folders = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Roles = new RoleRepository(Factory);
        RoleAcls = new RoleAclRepository(Factory);
        UserRepo = new UserRepository(Factory);
        Folders = new FolderRepository(Factory, ScopeHolder);
        ScopeHolder.Scope = Core.Services.SystemCallerScope.Instance;
    }

    private async Task<Guid> CreateFolderAsync(string path)
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

    private static Role NewRole(string name, string basePolicy = RoleBasePolicy.Closed) => new()
    {
        Name = name,
        DisplayName = name,
        IsSystem = false,
        BasePolicy = basePolicy,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task Migration_SeedsBothSystemRoles()
    {
        var all = await Roles.ListAsync();

        all.Should().HaveCount(2);
        all.Should().OnlyContain(r => r.IsSystem);
        all.Select(r => r.Name).Should().BeEquivalentTo([UserRoles.Superadmin, UserRoles.User]);
        // Both built-ins keep the historical "no allow rows means the whole vault" behaviour.
        all.Should().OnlyContain(r => r.BasePolicy == RoleBasePolicy.Open);
    }

    [Fact]
    public async Task GetByName_IsCaseInsensitive()
    {
        // tbl_role.name is COLLATE NOCASE, which is what makes a "SuperAdmin" role impossible
        // to create — the case-collision escalation vector between the ordinal X-User-Role check
        // and the Web layer's case-insensitive role matching.
        (await Roles.GetByNameAsync("SUPERADMIN")).Should().NotBeNull();
        (await Roles.GetByNameAsync("User")).Should().NotBeNull();
    }

    [Fact]
    public async Task CreatingARoleThatDiffersOnlyInCase_IsRejectedByThePrimaryKey()
    {
        var act = async () => await Roles.CreateAsync(NewRole("SuperAdmin"));

        await act.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
    }

    [Fact]
    public async Task Create_ThenGet_RoundTrips()
    {
        var role = NewRole("user-developer");
        role.DisplayName = "Developer";
        role.Description = "Engineering staff";
        await Roles.CreateAsync(role);

        var loaded = await Roles.GetByNameAsync("user-developer");

        loaded.Should().NotBeNull();
        loaded!.DisplayName.Should().Be("Developer");
        loaded.Description.Should().Be("Engineering staff");
        loaded.IsSystem.Should().BeFalse();
        loaded.BasePolicy.Should().Be(RoleBasePolicy.Closed);
    }

    [Fact]
    public async Task Update_ChangesMetadataAndBasePolicy()
    {
        await Roles.CreateAsync(NewRole("user-tester"));

        await Roles.UpdateAsync("user-tester", "QA", "Testers", RoleBasePolicy.Open);

        var loaded = await Roles.GetByNameAsync("user-tester");
        loaded!.DisplayName.Should().Be("QA");
        loaded.Description.Should().Be("Testers");
        loaded.BasePolicy.Should().Be(RoleBasePolicy.Open);
    }

    [Fact]
    public async Task Delete_RefusesSystemRoles_EvenAtTheRepositoryLevel()
    {
        // Defence in depth: RoleService guards this too, but the repository must not be a way
        // around it — deleting 'user' would strand every regular account on a missing role,
        // which resolves fail-closed.
        (await Roles.DeleteAsync(UserRoles.User)).Should().BeFalse();
        (await Roles.GetByNameAsync(UserRoles.User)).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_CascadesTheRolesAclRows()
    {
        await Roles.CreateAsync(NewRole("user-support"));
        var folderId = await CreateFolderAsync("/Support");
        await RoleAcls.AddAsync(new RoleAclEntry
        {
            RoleName = "user-support",
            FolderId = folderId,
            Effect = AclEffect.Allow,
            CreatedAt = DateTime.UtcNow
        });

        (await Roles.DeleteAsync("user-support")).Should().BeTrue();

        (await RoleAcls.GetByRoleNameAsync("user-support")).Should().BeEmpty();
    }

    [Fact]
    public async Task RoleAcl_AllowAndDenyOnTheSameFolder_Coexist()
    {
        await Roles.CreateAsync(NewRole("user-developer"));
        var folderId = await CreateFolderAsync("/Work");

        await RoleAcls.AddAsync(new RoleAclEntry
        {
            RoleName = "user-developer", FolderId = folderId,
            Effect = AclEffect.Allow, IsReadOnly = true, CreatedAt = DateTime.UtcNow
        });
        await RoleAcls.AddAsync(new RoleAclEntry
        {
            RoleName = "user-developer", FolderId = folderId,
            Effect = AclEffect.Deny, CreatedAt = DateTime.UtcNow
        });

        var entries = await RoleAcls.GetByRoleNameAsync("user-developer");

        entries.Should().HaveCount(2, "the primary key is (role, folder, effect), as on the user table");
        entries.Single(e => e.Effect == AclEffect.Allow).IsReadOnly.Should().BeTrue();
        entries.Single(e => e.Effect == AclEffect.Deny).IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public async Task RoleAcl_SetReadOnly_TargetsOnlyTheNamedEffect()
    {
        await Roles.CreateAsync(NewRole("user-developer"));
        var folderId = await CreateFolderAsync("/Work");
        await RoleAcls.AddAsync(new RoleAclEntry
        {
            RoleName = "user-developer", FolderId = folderId,
            Effect = AclEffect.Allow, CreatedAt = DateTime.UtcNow
        });

        await RoleAcls.SetReadOnlyAsync("user-developer", folderId, AclEffect.Allow, true);

        var entry = (await RoleAcls.GetByRoleNameAsync("user-developer")).Single();
        entry.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public async Task RoleAcl_RemoveByRoleAndFolder_DropsBothEffects()
    {
        await Roles.CreateAsync(NewRole("user-developer"));
        var folderId = await CreateFolderAsync("/Work");
        await RoleAcls.AddAsync(new RoleAclEntry { RoleName = "user-developer", FolderId = folderId, Effect = AclEffect.Allow, CreatedAt = DateTime.UtcNow });
        await RoleAcls.AddAsync(new RoleAclEntry { RoleName = "user-developer", FolderId = folderId, Effect = AclEffect.Deny, CreatedAt = DateTime.UtcNow });

        await RoleAcls.RemoveByRoleAndFolderAsync("user-developer", folderId);

        (await RoleAcls.GetByRoleNameAsync("user-developer")).Should().BeEmpty();
    }

    [Fact]
    public async Task RoleAcl_GetRoleNamesByFolderId_FeedsCacheInvalidation()
    {
        await Roles.CreateAsync(NewRole("user-developer"));
        await Roles.CreateAsync(NewRole("user-tester"));
        var shared = await CreateFolderAsync("/Shared");
        var other = await CreateFolderAsync("/Other");
        await RoleAcls.AddAsync(new RoleAclEntry { RoleName = "user-developer", FolderId = shared, Effect = AclEffect.Deny, CreatedAt = DateTime.UtcNow });
        await RoleAcls.AddAsync(new RoleAclEntry { RoleName = "user-tester", FolderId = shared, Effect = AclEffect.Deny, CreatedAt = DateTime.UtcNow });
        await RoleAcls.AddAsync(new RoleAclEntry { RoleName = "user-tester", FolderId = other, Effect = AclEffect.Deny, CreatedAt = DateTime.UtcNow });

        var names = await RoleAcls.GetRoleNamesByFolderIdAsync(shared);

        names.Should().BeEquivalentTo(["user-developer", "user-tester"]);
    }

    [Fact]
    public async Task RoleAcl_CountEntriesPerRole_GroupsCasingsTogether_AndOmitsEmptyRoles()
    {
        // Rows written with different casings for the same role must collapse into ONE group.
        // Case-sensitive grouping would emit a row per casing, and building a dictionary with an
        // OrdinalIgnoreCase comparer over that throws on the duplicate key rather than miscounting.
        await Roles.CreateAsync(NewRole("user-developer"));
        await Roles.CreateAsync(NewRole("user-tester"));
        var work = await CreateFolderAsync("/Work");
        var docs = await CreateFolderAsync("/Docs");
        await RoleAcls.AddAsync(new RoleAclEntry { RoleName = "user-developer", FolderId = work, Effect = AclEffect.Deny, CreatedAt = DateTime.UtcNow });
        await RoleAcls.AddAsync(new RoleAclEntry { RoleName = "User-Developer", FolderId = docs, Effect = AclEffect.Deny, CreatedAt = DateTime.UtcNow });

        var counts = await RoleAcls.CountEntriesPerRoleAsync();

        counts["USER-DEVELOPER"].Should().Be(2);
        counts.Should().NotContainKey("user-tester");
    }

    [Fact]
    public async Task RoleAcl_DuplicateRuleDifferingOnlyInCase_IsRejectedByThePrimaryKey()
    {
        // role_name is COLLATE NOCASE, so (role, folder, effect) really is unique per role —
        // a BINARY column would let 'user' and 'User' hold two conflicting rows for one folder.
        await Roles.CreateAsync(NewRole("user-developer"));
        var folderId = await CreateFolderAsync("/Work");
        await RoleAcls.AddAsync(new RoleAclEntry { RoleName = "user-developer", FolderId = folderId, Effect = AclEffect.Deny, CreatedAt = DateTime.UtcNow });

        var act = async () => await RoleAcls.AddAsync(new RoleAclEntry
        {
            RoleName = "USER-DEVELOPER", FolderId = folderId, Effect = AclEffect.Deny, CreatedAt = DateTime.UtcNow
        });

        await act.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
    }

    [Fact]
    public async Task RoleAcl_ReadsAndWritesMatchCaseInsensitively()
    {
        // The role name reaching these methods comes off a URL segment, so its casing is the
        // caller's choice. A case-sensitive match would make a delete or a read-only toggle
        // silently affect nothing — a restriction that looks removed but is still enforced, or
        // one that looks read-only but still permits writes.
        await Roles.CreateAsync(NewRole("user-developer"));
        var folderId = await CreateFolderAsync("/Work");
        await RoleAcls.AddAsync(new RoleAclEntry { RoleName = "user-developer", FolderId = folderId, Effect = AclEffect.Allow, CreatedAt = DateTime.UtcNow });

        (await RoleAcls.GetByRoleNameAsync("USER-Developer")).Should().HaveCount(1);

        await RoleAcls.SetReadOnlyAsync("USER-Developer", folderId, AclEffect.Allow, true);
        (await RoleAcls.GetByRoleNameAsync("user-developer")).Single().IsReadOnly.Should().BeTrue();

        (await RoleAcls.GetRoleNamesByFolderIdAsync(folderId)).Should().HaveCount(1);

        await RoleAcls.RemoveByRoleAndFolderAsync("USER-DEVELOPER", folderId);
        (await RoleAcls.GetByRoleNameAsync("user-developer")).Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserIdsByRole_ReturnsOnlyActiveHolders_CaseInsensitively()
    {
        var alice = await UserRepo.CreateAsync(new User { Username = "alice", DisplayName = "Alice", PasswordHash = "x", Role = "user-developer", CreatedAt = DateTime.UtcNow });
        await UserRepo.CreateAsync(new User { Username = "bob", DisplayName = "Bob", PasswordHash = "x", Role = UserRoles.User, CreatedAt = DateTime.UtcNow });
        var carol = await UserRepo.CreateAsync(new User { Username = "carol", DisplayName = "Carol", PasswordHash = "x", Role = "user-developer", CreatedAt = DateTime.UtcNow });
        await UserRepo.DeleteAsync(carol, "carol_del_xyz");

        var ids = await UserRepo.GetUserIdsByRoleAsync("USER-DEVELOPER");

        ids.Should().BeEquivalentTo([alice]);
    }

    [Fact]
    public async Task CountActiveUsersPerRole_GroupsByRole()
    {
        await UserRepo.CreateAsync(new User { Username = "alice", DisplayName = "Alice", PasswordHash = "x", Role = "user-developer", CreatedAt = DateTime.UtcNow });
        await UserRepo.CreateAsync(new User { Username = "bob", DisplayName = "Bob", PasswordHash = "x", Role = "user-developer", CreatedAt = DateTime.UtcNow });

        var counts = await UserRepo.CountActiveUsersPerRoleAsync();

        counts["user-developer"].Should().Be(2);
        // The fixture's InitializationService never ran here, so 'user'/'superadmin' may be absent.
        counts.Should().NotContainKey("user-tester");
    }
}
