using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// Every guard around roles lives here, so there is one auditable place to read them:
/// what a role may be named, which roles may be edited or deleted, which roles may carry folder
/// rules, and when the folder-ACL cache has to be invalidated.
/// <para>
/// Throws <see cref="ArgumentException"/> for bad input (→ 400) and
/// <see cref="InvalidOperationException"/> for a conflict with current state (→ 409), matching
/// how the user endpoints already translate exceptions.
/// </para>
/// </summary>
public class RoleService(
    IRoleRepository roleRepo,
    IRoleAclRepository roleAclRepo,
    IUserRepository userRepo,
    IFolderRepository folderRepo,
    FolderAccessService folderAccess)
{
    /// <summary>
    /// Lower-case, starts alphanumeric, 2–32 chars. Restricting the alphabet is a security
    /// control: the forwarded <c>X-User-Role</c> header is compared to "superadmin" with an
    /// ordinal <c>==</c> in <c>CallerIdentity</c> while the Web layer's role matching is
    /// case-insensitive, so any name that could differ only by case from a privileged one is a
    /// privilege-escalation vector. Lower-case-only plus the NOCASE primary key closes it.
    /// </summary>
    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9_-]{1,31}$", RegexOptions.Compiled);

    /// <summary>
    /// Names refused on top of the ones already taken by the seeded system roles. None of these
    /// grant anything today — they are reserved so that a future privileged role name cannot
    /// collide with an existing custom role somebody already assigned to twenty people.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "superadmin", "user", "admin", "administrator", "root", "system", "sysadmin",
        "superuser", "owner", "none", "anonymous"
    };

    public record RoleSummary(Role Role, int UserCount, int RuleCount);

    public async Task<List<RoleSummary>> ListAsync()
    {
        var roles = await roleRepo.ListAsync();
        var userCounts = await userRepo.CountActiveUsersPerRoleAsync();
        var ruleCounts = await roleAclRepo.CountEntriesPerRoleAsync();

        return roles
            .Select(r => new RoleSummary(
                r,
                userCounts.GetValueOrDefault(r.Name),
                ruleCounts.GetValueOrDefault(r.Name)))
            .ToList();
    }

    public Task<Role?> GetAsync(string name) => roleRepo.GetByNameAsync(name);

    public async Task<Role> CreateAsync(string name, string displayName, string? description, string basePolicy)
    {
        // Lower-cased rather than rejected. The alphabet restriction exists so that no role can
        // differ from a privileged one only by case (see NamePattern) — storing it lower-cased
        // delivers exactly that, while refusing "OneFolder" outright only turns an ordinary typo
        // into a dead end. Characters outside the alphabet are still refused, with a message.
        name = (name ?? "").Trim().ToLowerInvariant();

        if (!NamePattern.IsMatch(name))
            throw new ArgumentException(
                "Role name must be 2–32 characters and contain only letters, digits, '-' and '_', " +
                "starting with a letter or digit. Capital letters are converted to lower case.");

        if (ReservedNames.Contains(name))
            throw new ArgumentException($"'{name}' is a reserved role name.");

        if (!RoleBasePolicy.IsValid(basePolicy))
            throw new ArgumentException($"Base policy must be '{RoleBasePolicy.Open}' or '{RoleBasePolicy.Closed}'.");

        if (string.IsNullOrWhiteSpace(displayName))
            displayName = name;

        // Case-insensitive, because tbl_role.name is COLLATE NOCASE — the insert would fail on
        // the primary key anyway, but a clear 409 beats an opaque SQLite error.
        if (await roleRepo.GetByNameAsync(name) is not null)
            throw new InvalidOperationException($"Role '{name}' already exists.");

        var now = DateTime.UtcNow;
        var role = new Role
        {
            Name = name,
            DisplayName = displayName.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            IsSystem = false,
            BasePolicy = basePolicy,
            CreatedAt = now,
            UpdatedAt = now
        };
        await roleRepo.CreateAsync(role);
        return role;
    }

    public async Task UpdateAsync(string name, string displayName, string? description, string basePolicy)
    {
        var role = await RequireRoleAsync(name);
        RequireNotSuperadminRole(role, "edited");

        if (!RoleBasePolicy.IsValid(basePolicy))
            throw new ArgumentException($"Base policy must be '{RoleBasePolicy.Open}' or '{RoleBasePolicy.Closed}'.");

        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name is required.");

        var policyChanged = role.BasePolicy != basePolicy;

        await roleRepo.UpdateAsync(
            role.Name,
            displayName.Trim(),
            string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            basePolicy);

        // base_policy decides what an empty allow list means, so flipping it changes who can see
        // what — immediately, not in up to 60 seconds.
        if (policyChanged)
            await folderAccess.InvalidateRoleAsync(role.Name);
    }

    public async Task DeleteAsync(string name)
    {
        var role = await RequireRoleAsync(name);

        if (role.IsSystem)
            throw new InvalidOperationException($"'{role.Name}' is a built-in role and cannot be deleted.");

        // A user whose role row is gone resolves fail-closed (no folder access at all), so
        // deleting a role out from under its holders would lock them out rather than demote
        // them. Make the caller reassign first, explicitly.
        var holders = await userRepo.GetUserIdsByRoleAsync(role.Name);
        if (holders.Count > 0)
            throw new InvalidOperationException(
                $"{holders.Count} user(s) still have the role '{role.Name}'. " +
                "Reassign them to another role first.");

        await roleRepo.DeleteAsync(role.Name);
        // Normally a no-op: the holder check above already found nobody, and reassigning a user
        // invalidates their entry in UserService. It covers the one gap that check cannot — a
        // user assigned to this role BETWEEN the check and the delete, who would otherwise keep
        // the now-deleted role's cached rules until the TTL expired.
        await folderAccess.InvalidateRoleAsync(role.Name);
    }

    // ---- folder rules on a role -------------------------------------------------------

    public async Task<List<(RoleAclEntry Entry, string FolderPath)>> ListRulesAsync(string name)
    {
        var role = await RequireRoleAsync(name);
        var entries = await roleAclRepo.GetByRoleNameAsync(role.Name);

        var result = new List<(RoleAclEntry, string)>();
        foreach (var entry in entries)
        {
            var folder = await folderRepo.GetByIdAsync(entry.FolderId, includeDeleted: true);
            result.Add((entry, folder?.Path ?? "(deleted)"));
        }
        return result;
    }

    public async Task<RoleAclEntry> AddRuleAsync(string name, Guid folderId, AclEffect effect, bool isReadOnly)
    {
        var role = await RequireRoleAsync(name);
        RequireRoleCanCarryRules(role);

        var folder = await folderRepo.GetByIdAsync(folderId)
            ?? throw new KeyNotFoundException("Folder not found");

        var entry = new RoleAclEntry
        {
            RoleName = role.Name,
            FolderId = folder.Id,
            Effect = effect,
            // is_read_only is only meaningful on an allow row; a deny row denies outright.
            IsReadOnly = effect == AclEffect.Allow && isReadOnly,
            CreatedAt = DateTime.UtcNow
        };

        // (role, folder, effect) is the primary key, so the insert would fail anyway — but a
        // named 409 beats an opaque constraint error. Core has no SQL-provider reference, so the
        // narrow race between this check and the insert is translated at the endpoint instead.
        var existing = await roleAclRepo.GetByRoleNameAsync(role.Name);
        if (existing.Any(e => e.FolderId == folder.Id && e.Effect == effect))
            throw new InvalidOperationException(
                $"Role '{role.Name}' already has a {effect.ToString().ToLowerInvariant()} rule on that folder.");

        await roleAclRepo.AddAsync(entry);
        await folderAccess.InvalidateRoleAsync(role.Name);
        return entry;
    }

    public async Task SetRuleReadOnlyAsync(string name, Guid folderId, bool isReadOnly)
    {
        var role = await RequireRoleAsync(name);
        RequireRoleCanCarryRules(role);

        await roleAclRepo.SetReadOnlyAsync(role.Name, folderId, AclEffect.Allow, isReadOnly);
        await folderAccess.InvalidateRoleAsync(role.Name);
    }

    public async Task RemoveRuleAsync(string name, Guid folderId)
    {
        var role = await RequireRoleAsync(name);

        // Deliberately NOT gated by RequireRoleCanCarryRules: removing a rule can only widen
        // access, and a row that somehow exists on the superadmin role must stay removable.
        await roleAclRepo.RemoveByRoleAndFolderAsync(role.Name, folderId);
        await folderAccess.InvalidateRoleAsync(role.Name);
    }

    // ---- guards -----------------------------------------------------------------------

    private async Task<Role> RequireRoleAsync(string name)
        => await roleRepo.GetByNameAsync(name ?? "")
           ?? throw new KeyNotFoundException($"Role '{name}' not found");

    /// <summary>
    /// Superadmins bypass every folder rule, so a rule attached to that role would be silently
    /// inert — an interface that shows a restriction it does not enforce. Refuse it outright
    /// instead.
    /// </summary>
    private static void RequireRoleCanCarryRules(Role role)
    {
        if (role.Name.Equals(UserRoles.Superadmin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Superadmins bypass all folder restrictions, so rules cannot be attached to the " +
                "'superadmin' role. Give the person a different role instead.");
    }

    private static void RequireNotSuperadminRole(Role role, string verb)
    {
        if (role.Name.Equals(UserRoles.Superadmin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The 'superadmin' role cannot be {verb}.");
    }
}
