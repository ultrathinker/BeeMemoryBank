namespace BeeMemoryBank.Core.Tests;

public class TreeSearchTests : TestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");

        await ArticleService.CreateAsync("Boilers", "/Work/Infrastructure", ["devops", "linux"], "text1");
        await ArticleService.CreateAsync("Docker Compose", "/Work/Infrastructure", ["docker", "devops"], "text2");
        await ArticleService.CreateAsync("Borscht Recipe", "/Personal/Food", ["cooking"], "text3");
        await ArticleService.CreateAsync("Budget 2026", "/Personal/Finance", ["finance"], "text4");
    }

    [Fact]
    public async Task GetUniquePaths_ReturnsAllDistinct()
    {
        var paths = await TreeService.GetUniquePathsAsync();
        paths.Should().Contain("/Work/Infrastructure")
             .And.Contain("/Personal/Food")
             .And.Contain("/Personal/Finance");
    }

    [Fact]
    public async Task GetTree_ReturnsCorrectStructure()
    {
        var tree = await TreeService.GetTreeAsync();
        tree.Should().ContainKey("/Work/Infrastructure");
    }

    [Fact]
    public async Task Search_ByTitle_FindsArticle()
    {
        var results = await SearchService.SearchAsync("Docker");
        results.Articles.Should().HaveCount(1);
        results.Articles[0].Title.Should().Be("Docker Compose");
    }

    [Fact]
    public async Task Search_ByFolderName_FindsFolder()
    {
        var results = await SearchService.SearchAsync("Infrastructure");
        results.Folders.Should().HaveCount(1);
        results.Folders[0].Name.Should().Be("Infrastructure");
    }

    [Fact]
    public async Task Search_CaseInsensitive_Works()
    {
        var results = await SearchService.SearchAsync("DOCKER");
        results.Articles.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Search_NoMatch_ReturnsEmpty()
    {
        var results = await SearchService.SearchAsync("nonexistentquery12345");
        results.Articles.Should().BeEmpty();
        results.Folders.Should().BeEmpty();
    }
}
