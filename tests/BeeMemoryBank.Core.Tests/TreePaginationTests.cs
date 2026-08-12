using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Tests for <see cref="Services.TreeService.GetTreePathsAsync"/> — the depth-bounded, optionally
/// paginated tree view backing the <c>bee_get_tree</c> MCP tool (WP-19).
/// </summary>
public class TreePaginationTests : TestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");

        // A small, hand-checked tree:
        //   /DepthTest          (level 1)
        //   /DepthTest/A        (level 2)
        //   /DepthTest/A/B      (level 3)
        //   /DepthTest/A/B/C    (level 4)
        await ArticleService.CreateAsync("Root of depth test", "/DepthTest", [], "t");
        await ArticleService.CreateAsync("Level A", "/DepthTest/A", [], "t");
        await ArticleService.CreateAsync("Level B", "/DepthTest/A/B", [], "t");
        await ArticleService.CreateAsync("Level C", "/DepthTest/A/B/C", [], "t");
    }

    private static List<string> PathList(TreePathsResult r) => r.Paths.Select(p => p.Path).ToList();

    [Fact]
    public async Task NoLimits_ReturnsAllDescendants_UnboundedLegacyBehavior()
    {
        // Omitting depth/limit must reproduce the pre-WP-19 unbounded behavior: every descendant of
        // the scoped path comes back, no pagination, truncated == false.
        var result = await TreeService.GetTreePathsAsync(path: "/DepthTest");

        PathList(result).Should().Contain(new[]
        {
            "/DepthTest", "/DepthTest/A", "/DepthTest/A/B", "/DepthTest/A/B/C"
        });
        result.Limit.Should().BeNull();
        result.Offset.Should().Be(0);
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task DepthZero_ReturnsOnlyScopedPathItself()
    {
        var result = await TreeService.GetTreePathsAsync(path: "/DepthTest", depth: 0);

        // depth 0 = the scoped path itself, nothing below.
        PathList(result).Should().ContainSingle().Which.Should().Be("/DepthTest");
    }

    [Fact]
    public async Task DepthOne_DescendsExactlyOneLevelBelowScopedPath()
    {
        var result = await TreeService.GetTreePathsAsync(path: "/DepthTest", depth: 1);

        // /DepthTest (level 0 relative) + /DepthTest/A (level 1). /DepthTest/A/B is level 2 → excluded.
        PathList(result).Should().BeEquivalentTo(new[] { "/DepthTest", "/DepthTest/A" });
    }

    [Fact]
    public async Task Depth_FromRoot_CountsFromRootLevel()
    {
        // depth 1 from the whole tree: only root-level folders (level 1) and root.
        var result = await TreeService.GetTreePathsAsync(depth: 1);

        var paths = PathList(result);
        paths.Should().Contain("/DepthTest");
        paths.Should().NotContain("/DepthTest/A");
        paths.Should().NotContain("/DepthTest/A/B/C");
    }

    [Fact]
    public async Task Limit_PagesEntriesAlphabetically()
    {
        var all = await TreeService.GetTreePathsAsync(path: "/DepthTest");
        var allPaths = PathList(all);

        var page = await TreeService.GetTreePathsAsync(path: "/DepthTest", limit: 2);

        page.Paths.Should().HaveCount(2);
        page.Total.Should().Be(allPaths.Count);
        page.Truncated.Should().BeTrue();
        // First page = first two entries in alphabetical order.
        PathList(page).Should().Equal(allPaths.Take(2));
    }

    [Fact]
    public async Task Offset_SkipsAndWrapsToEnd()
    {
        var all = await TreeService.GetTreePathsAsync(path: "/DepthTest");
        var allPaths = PathList(all);
        var pageCount = allPaths.Count;

        var lastPage = await TreeService.GetTreePathsAsync(path: "/DepthTest", limit: 2, offset: pageCount - 1);

        lastPage.Paths.Should().HaveCount(1);
        lastPage.Paths[0].Path.Should().Be(allPaths[^1]);
        lastPage.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task LimitNull_OffsetAloneStillReturnsEverything()
    {
        // A caller passing offset without limit must not get an empty page — the unbounded contract
        // is preserved (offset only takes effect together with limit).
        var result = await TreeService.GetTreePathsAsync(path: "/DepthTest", offset: 3);

        PathList(result).Should().HaveCountGreaterThan(0);
        result.Limit.Should().BeNull();
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task LimitLargerThanTotal_ReturnsEverythingNotTruncated()
    {
        var result = await TreeService.GetTreePathsAsync(path: "/DepthTest", limit: 1000);

        result.Truncated.Should().BeFalse();
        result.Total.Should().Be(result.Paths.Count);
    }

    [Fact]
    public async Task DepthZero_OnThousandsOfFolders_ReturnsBoundedResponse()
    {
        // Synthetic 1000-folder subtree. This is the core WP-19 guarantee: even at scale, a
        // depth/limit cap returns a bounded slice instead of the whole subtree.
        const int n = 1000;
        for (var i = 0; i < n; i++)
            await FolderRepo.EnsureExistsAsync($"/Bulk/{i:D4}", sourceNodeId: null);

        // depth 0 → just /Bulk itself, regardless of the 1000 children.
        var depthBounded = await TreeService.GetTreePathsAsync(path: "/Bulk", depth: 0);
        depthBounded.Paths.Should().ContainSingle().Which.Path.Should().Be("/Bulk");

        // limit 50 → exactly 50 entries of the 1001 total (/Bulk + 1000 leaves).
        var paged = await TreeService.GetTreePathsAsync(path: "/Bulk", limit: 50);
        paged.Paths.Should().HaveCount(50);
        paged.Total.Should().Be(1001);
        paged.Truncated.Should().BeTrue();

        // No limits → all 1001 entries back (unbounded path still works).
        var unbounded = await TreeService.GetTreePathsAsync(path: "/Bulk");
        unbounded.Paths.Should().HaveCount(1001);
        unbounded.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task DepthAndLimit_Compose()
    {
        // depth caps the descent, then limit pages whatever remains. With depth 0 the scoped path
        // is the only entry, so limit must not change the count.
        var result = await TreeService.GetTreePathsAsync(path: "/DepthTest", depth: 0, limit: 10);
        PathList(result).Should().ContainSingle().Which.Should().Be("/DepthTest");
        result.Limit.Should().Be(10);
        result.Depth.Should().Be(0);
    }

    [Fact]
    public async Task Entries_CarryFolderFlagsAndArticles()
    {
        var result = await TreeService.GetTreePathsAsync(path: "/DepthTest/A/B");

        var entry = result.Paths.Single(p => p.Path == "/DepthTest/A/B");
        entry.Articles.Should().ContainSingle(a => a.Title == "Level B");
        // /DepthTest/A/B was auto-created (not a system folder, not remote).
        entry.IsSystem.Should().BeFalse();
        entry.IsRemote.Should().BeFalse();
    }
}
