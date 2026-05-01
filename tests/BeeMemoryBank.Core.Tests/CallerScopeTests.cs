using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Core.Tests;

public class CallerScopeTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private CallerScopeHolder _systemHolder = null!;
    private CallerScopeHolder _restrictedHolder = null!;
    private CallerScopeHolder _denyListHolder = null!;
    private ArticleRepository _systemArticleRepo = null!;
    private ArticleRepository _restrictedArticleRepo = null!;
    private ArticleRepository _denyListArticleRepo = null!;
    private FolderRepository _systemFolderRepo = null!;
    private FolderRepository _restrictedFolderRepo = null!;
    private FolderRepository _denyListFolderRepo = null!;
    private Folder _workFolder = null!;
    private Folder _personalFolder = null!;
    private Article _workArticle = null!;
    private Article _personalArticle = null!;
    private Article _rootArticle = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        _factory = DbConnectionFactory.CreateInMemory($"bmb_scope_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        _systemHolder = new CallerScopeHolder();
        _restrictedHolder = new CallerScopeHolder();
        _restrictedHolder.Scope = new HttpCallerScope(false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Work" });
        _denyListHolder = new CallerScopeHolder();
        _denyListHolder.Scope = new HttpCallerScope(false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Personal" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        _systemArticleRepo = new ArticleRepository(_factory, _systemHolder);
        _restrictedArticleRepo = new ArticleRepository(_factory, _restrictedHolder);
        _denyListArticleRepo = new ArticleRepository(_factory, _denyListHolder);
        _systemFolderRepo = new FolderRepository(_factory, _systemHolder);
        _restrictedFolderRepo = new FolderRepository(_factory, _restrictedHolder);
        _denyListFolderRepo = new FolderRepository(_factory, _denyListHolder);

        var folderRepo = new FolderRepository(_factory, _systemHolder);

        _workFolder = new Folder
        {
            Id = Guid.NewGuid(), Path = "/Work", Name = "Work",
            ParentPath = null, Status = "A", LamportTs = 0,
            SourceNodeId = null, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        await folderRepo.CreateAsync(_workFolder);

        _personalFolder = new Folder
        {
            Id = Guid.NewGuid(), Path = "/Personal", Name = "Personal",
            ParentPath = null, Status = "A", LamportTs = 0,
            SourceNodeId = null, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        await folderRepo.CreateAsync(_personalFolder);

        _rootArticle = new Article
        {
            Id = Guid.NewGuid(), Title = "Root Article", TreePath = "/",
            FolderId = null, Status = "A", LamportTs = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        await new ArticleRepository(_factory, _systemHolder).CreateAsync(_rootArticle);

        _workArticle = new Article
        {
            Id = Guid.NewGuid(), Title = "Work Article", TreePath = "/Work",
            FolderId = _workFolder.Id, Status = "A", LamportTs = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        await new ArticleRepository(_factory, _systemHolder).CreateAsync(_workArticle);

        _personalArticle = new Article
        {
            Id = Guid.NewGuid(), Title = "Personal Article", TreePath = "/Personal",
            FolderId = _personalFolder.Id, Status = "A", LamportTs = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        await new ArticleRepository(_factory, _systemHolder).CreateAsync(_personalArticle);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AllowList_ListAsync_ReturnsOnlyAllowedPaths()
    {
        var articles = await _restrictedArticleRepo.ListAsync();

        articles.Should().OnlyContain(a =>
            a.TreePath == "/Work" || a.TreePath.StartsWith("/Work/"));
    }

    [Fact]
    public async Task AllowList_GetByIdAsync_DeniesRestrictedArticle()
    {
        var article = await _restrictedArticleRepo.GetByIdAsync(_personalArticle.Id);

        article.Should().BeNull();
    }

    [Fact]
    public async Task AllowList_GetByIdAsync_AllowsAccessibleArticle()
    {
        var article = await _restrictedArticleRepo.GetByIdAsync(_workArticle.Id);

        article.Should().NotBeNull();
        article!.Title.Should().Be("Work Article");
    }

    [Fact]
    public async Task AllowList_SearchAsync_FiltersResults()
    {
        var articles = await _restrictedArticleRepo.SearchAsync("Article");

        articles.Should().OnlyContain(a =>
            a.TreePath == "/Work" || a.TreePath.StartsWith("/Work/"));
    }

    [Fact]
    public async Task AllowList_GetByIdsAsync_FiltersResults()
    {
        var articles = await _restrictedArticleRepo.GetByIdsAsync(
            [_workArticle.Id, _personalArticle.Id]);

        articles.Should().HaveCount(1);
        articles[0].Id.Should().Be(_workArticle.Id);
    }

    [Fact]
    public async Task AllowList_FolderGetByIdAsync_DeniesRestrictedFolder()
    {
        var folder = await _restrictedFolderRepo.GetByIdAsync(_personalFolder.Id);

        folder.Should().BeNull();
    }

    [Fact]
    public async Task AllowList_FolderGetByPathAsync_DeniesRestrictedFolder()
    {
        var folder = await _restrictedFolderRepo.GetByPathAsync("/Personal");

        folder.Should().BeNull();
    }

    [Fact]
    public async Task AllowList_FolderGetChildrenAsync_FiltersResults()
    {
        var folders = await _restrictedFolderRepo.GetChildrenAsync(null);

        folders.Should().OnlyContain(f => f.Path == "/Work");
    }

    [Fact]
    public async Task DenyList_ListAsync_ExcludesDeniedPaths()
    {
        var articles = await _denyListArticleRepo.ListAsync();

        articles.Should().NotContain(a => a.TreePath == "/Personal" || a.TreePath.StartsWith("/Personal/"));
        articles.Should().Contain(a => a.Id == _workArticle.Id);
    }

    [Fact]
    public async Task DenyList_GetByIdAsync_DeniesDeniedArticle()
    {
        var article = await _denyListArticleRepo.GetByIdAsync(_personalArticle.Id);

        article.Should().BeNull();
    }

    [Fact]
    public async Task DenyList_FolderSearchAsync_FiltersResults()
    {
        var folders = await _denyListFolderRepo.SearchAsync("Work");

        folders.Should().HaveCount(1);
        folders[0].Path.Should().Be("/Work");
    }

    [Fact]
    public async Task System_ListAsync_ReturnsAllArticles()
    {
        var articles = await _systemArticleRepo.ListAsync();

        articles.Should().HaveCount(3);
    }

    [Fact]
    public async Task System_GetByIdAsync_ReturnsAllArticles()
    {
        var work = await _systemArticleRepo.GetByIdAsync(_workArticle.Id);
        var personal = await _systemArticleRepo.GetByIdAsync(_personalArticle.Id);
        var root = await _systemArticleRepo.GetByIdAsync(_rootArticle.Id);

        work.Should().NotBeNull();
        personal.Should().NotBeNull();
        root.Should().NotBeNull();
    }

    [Fact]
    public async Task System_FolderGetAllActiveAsync_ReturnsAllFolders()
    {
        var folders = await _systemFolderRepo.GetAllActiveAsync();

        folders.Should().HaveCount(2);
    }

    [Fact]
    public async Task System_SearchAsync_ReturnsAllMatching()
    {
        var articles = await _systemArticleRepo.SearchAsync("Article");

        articles.Should().HaveCount(3);
    }

    [Fact]
    public async Task AllowList_DoesNotIncludeRoot_WhenRootNotInAllowList()
    {
        var articles = await _restrictedArticleRepo.ListAsync();

        articles.Should().NotContain(a => a.Id == _rootArticle.Id);
    }
}
