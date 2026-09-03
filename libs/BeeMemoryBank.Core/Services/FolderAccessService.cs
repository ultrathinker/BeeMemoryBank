using System.Collections.Concurrent;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Core.Services;

public class FolderAccessService
{
    private static readonly ConcurrentDictionary<string, (HashSet<string> denyPaths, HashSet<string> allowPaths, HashSet<string> readOnlyPaths, DateTime loadedAt)> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>Deny prefix that blocks every path — <c>MatchesAnyPrefix</c> short-circuits on "/".</summary>
    private const string DenyEverything = "/";

    private readonly IServiceProvider _serviceProvider;

    public FolderAccessService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Cache keys are namespaced by database. The dictionary is process-wide static (it has to
    /// outlive the scoped service instances that read it), while user ids restart at 1 in every
    /// database — so without this prefix two vaults open in one process would answer for each
    /// other's users. Production runs one vault per process; the test suite does not.
    /// </summary>
    private string CacheKeyPrefix =>
        _serviceProvider.GetService<IDbConnectionFactory>()?.DatabaseId ?? "default";

    private string UserCacheKey(int userId) => $"{CacheKeyPrefix}|u:{userId}";

    public async Task<(HashSet<string> denyPaths, HashSet<string> allowPaths)> GetAccessInfoAsync(int? userId, int? agentId = null)
    {
        var (deny, allow, _) = await GetFullAccessInfoAsync(userId);
        return (deny, allow);
    }

    /// <summary>
    /// Resolves the caller's effective folder rules: the union of the rules attached to their
    /// role and their own per-user rules. Deny-wins prefix matching then runs unchanged over the
    /// merged sets (see <see cref="IsAccessDenied"/>), so a role deny can never be widened by a
    /// per-user allow, and a read-only marking from either source sticks.
    /// <para>
    /// Roles do NOT inherit from one another: a user's role rules come from exactly their own
    /// role. Superadmins bypass folder rules entirely and resolve to empty sets.
    /// </para>
    /// </summary>
    public async Task<(HashSet<string> denyPaths, HashSet<string> allowPaths, HashSet<string> readOnlyPaths)> GetFullAccessInfoAsync(int? userId)
    {
        if (userId is null)
        {
            // Fail closed. "No user id" means an agent whose owner could not be resolved, or no
            // authenticated identity at all — CallerScopeMiddleware already decided both of those
            // deny everything, and the endpoints that call this directly (ArticleEndpoints,
            // TreeEndpoints, CopyEndpoints, …) bypass the middleware's scope and use these sets
            // raw. Returning empty sets here meant "no restrictions" to IsAccessDenied, i.e. full
            // vault access for a caller we could not identify.
            return (DenyAllSet(), [], []);
        }

        var cacheKey = UserCacheKey(userId.Value);

        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.loadedAt < CacheTtl)
            return (cached.denyPaths, cached.allowPaths, cached.readOnlyPaths);

        var userRepo = _serviceProvider.GetRequiredService<IUserRepository>();
        var repo = _serviceProvider.GetRequiredService<IFolderAclRepository>();
        var roleRepo = _serviceProvider.GetRequiredService<IRoleRepository>();
        var roleAclRepo = _serviceProvider.GetRequiredService<IRoleAclRepository>();
        var folderRepo = _serviceProvider.GetRequiredService<IFolderRepository>();

        var denyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allowPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readOnlyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var holder = _serviceProvider.GetRequiredService<CallerScopeHolder>();
        using (holder.ElevateToSystem())
        {
            var user = await userRepo.GetByIdAsync(userId.Value);

            if (user is null)
            {
                // Deactivated or deleted underneath us. Fail closed rather than falling through
                // to "no rows found, therefore no restrictions".
                denyPaths.Add(DenyEverything);
            }
            else if (user.Role == UserRoles.Superadmin)
            {
                // Superadmins bypass folder rules everywhere (HttpCallerScope.IsSuperadmin, and
                // every direct caller of this method gates on isSuperadmin first). Resolving to
                // empty sets keeps that true even if stale per-user rows survived a promotion.
            }
            else
            {
                var role = await roleRepo.GetByNameAsync(user.Role);
                if (role is null)
                {
                    // tbl_user.role names a role that no longer exists. RoleService refuses to
                    // delete a role anyone still holds, so this should be unreachable — but if
                    // the row is ever lost, the safe reading of "unknown policy" is "no access",
                    // matching DenyAllScope and the unresolvable-agent branch of
                    // CallerScopeMiddleware.
                    denyPaths.Add(DenyEverything);
                }
                else
                {
                    foreach (var entry in await roleAclRepo.GetByRoleNameAsync(role.Name))
                        await AddEntryAsync(folderRepo, entry.FolderId, entry.Effect, entry.IsReadOnly,
                            denyPaths, allowPaths, readOnlyPaths);

                    foreach (var entry in await repo.GetByUserIdAsync(userId.Value))
                        await AddEntryAsync(folderRepo, entry.FolderId, entry.Effect, entry.IsReadOnly,
                            denyPaths, allowPaths, readOnlyPaths);

                    // base_policy only decides what an EMPTY allow list means; a non-empty allow
                    // list is a whitelist under either policy, and IsAccessDenied already handles
                    // that. Expressing 'closed' as a deny on "/" keeps the matcher itself
                    // untouched: a later allow row cannot re-open it, because deny wins — which is
                    // the intended reading of "this role sees nothing until you say otherwise".
                    if (role.BasePolicy == RoleBasePolicy.Closed && allowPaths.Count == 0)
                        denyPaths.Add(DenyEverything);
                }
            }
        }

        _cache[cacheKey] = (denyPaths, allowPaths, readOnlyPaths, DateTime.UtcNow);
        return (denyPaths, allowPaths, readOnlyPaths);
    }

    /// <summary>Resolves one ACL row's folder to its current path and folds it into the sets.
    /// Deleted folders are included on purpose (<c>includeDeleted: true</c>): a deny row must not
    /// stop applying just because the folder was soft-deleted and could be restored.</summary>
    private static async Task AddEntryAsync(
        IFolderRepository folderRepo,
        Guid folderId,
        AclEffect effect,
        bool isReadOnly,
        HashSet<string> denyPaths,
        HashSet<string> allowPaths,
        HashSet<string> readOnlyPaths)
    {
        var folder = await folderRepo.GetByIdAsync(folderId, includeDeleted: true);
        if (folder is null) return;

        if (effect == AclEffect.Deny)
        {
            denyPaths.Add(folder.Path);
        }
        else
        {
            allowPaths.Add(folder.Path);
            if (isReadOnly)
                readOnlyPaths.Add(folder.Path);
        }
    }

    private static HashSet<string> DenyAllSet() =>
        new(StringComparer.OrdinalIgnoreCase) { DenyEverything };

    public static bool IsAccessDenied(HashSet<string> denyPaths, HashSet<string> allowPaths, string? treePath)
    {
        if (string.IsNullOrEmpty(treePath))
            return true;

        // 1. Deny wins: if path matches any deny prefix → denied
        if (MatchesAnyPrefix(treePath, denyPaths))
            return true;

        // 2. If no allow rows exist → no restrictions (sees everything)
        if (allowPaths.Count == 0)
            return false;

        // 3. Allow list is non-empty: path must match an allow prefix
        return !MatchesAnyPrefix(treePath, allowPaths);
    }

    // True when any write operation (create/update/delete/move/rename) on
    // treePath should be refused for this caller. Reuses IsAccessDenied as
    // the floor: anything denied for read is also denied for write. On top,
    // matching an allow-row with is_read_only=1 also yields "write denied".
    public static bool IsWriteDenied(
        HashSet<string> denyPaths,
        HashSet<string> allowPaths,
        HashSet<string> readOnlyPaths,
        string? treePath)
    {
        if (IsAccessDenied(denyPaths, allowPaths, treePath))
            return true;
        if (string.IsNullOrEmpty(treePath))
            return true;
        return MatchesAnyPrefix(treePath, readOnlyPaths);
    }

    // True when treePath matches an allow-with-is_read_only=1 entry.
    // Used together with IsAccessDenied so endpoints can return a distinct
    // "read-only" error message instead of generic "no permission".
    public static bool IsReadOnlyForCaller(HashSet<string> readOnlyPaths, string? treePath)
    {
        if (string.IsNullOrEmpty(treePath))
            return false;
        return MatchesAnyPrefix(treePath, readOnlyPaths);
    }

    private static bool MatchesAnyPrefix(string treePath, HashSet<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (treePath.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
            if (prefix == "/")
                return true;
            if (treePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void InvalidateCache(int? userId)
    {
        if (userId is not null)
            _cache.TryRemove(UserCacheKey(userId.Value), out _);
    }

    /// <summary>
    /// Drops the cached rules of every active user holding this role. Editing a role's folder
    /// rules has to reach N users, and the cache is keyed per user — without this fan-out the
    /// change would take up to <see cref="CacheTtl"/> to appear, which is a security bug in the
    /// tightening direction and a support ticket in the loosening one.
    /// </summary>
    public async Task InvalidateRoleAsync(string roleName)
    {
        var userRepo = _serviceProvider.GetRequiredService<IUserRepository>();
        foreach (var userId in await userRepo.GetUserIdsByRoleAsync(roleName))
            InvalidateCache(userId);
    }

    public async Task InvalidateCacheForFolderAsync(Guid folderId)
        => await InvalidateCacheForFoldersAsync([folderId]);

    /// <summary>
    /// Invalidates everyone whose rules mention any of these folders — both users with a direct
    /// per-user row and users holding a role that has a row. Called when folders are moved,
    /// renamed or deleted, since the cached sets hold resolved PATHS, not folder ids.
    /// </summary>
    public async Task InvalidateCacheForFoldersAsync(IEnumerable<Guid> folderIds)
    {
        var repo = _serviceProvider.GetRequiredService<IFolderAclRepository>();
        var roleAclRepo = _serviceProvider.GetRequiredService<IRoleAclRepository>();
        var userRepo = _serviceProvider.GetRequiredService<IUserRepository>();

        var userIds = new HashSet<int>();
        var roleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folderId in folderIds)
        {
            foreach (var id in await repo.GetUserIdsByFolderIdAsync(folderId))
                userIds.Add(id);
            foreach (var roleName in await roleAclRepo.GetRoleNamesByFolderIdAsync(folderId))
                roleNames.Add(roleName);
        }

        foreach (var roleName in roleNames)
            foreach (var id in await userRepo.GetUserIdsByRoleAsync(roleName))
                userIds.Add(id);

        foreach (var userId in userIds)
            InvalidateCache(userId);
    }

    /// <summary>
    /// Clears every cached entry, for every database. Used where the affected set is unknown or
    /// where the database itself was replaced wholesale — a node reset, a snapshot restore, a
    /// bulk import. Both of those reuse user ids starting from 1 under an unchanged database
    /// path, so the per-database key namespacing does NOT protect against them.
    /// <para>
    /// Static because it touches no instance state, which lets the singleton
    /// <c>SnapshotService</c> call it — injecting the scoped service into a singleton would
    /// fail scope validation.
    /// </para>
    /// <para>
    /// Never call this from a test fixture: it reaches into whatever other test class is running
    /// in parallel.
    /// </para>
    /// </summary>
    public static void InvalidateAll() => _cache.Clear();

    public static List<Article> FilterArticles(List<Article> articles, HashSet<string> denyPaths, HashSet<string> allowPaths)
    {
        if (denyPaths.Count == 0 && allowPaths.Count == 0)
            return articles;

        return articles.Where(a => !IsAccessDenied(denyPaths, allowPaths, a.TreePath)).ToList();
    }

    public static List<Folder> FilterFolders(List<Folder> folders, HashSet<string> denyPaths, HashSet<string> allowPaths)
    {
        if (denyPaths.Count == 0 && allowPaths.Count == 0)
            return folders;

        return folders.Where(f => !IsAccessDenied(denyPaths, allowPaths, f.Path)).ToList();
    }

    // Returns the set of ancestor paths of each allowed path.
    // E.g. {"/Work/Project2"} → {"/", "/Work"}.
    // Ancestors are shown as empty navigation stubs in the folder tree so the user
    // can walk down to their allowed subtree without exposing sibling folders.
    public static HashSet<string> ComputeAncestors(HashSet<string> allowedPaths)
    {
        var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/" };
        foreach (var path in allowedPaths)
        {
            if (string.IsNullOrEmpty(path) || path == "/") continue;
            var trimmed = path.TrimEnd('/');
            var idx = trimmed.IndexOf('/', 1);
            while (idx > 0)
            {
                ancestors.Add(trimmed[..idx]);
                idx = trimmed.IndexOf('/', idx + 1);
            }
        }
        return ancestors;
    }
}
