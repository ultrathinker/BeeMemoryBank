using BeeMemoryBank.Core.Models;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The "hide an empty system folder" rule in TreeService used to answer "does this system root have
/// any article?" by loading the ENTIRE vault and scanning it in memory. It now asks the DB per
/// system root (CountAsync, scoped + prefix-narrowed in SQL). These lock in the behaviour that the
/// perf rewrite has to preserve: empty system folder hidden, system folder with a *nested* article
/// shown (the case a naive "direct children only" check gets wrong), non-system folder never hidden.
/// </summary>
public class TreeSystemFolderHideTests : TestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TreeNode", "password");
        await Session.UnlockAsync("password");
    }

    private async Task CreateSystemFolderAsync(string path, string? parentPath)
    {
        var name = path.TrimEnd('/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        await FolderRepo.CreateAsync(new Folder
        {
            Id = Guid.NewGuid(),
            Path = path,
            Name = name,
            ParentPath = parentPath,
            Status = "A",
            IsSystem = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    [Fact]
    public async Task EmptySystemFolder_IsHidden()
    {
        await CreateSystemFolderAsync("/_Sys", parentPath: "/");

        var tree = await TreeService.GetTreeAsync();

        tree.Should().NotContainKey("/_Sys", "an empty system folder is hidden from the tree");
    }

    [Fact]
    public async Task SystemFolderWithADirectArticle_IsShown()
    {
        await CreateSystemFolderAsync("/_Sys", parentPath: "/");
        await ArticleService.CreateAsync("Note", "/_Sys", [], "body");

        var tree = await TreeService.GetTreeAsync();

        tree.Should().ContainKey("/_Sys", "a system folder with content is shown");
    }

    [Fact]
    public async Task SystemFolderWhoseOnlyContentIsNested_IsShown()
    {
        await CreateSystemFolderAsync("/_Sys", parentPath: "/");
        await CreateSystemFolderAsync("/_Sys/sub", parentPath: "/_Sys");
        await ArticleService.CreateAsync("Nested", "/_Sys/sub", [], "body");

        var tree = await TreeService.GetTreeAsync();

        // The recursive (at-or-under) count is exactly what keeps this folder visible; a
        // direct-children-only check would wrongly hide it.
        tree.Should().ContainKey("/_Sys", "a system folder whose only article lives in a subfolder is still shown");
    }

    [Fact]
    public async Task EmptyNonSystemFolder_IsNeverHidden()
    {
        await FolderRepo.CreateAsync(new Folder
        {
            Id = Guid.NewGuid(), Path = "/Regular", Name = "Regular", ParentPath = "/",
            Status = "A", IsSystem = false, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });

        var tree = await TreeService.GetTreeAsync();

        tree.Should().ContainKey("/Regular", "only system folders are ever hidden for being empty");
    }
}
