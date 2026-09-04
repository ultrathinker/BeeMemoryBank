using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Covers the WP that pushes ACL filtering for <see cref="Services.TreeService.GetTreePathsAsync"/>
/// (the <c>bee_get_tree</c> MCP tool's backing method) into
/// <c>ArticleRepository.ListAsync</c>/<c>FolderRepository.GetAllActiveAsync</c>'s SQL WHERE
/// clauses, instead of loading the whole vault and filtering with
/// <c>CallerScopeHolder</c>/<c>FolderAccessService</c> in memory. Complements
/// <see cref="TreePaginationTests"/> (which covers depth/limit/offset with an unrestricted
/// caller) by covering the ACL axis: a caller denied a subtree must not see it via the tree path,
/// exactly like <c>CallerScopeTests</c> already proves for the plain list path.
/// </summary>
public class TreeAclSqlPushdownTests : TestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");

        await ArticleService.CreateAsync("Work note", "/Work", [], "t");
        await ArticleService.CreateAsync("Work sub note", "/Work/Project1", [], "t");
        await ArticleService.CreateAsync("Personal note", "/Personal", [], "t");
        await ArticleService.CreateAsync("Personal secret note", "/Personal/Secret", [], "t");
    }

    [Fact]
    public async Task DenyList_GetTreePathsAsync_HidesDeniedSubtree_NoPathFilter()
    {
        ScopeHolder.Scope = new HttpCallerScope(false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Personal" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var result = await TreeService.GetTreePathsAsync();

        result.Paths.Select(p => p.Path).Should().NotContain(p => p == "/Personal" || p.StartsWith("/Personal/"));
        result.Paths.Should().Contain(p => p.Path == "/Work");
        // The denied folder's articles must not leak into some OTHER visible entry either.
        result.Paths.SelectMany(p => p.Articles).Should().NotContain(a => a.Title.StartsWith("Personal"));
    }

    [Fact]
    public async Task DenyList_GetTreePathsAsync_HidesDeniedSubtree_WhenScopedToThatSubtree()
    {
        // Explicitly asking for the denied path's own subtree must come back empty, not an
        // "access denied"-flavored error and not the real content -- same contract as the plain
        // list path (ArticleRepository.ListAsync under a deny-list scope for CallerScopeTests).
        ScopeHolder.Scope = new HttpCallerScope(false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Personal" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var result = await TreeService.GetTreePathsAsync(path: "/Personal");

        result.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task AllowList_GetTreePathsAsync_OnlyShowsAllowedSubtreePlusAncestorStubs()
    {
        ScopeHolder.Scope = new HttpCallerScope(false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Work/Project1" });

        var result = await TreeService.GetTreePathsAsync();
        var paths = result.Paths.Select(p => p.Path).ToList();

        paths.Should().Contain("/Work/Project1"); // the allowed subtree itself
        paths.Should().NotContain("/Personal");
        paths.Should().NotContain("/Personal/Secret");
        // "/Work" itself may appear as a navigation stub (ancestor of the allowed subtree), but
        // its own direct article ("Work note") must not leak through that stub.
        var workEntry = result.Paths.FirstOrDefault(p => p.Path == "/Work");
        if (workEntry != null)
            workEntry.Articles.Should().BeEmpty();
    }

    [Fact]
    public async Task Superadmin_GetTreePathsAsync_SeesEverything_EvenWithExplicitAclRows()
    {
        // An explicit HttpCallerScope(isSuperadmin: true, ...) carrying deny/allow rows must still
        // see everything -- IsSuperadmin short-circuits before those rows are ever consulted,
        // mirroring AGENTS.md's "an agent owned by a superadmin has no restrictions at all".
        ScopeHolder.Scope = new HttpCallerScope(true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Personal" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Work" });

        var result = await TreeService.GetTreePathsAsync();
        var paths = result.Paths.Select(p => p.Path).ToList();

        paths.Should().Contain("/Personal");
        paths.Should().Contain("/Personal/Secret");
        paths.Should().Contain("/Work");
        paths.Should().Contain("/Work/Project1");
    }
}
