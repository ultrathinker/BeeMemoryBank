using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// A folder deleted on one node while an article in it is edited on another. Whatever the order
/// the two events arrive in, every node has to end up with the article in the same place: in the
/// folder (which then stays or comes back) when the edit is newer than the delete, at the root
/// when the delete is newer. Found on the test stand (BMB-31, scenario 10c): the hub, which had
/// deleted /QA-S10c, got the desktop's edit and revived the folder with the article in it, while
/// the desktop, which got the delete after its edit, moved the article to the root — for good.
/// </summary>
public class FolderDeleteVersusEditTests : IAsyncLifetime
{
    private SyncTestFixture _nodeA = null!; // deletes the folder / sends events
    private SyncTestFixture _nodeB = null!; // applies them
    private FolderRepository _foldersB = null!;

    public async Task InitializeAsync()
    {
        _nodeA = new ConcreteFixture();
        await _nodeA.InitializeAsync();
        await _nodeA.InitService.InitializeAsync("admin", "NodeA", "passwordA");
        await _nodeA.Session.UnlockAsync("passwordA");

        _nodeB = new ConcreteFixture();
        await _nodeB.InitializeAsync();
        await _nodeB.InitService.InitializeAsync("admin", "NodeB", "passwordB");
        await _nodeB.Session.UnlockAsync("passwordB");

        var identityA = (await _nodeA.NodeRepo.GetAsync())!;
        var now = DateTime.UtcNow;
        await _nodeB.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identityA.NodeId,
            DisplayName = identityA.DisplayName,
            Ed25519PublicKey = identityA.Ed25519PublicKey,
            Status = "A", CreatedAt = now, UpdatedAt = now
        });

        _foldersB = new FolderRepository(_nodeB.Factory, new CallerScopeHolder());
    }

    public async Task DisposeAsync()
    {
        await _nodeA.DisposeAsync();
        await _nodeB.DisposeAsync();
    }

    // ── The delete arrives after a local edit ──

    [Fact]
    public async Task Delete_older_than_a_local_edit_keeps_the_article_and_its_folder()
    {
        var article = await _nodeB.ArticleService.CreateAsync("X", "/F", [], "v1");
        _nodeB.Clock.Update(100);
        await _nodeB.ArticleService.UpdateAsync(article.Id, plaintext: "v2 edited offline");

        _nodeA.Clock.Update(50);
        var delete = await LogFolderDeleteOnA("/F");

        await _nodeB.ApplyFromAsync(_nodeA, delete);

        (await _foldersB.GetByPathAsync("/F")).Should().NotBeNull(
            "the edit is newer than the delete, so the node that deleted the folder revives it when the edit reaches it");
        (await _nodeB.ArticleRepo.GetByIdAsync(article.Id))!.TreePath.Should().Be("/F");
    }

    [Fact]
    public async Task Delete_newer_than_a_local_edit_moves_the_article_to_the_root()
    {
        var article = await _nodeB.ArticleService.CreateAsync("X", "/F", [], "v1");

        _nodeA.Clock.Update(100);
        var delete = await LogFolderDeleteOnA("/F");

        await _nodeB.ApplyFromAsync(_nodeA, delete);

        (await _foldersB.GetByPathAsync("/F")).Should().BeNull();
        (await _nodeB.ArticleRepo.GetByIdAsync(article.Id))!.TreePath.Should().Be("/");
    }

    [Fact]
    public async Task Parent_with_a_subfolder_kept_by_a_newer_edit_is_kept_too()
    {
        var article = await _nodeB.ArticleService.CreateAsync("X", "/P/C", [], "v1");
        _nodeB.Clock.Update(100);
        await _nodeB.ArticleService.UpdateAsync(article.Id, plaintext: "v2");

        _nodeA.Clock.Update(50);
        var deleteChild = await LogFolderDeleteOnA("/P/C");  // the cascade logs descendants first
        var deleteParent = await LogFolderDeleteOnA("/P");

        await _nodeB.ApplyFromAsync(_nodeA, deleteChild);
        await _nodeB.ApplyFromAsync(_nodeA, deleteParent);

        (await _foldersB.GetByPathAsync("/P/C")).Should().NotBeNull();
        (await _foldersB.GetByPathAsync("/P")).Should().NotBeNull("a live subfolder keeps its parent");
        (await _nodeB.ArticleRepo.GetByIdAsync(article.Id))!.TreePath.Should().Be("/P/C");
    }

    // ── The edit arrives after the delete ──

    [Fact]
    public async Task Edit_newer_than_the_delete_revives_the_folder()
    {
        await _foldersB.EnsureExistsAsync("/F", null);
        _nodeA.Clock.Update(50);
        var delete = await LogFolderDeleteOnA("/F");
        await _nodeB.ApplyFromAsync(_nodeA, delete);
        (await _foldersB.GetByPathAsync("/F")).Should().BeNull("premise: the delete applied");

        _nodeA.Clock.Update(100);
        var article = await _nodeA.ArticleService.CreateAsync("Y", "/F", [], "body");
        await _nodeB.ApplyFromAsync(_nodeA, await LastEventOnA());

        (await _foldersB.GetByPathAsync("/F")).Should().NotBeNull();
        (await _nodeB.ArticleRepo.GetByIdAsync(article.Id))!.TreePath.Should().Be("/F");
    }

    [Fact]
    public async Task Edit_older_than_the_delete_lands_at_the_root_and_does_not_revive_the_folder()
    {
        await _foldersB.EnsureExistsAsync("/F", null);
        // The article event is made first (lower Lamport) but delivered after the delete.
        var article = await _nodeA.ArticleService.CreateAsync("Y", "/F", [], "body");
        var create = await LastEventOnA();
        _nodeA.Clock.Update(100);
        var delete = await LogFolderDeleteOnA("/F");

        await _nodeB.ApplyFromAsync(_nodeA, delete);
        await _nodeB.ApplyFromAsync(_nodeA, create);

        (await _foldersB.GetByPathAsync("/F")).Should().BeNull("the delete is newer than the article event");
        (await _nodeB.ArticleRepo.GetByIdAsync(article.Id))!.TreePath.Should().Be("/");
    }

    private async Task<SyncEvent> LogFolderDeleteOnA(string path)
    {
        await _nodeA.EventLogger.LogFolderDeleteAsync(Guid.NewGuid(), path, DateTime.UtcNow);
        return await LastEventOnA();
    }

    private async Task<SyncEvent> LastEventOnA() => (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0))[^1];

    private sealed class ConcreteFixture : SyncTestFixture { }
}
