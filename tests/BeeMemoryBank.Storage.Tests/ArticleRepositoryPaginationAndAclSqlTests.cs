using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Covers the WP that pushes <c>ArticleRepository.ListAsync</c>'s pagination and ACL filtering
/// into SQL instead of a full unbounded load + in-memory <c>Skip/Take</c>/<c>FilterArticles</c>
/// pass (see AGENTS.md's "Non-obvious invariants" and the ACL-scoping tests already in
/// <c>CallerScopeTests</c>, which this complements rather than duplicates).
/// </summary>
public class ArticleRepositoryPaginationAndAclSqlTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private CallerScopeHolder _systemHolder = null!;
    private ArticleRepository _systemArticleRepo = null!;
    private FolderRepository _systemFolderRepo = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_pagacl_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        _systemHolder = new CallerScopeHolder();
        _systemArticleRepo = new ArticleRepository(_factory, _systemHolder);
        _systemFolderRepo = new FolderRepository(_factory, _systemHolder);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<Article> CreateArticleAsync(string title, string treePath, Guid? folderId = null)
    {
        var article = new Article
        {
            Id = Guid.NewGuid(),
            Title = title,
            TreePath = treePath,
            FolderId = folderId,
            Status = "A",
            LamportTs = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _systemArticleRepo.CreateAsync(article);
        return article;
    }

    private async Task<Folder> CreateFolderAsync(string path, string name, string? parentPath = null)
    {
        var folder = new Folder
        {
            Id = Guid.NewGuid(),
            Path = path,
            Name = name,
            ParentPath = parentPath,
            Status = "A",
            LamportTs = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        await _systemFolderRepo.CreateAsync(folder);
        return folder;
    }

    // ---- Pagination: SQL LIMIT/OFFSET must return the same rows as an unbounded fetch's slice ----

    [Fact]
    public async Task Pagination_MiddlePage_MatchesUnboundedSlice()
    {
        const int total = 47;
        for (var i = 0; i < total; i++)
            await CreateArticleAsync($"Article {i:D3}", "/");

        var all = await _systemArticleRepo.ListAsync();
        all.Should().HaveCount(total);

        const int limit = 10;
        const int offset = 15;
        var page = await _systemArticleRepo.ListAsync(limit: limit, offset: offset);

        page.Should().HaveCount(limit);
        page.Select(a => a.Id).Should().Equal(all.Skip(offset).Take(limit).Select(a => a.Id));
    }

    [Fact]
    public async Task Pagination_LastPartialPage_ReturnsRemainder()
    {
        const int total = 25;
        for (var i = 0; i < total; i++)
            await CreateArticleAsync($"Article {i:D3}", "/");

        var page = await _systemArticleRepo.ListAsync(limit: 10, offset: 20);

        page.Should().HaveCount(5); // 25 total, offset 20 -> only 5 remain
    }

    [Fact]
    public async Task Pagination_LimitOnly_NeverReturnsMoreThanRequested()
    {
        const int total = 30;
        for (var i = 0; i < total; i++)
            await CreateArticleAsync($"Article {i:D3}", "/");

        var page = await _systemArticleRepo.ListAsync(limit: 5);

        page.Should().HaveCount(5);
    }

    [Fact]
    public async Task Pagination_OffsetWithoutLimit_StillReturnsEverything()
    {
        // Matches TreeService.GetTreePathsAsync's own forgiving contract: offset alone, with no
        // limit, must not silently produce an empty/partial page.
        const int total = 12;
        for (var i = 0; i < total; i++)
            await CreateArticleAsync($"Article {i:D3}", "/");

        var result = await _systemArticleRepo.ListAsync(offset: 5);

        result.Should().HaveCount(total);
    }

    [Fact]
    public async Task CountAsync_MatchesListAsync_UnboundedCount()
    {
        const int total = 18;
        for (var i = 0; i < total; i++)
            await CreateArticleAsync($"Article {i:D3}", "/");

        var count = await _systemArticleRepo.CountAsync();
        var all = await _systemArticleRepo.ListAsync();

        count.Should().Be(all.Count).And.Be(total);
    }

    /// <summary>
    /// Rough before/after row-materialization count for a "first page" request (see the task's
    /// DoD item 5). Counts rows actually READ off the SQLite driver -- not what a C#-side
    /// Skip/Take would keep afterwards -- for two SQL shapes:
    /// "before" is the exact SELECT <c>ArticleRepository.ListAsync</c> ran for a null treePath
    /// before this WP (no LIMIT clause existed at all); "after" is the same query with the
    /// LIMIT this WP adds. This isolates the SQL-level effect from the ACL-predicate change,
    /// which is covered separately by the tests above.
    /// </summary>
    [Fact]
    public async Task Pagination_MaterializesFarFewerRowsThanUnboundedFetch_ForAFirstPageRequest()
    {
        const int total = 2000;
        for (var i = 0; i < total; i++)
            await CreateArticleAsync($"Article {i:D4}", "/");

        using var conn = _factory.CreateConnection();

        var beforeRowsRead = CountRowsRead(conn,
            "SELECT a.id FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id WHERE a.status = 'A'");
        var afterRowsRead = CountRowsRead(conn,
            "SELECT a.id FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id WHERE a.status = 'A' LIMIT 20");

        beforeRowsRead.Should().Be(total);   // pre-WP: every active article, regardless of page size requested
        afterRowsRead.Should().Be(20);        // post-WP: exactly the requested page, database-side
    }

    private static int CountRowsRead(System.Data.IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var count = 0;
        while (reader.Read()) count++;
        return count;
    }

    // ---- Denied subtree stays invisible even at a scale where an in-memory filter would still
    //      "accidentally" work -- these specifically probe the SQL predicate itself. ----

    [Fact]
    public async Task DenyList_ListAsync_HidesDeniedSubtree_AmongManyRows()
    {
        var work = await CreateFolderAsync("/Work", "Work");
        var personal = await CreateFolderAsync("/Personal", "Personal");

        for (var i = 0; i < 20; i++)
            await CreateArticleAsync($"Work {i:D2}", "/Work", work.Id);
        for (var i = 0; i < 20; i++)
            await CreateArticleAsync($"Personal {i:D2}", "/Personal", personal.Id);

        var denyHolder = new CallerScopeHolder
        {
            Scope = new HttpCallerScope(false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Personal" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
        var denyRepo = new ArticleRepository(_factory, denyHolder);

        var visible = await denyRepo.ListAsync();

        visible.Should().HaveCount(20);
        visible.Should().OnlyContain(a => a.TreePath == "/Work");
    }

    [Fact]
    public async Task DenyList_FolderGetAllActiveAsync_HidesDeniedSubtree()
    {
        await CreateFolderAsync("/Work", "Work");
        await CreateFolderAsync("/Personal", "Personal");
        await CreateFolderAsync("/Personal/Secret", "Secret", "/Personal");

        var denyHolder = new CallerScopeHolder
        {
            Scope = new HttpCallerScope(false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Personal" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
        var denyRepo = new FolderRepository(_factory, denyHolder);

        var folders = await denyRepo.GetAllActiveAsync();

        folders.Select(f => f.Path).Should().BeEquivalentTo(new[] { "/Work" });
    }

    [Fact]
    public async Task Superadmin_ListAsync_SeesEverythingRegardlessOfOtherUsersAcl()
    {
        var work = await CreateFolderAsync("/Work", "Work");
        var personal = await CreateFolderAsync("/Personal", "Personal");
        await CreateArticleAsync("Work article", "/Work", work.Id);
        await CreateArticleAsync("Personal article", "/Personal", personal.Id);

        // _systemHolder's default scope is SystemCallerScope (superadmin-equivalent) -- exercise
        // it alongside an explicit HttpCallerScope(isSuperadmin: true, ...) that ALSO carries
        // deny/allow rows, to prove IsSuperadmin short-circuits the predicate before those rows
        // are ever consulted.
        var explicitSuperadminHolder = new CallerScopeHolder
        {
            Scope = new HttpCallerScope(true,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Work" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Personal" })
        };
        var explicitSuperadminRepo = new ArticleRepository(_factory, explicitSuperadminHolder);

        var systemView = await _systemArticleRepo.ListAsync();
        var explicitView = await explicitSuperadminRepo.ListAsync();

        systemView.Should().HaveCount(2);
        explicitView.Should().HaveCount(2);
    }

    // ---- LIKE-escaping: a folder whose path contains '%' or '_' must be matched literally, not
    //      as a wildcard, when it appears in an ACL deny/allow rule. ----

    // These two specifically exercise the LIKE-based DESCENDANT check (not the plain "=" exact
    // match, which is unaffected by escaping) -- the escaping bug only shows up for a SUBFOLDER of
    // the ACL path, where an unescaped '%'/'_' from the folder's own name would be misread as a
    // SQL wildcard by the "prefix/%" pattern. An article sitting directly AT the ACL path (no
    // descendant involved) would pass this test even with the bug, which is exactly the
    // "worthless test" trap the task warned about -- see the DoD-4 write-up in the task report for
    // the concrete before/after run that proves these two do NOT fall into that trap.

    [Fact]
    public async Task DenyList_LiteralPercentInPath_DoesNotActAsWildcard()
    {
        var denied = await CreateFolderAsync("/Te%st", "Te%st");
        var deniedChild = await CreateFolderAsync("/Te%st/Child", "Child", "/Te%st");
        // Decoy: an unrelated folder whose descendant path would match the UNESCAPED LIKE pattern
        // "/Te%st/%" (the '%' consumes "ANY", leaving a literal "st/" match) even though "/TeANYst"
        // is nothing like "/Te%st".
        var unrelated = await CreateFolderAsync("/TeANYst", "TeANYst");
        var unrelatedChild = await CreateFolderAsync("/TeANYst/Child", "Child", "/TeANYst");
        await CreateArticleAsync("Denied child", "/Te%st/Child", deniedChild.Id);
        var unrelatedArticle = await CreateArticleAsync("Unrelated child", "/TeANYst/Child", unrelatedChild.Id);

        var denyHolder = new CallerScopeHolder
        {
            Scope = new HttpCallerScope(false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Te%st" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
        var denyRepo = new ArticleRepository(_factory, denyHolder);

        var visible = await denyRepo.ListAsync();

        visible.Should().ContainSingle().Which.Id.Should().Be(unrelatedArticle.Id);
    }

    [Fact]
    public async Task AllowList_LiteralUnderscoreInPath_DoesNotActAsWildcard()
    {
        var allowed = await CreateFolderAsync("/Te_st", "Te_st");
        var allowedChild = await CreateFolderAsync("/Te_st/Child", "Child", "/Te_st");
        // Decoy: an unrelated folder whose descendant path would match the UNESCAPED LIKE pattern
        // "/Te_st/%" (the '_' matches any single char, here 'X') even though "/TeXst" is a
        // different folder name than "/Te_st".
        var unrelated = await CreateFolderAsync("/TeXst", "TeXst");
        var unrelatedChild = await CreateFolderAsync("/TeXst/Child", "Child", "/TeXst");
        var allowedArticle = await CreateArticleAsync("Allowed child", "/Te_st/Child", allowedChild.Id);
        await CreateArticleAsync("Unrelated child", "/TeXst/Child", unrelatedChild.Id);

        var allowHolder = new CallerScopeHolder
        {
            Scope = new HttpCallerScope(false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/Te_st" })
        };
        var allowRepo = new ArticleRepository(_factory, allowHolder);

        var visible = await allowRepo.ListAsync();

        visible.Should().ContainSingle().Which.Id.Should().Be(allowedArticle.Id);
    }
}
