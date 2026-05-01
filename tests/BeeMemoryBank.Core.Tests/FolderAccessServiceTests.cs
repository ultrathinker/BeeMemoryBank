using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

public class FolderAccessServiceTests
{
    private static HashSet<string> Set(params string[] paths) =>
        new(paths, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void IsAccessDenied_NoRules_AllowsAll()
    {
        Assert.False(FolderAccessService.IsAccessDenied(Set(), Set(), "/anything"));
        Assert.False(FolderAccessService.IsAccessDenied(Set(), Set(), "/"));
    }

    [Fact]
    public void IsAccessDenied_DenyOnly_MatchingPath_Denied()
    {
        var denied = Set("/Work/Secret");
        Assert.True(FolderAccessService.IsAccessDenied(denied, Set(), "/Work/Secret"));
        Assert.True(FolderAccessService.IsAccessDenied(denied, Set(), "/Work/Secret/Project1"));
        Assert.False(FolderAccessService.IsAccessDenied(denied, Set(), "/Work"));
        Assert.False(FolderAccessService.IsAccessDenied(denied, Set(), "/Work/Public"));
    }

    [Fact]
    public void IsAccessDenied_DenyOnly_SubfolderOfDenied_NotDenied()
    {
        var denied = Set("/Work");
        Assert.False(FolderAccessService.IsAccessDenied(denied, Set(), "/Workplace"));
    }

    [Fact]
    public void IsAccessDenied_AllowOnly_ExactPath_Allowed()
    {
        var allowed = Set("/Work/Project1");
        Assert.False(FolderAccessService.IsAccessDenied(Set(), allowed, "/Work/Project1"));
    }

    [Fact]
    public void IsAccessDenied_AllowOnly_SubfolderOfAllowed_Allowed()
    {
        var allowed = Set("/Work/Project1");
        Assert.False(FolderAccessService.IsAccessDenied(Set(), allowed, "/Work/Project1/Sub"));
        Assert.False(FolderAccessService.IsAccessDenied(Set(), allowed, "/Work/Project1/Sub/Deep"));
    }

    [Fact]
    public void IsAccessDenied_AllowOnly_ParentOfAllowed_Denied()
    {
        var allowed = Set("/Work/Project1");
        Assert.True(FolderAccessService.IsAccessDenied(Set(), allowed, "/Work"));
        Assert.True(FolderAccessService.IsAccessDenied(Set(), allowed, "/"));
    }

    [Fact]
    public void IsAccessDenied_AllowOnly_SiblingPrefix_Denied()
    {
        var allowed = Set("/Work/Project1");
        Assert.True(FolderAccessService.IsAccessDenied(Set(), allowed, "/Work/Project1Other"));
        Assert.True(FolderAccessService.IsAccessDenied(Set(), allowed, "/Work/Project12"));
    }

    [Fact]
    public void IsAccessDenied_AllowOnly_RootExplicitlyAllowed_AllowsAll()
    {
        var allowed = Set("/");
        Assert.False(FolderAccessService.IsAccessDenied(Set(), allowed, "/"));
        Assert.False(FolderAccessService.IsAccessDenied(Set(), allowed, "/anything"));
        Assert.False(FolderAccessService.IsAccessDenied(Set(), allowed, "/Work/Project1"));
    }

    [Fact]
    public void IsAccessDenied_AllowOnly_RootNotExplicitly_Denied()
    {
        var allowed = Set("/Work");
        Assert.True(FolderAccessService.IsAccessDenied(Set(), allowed, "/"));
    }

    [Fact]
    public void IsAccessDenied_DenyOnly_RootAlwaysAllowed()
    {
        var denied = Set("/Work");
        Assert.False(FolderAccessService.IsAccessDenied(denied, Set(), "/"));
    }

    [Fact]
    public void IsAccessDenied_NullOrEmptyPath_Denied()
    {
        Assert.True(FolderAccessService.IsAccessDenied(Set(), Set(), null));
        Assert.True(FolderAccessService.IsAccessDenied(Set(), Set(), ""));
    }

    [Fact]
    public void IsAccessDenied_Mixed_DenyWinsOverAllow()
    {
        var denied = Set("/Work/Project1");
        var allowed = Set("/Work/Project1");
        Assert.True(FolderAccessService.IsAccessDenied(denied, allowed, "/Work/Project1"));
        Assert.True(FolderAccessService.IsAccessDenied(denied, allowed, "/Work/Project1/Sub"));
    }

    [Fact]
    public void IsAccessDenied_Mixed_DenyWinsAllowPasses()
    {
        var denied = Set("/Work/Secret");
        var allowed = Set("/Work");
        Assert.True(FolderAccessService.IsAccessDenied(denied, allowed, "/Work/Secret"));
        Assert.False(FolderAccessService.IsAccessDenied(denied, allowed, "/Work/Public"));
        Assert.False(FolderAccessService.IsAccessDenied(denied, allowed, "/Work"));
    }

    [Fact]
    public void FilterArticles_AllowOnly_OnlyAllowedVisible()
    {
        var articles = new List<Article>
        {
            new() { TreePath = "/Work/Project1" },
            new() { TreePath = "/Work/Project2" },
            new() { TreePath = "/Personal" },
        };
        var allowed = Set("/Work/Project1");
        var result = FolderAccessService.FilterArticles(articles, Set(), allowed);
        Assert.Single(result);
        Assert.Equal("/Work/Project1", result[0].TreePath);
    }

    [Fact]
    public void FilterFolders_AllowOnly_OnlyAllowedVisible()
    {
        var folders = new List<Folder>
        {
            new() { Path = "/Work/Project1" },
            new() { Path = "/Work/Project2" },
            new() { Path = "/Personal" },
        };
        var allowed = Set("/Work/Project1");
        var result = FolderAccessService.FilterFolders(folders, Set(), allowed);
        Assert.Single(result);
        Assert.Equal("/Work/Project1", result[0].Path);
    }
}
