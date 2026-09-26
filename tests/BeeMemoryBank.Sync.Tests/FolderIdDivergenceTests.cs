using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// A folder that EnsureExistsAsync vivified as an ancestor gets a fresh local id and no event of
/// its own, so two nodes can hold the same folder under different ids. A rename or delete of it
/// arriving over sync carries the sender's id; the applier must still find the local folder
/// (by path) and act on it. Found on the test stand: the desktop renamed /QA-S3/A to /QA-S3/A2,
/// the hub moved the subfolder and the article path but kept folder A — and the article, whose
/// folder_id still pointed at A, kept showing up under A.
/// </summary>
public class FolderIdDivergenceTests : IAsyncLifetime
{
    private SyncTestFixture _nodeA = null!;
    private SyncTestFixture _nodeB = null!;
    private FolderRepository _foldersA = null!;
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

        _foldersA = new FolderRepository(_nodeA.Factory, new CallerScopeHolder());
        _foldersB = new FolderRepository(_nodeB.Factory, new CallerScopeHolder());

        // Each node vivifies the same tree on its own — same paths, different ids.
        await _foldersA.EnsureExistsAsync("/S/A/B", null);
        await _foldersB.EnsureExistsAsync("/S/A/B", null);
    }

    public async Task DisposeAsync()
    {
        await _nodeA.DisposeAsync();
        await _nodeB.DisposeAsync();
    }

    [Fact]
    public async Task Rename_of_a_folder_known_under_another_id_renames_the_local_folder()
    {
        var localA = (await _foldersB.GetByPathAsync("/S/A"))!;
        var remoteA = (await _foldersA.GetByPathAsync("/S/A"))!;
        remoteA.Id.Should().NotBe(localA.Id, "the premise: both nodes vivified /S/A independently");

        var article = await _nodeB.ArticleService.CreateAsync("art", "/S/A", [], "body");
        (await _nodeB.ArticleRepo.GetByIdAsync(article.Id))!.FolderId.Should().Be(localA.Id);

        var identityA = (await _nodeA.NodeRepo.GetAsync())!;
        var ts = _nodeA.Clock.Tick();
        var now = DateTime.UtcNow;
        await _foldersA.RenamePathAsync("/S/A", "/S/A2", remoteA.Id, ts, identityA.NodeId, now);
        await _nodeA.EventLogger.LogFolderRenameAsync(remoteA.Id, "/S/A", "/S/A2", "A2", "/S", ts, now);
        var events = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);

        await _nodeB.ApplyFromAsync(_nodeA, events[^1]);

        (await _foldersB.GetByPathAsync("/S/A")).Should().BeNull("the folder itself must move, not only its contents");
        var renamed = await _foldersB.GetByPathAsync("/S/A2");
        renamed.Should().NotBeNull();
        renamed!.Id.Should().Be(localA.Id);
        (await _foldersB.GetByPathAsync("/S/A2/B")).Should().NotBeNull();
        var moved = (await _nodeB.ArticleRepo.GetByIdAsync(article.Id))!;
        moved.TreePath.Should().Be("/S/A2");
        moved.FolderId.Should().Be(renamed.Id);
    }

    [Fact]
    public async Task Delete_of_a_folder_known_under_another_id_deletes_the_local_folder()
    {
        var remoteB = (await _foldersA.GetByPathAsync("/S/A/B"))!;
        (await _foldersB.GetByPathAsync("/S/A/B"))!.Id.Should().NotBe(remoteB.Id);

        await _nodeA.EventLogger.LogFolderDeleteAsync(remoteB.Id, "/S/A/B", DateTime.UtcNow);
        var events = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);

        await _nodeB.ApplyFromAsync(_nodeA, events[^1]);

        (await _foldersB.GetByPathAsync("/S/A/B")).Should().BeNull();
        (await _foldersB.GetByPathAsync("/S/A")).Should().NotBeNull("only the named folder is deleted");
    }

    private sealed class ConcreteFixture : SyncTestFixture { }
}
