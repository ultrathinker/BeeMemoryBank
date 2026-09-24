using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// "/Work" and "/work" are two different folders (tbl_folder.path is case-sensitive). Every subtree
/// operation — list, rename, soft delete, hard delete — must stay inside the folder it was asked
/// about. SQLite's LIKE folds ASCII case, which is how an operation on "/Work" used to reach into
/// "/work/…" as well; for a hard delete that meant irreversibly purging someone else's tree.
/// </summary>
public class CaseVariantFolderSubtreeTests : TestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("admin", "TestNode", "password123");
        await Session.UnlockAsync("password123");

        await ArticleService.CreateAsync("Mine", "/Work/Sub", [], "mine");
        await ArticleService.CreateAsync("Theirs", "/work/Other", [], "theirs");
        await ArticleService.CreateAsync("Cyr mine", "/\u041F\u0440\u043E\u0435\u043A\u0442\u044B/\u041F\u043B\u0430\u043D", [], "cyr mine");
        await ArticleService.CreateAsync("Cyr theirs", "/\u043F\u0440\u043E\u0435\u043A\u0442\u044B/\u0421\u0435\u043A\u0440\u0435\u0442", [], "cyr theirs");
    }

    [Fact]
    public async Task List_OfFolder_DoesNotIncludeCaseVariantSibling()
    {
        (await ArticleService.ListAsync("/Work")).Select(a => a.Title).Should().BeEquivalentTo(["Mine"]);
        (await ArticleService.ListAsync("/\u041F\u0440\u043E\u0435\u043A\u0442\u044B")).Select(a => a.Title).Should().BeEquivalentTo(["Cyr mine"]);
    }

    [Fact]
    public async Task Rename_DoesNotMoveCaseVariantSibling()
    {
        var work = await FolderRepo.GetByPathAsync("/Work");
        await FolderService.RenameAsync(work!.Id, "Job");

        (await FolderRepo.GetByPathAsync("/work/Other")).Should().NotBeNull();
        (await FolderRepo.GetByPathAsync("/Job/Sub")).Should().NotBeNull();
        (await FolderRepo.GetByPathAsync("/Job/Other")).Should().BeNull();
        (await ArticleService.ListAsync("/work")).Select(a => a.TreePath).Should().BeEquivalentTo(["/work/Other"]);
    }

    [Fact]
    public async Task SoftDelete_DoesNotCascadeIntoCaseVariantSibling()
    {
        var work = await FolderRepo.GetByPathAsync("/Work");
        await FolderService.DeleteAsync(work!.Id);

        (await FolderRepo.GetByPathAsync("/work/Other")).Should().NotBeNull();
        (await FolderRepo.GetByPathAsync("/Work/Sub")).Should().BeNull();
    }

    [Fact]
    public async Task HardDelete_DoesNotPurgeCaseVariantSibling()
    {
        var hardDelete = new HardDeleteService(
            Factory, new NullEventLogger(), new LamportClock(), new NodeIdentityRepository(Factory),
            new MediaStorageOptions(Path.Combine(Path.GetTempPath(), "bmb_media_" + Guid.NewGuid().ToString("N"))));

        var preview = await hardDelete.PreviewFolderAsync("/Work", CancellationToken.None);
        preview.ArticlesCount.Should().Be(1);

        var result = await hardDelete.DeleteFolderAsync("/Work", 1, null, CancellationToken.None);
        result.DeletedArticles.Should().Be(1);

        using var conn = Factory.CreateConnection();
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_article WHERE title = 'Theirs'")).Should().Be(1);
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_folder WHERE status = 'A' AND path GLOB '/work*'"))
            .Should().Be(2, "'/work' and '/work/Other' survive");
    }
}
