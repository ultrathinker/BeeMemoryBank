using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using Dapper;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

public class HardDeleteSyncTests : IAsyncLifetime
{
    private SyncTestFixture _nodeA = null!;
    private SyncTestFixture _nodeB = null!;

    public async Task InitializeAsync()
    {
        _nodeA = new ConcreteFixture();
        await _nodeA.InitializeAsync();
        await _nodeA.InitService.InitializeAsync("admin", "NodeA", "pass");
        await _nodeA.Session.UnlockAsync("pass");

        _nodeB = new ConcreteFixture();
        await _nodeB.InitializeAsync();
        await _nodeB.InitService.InitializeAsync("admin", "NodeB", "pass");
        await _nodeB.Session.UnlockAsync("pass");

        var identityA = (await _nodeA.NodeRepo.GetAsync())!;
        var identityB = (await _nodeB.NodeRepo.GetAsync())!;

        await _nodeB.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identityA.NodeId,
            DisplayName = identityA.DisplayName,
            Ed25519PublicKey = identityA.Ed25519PublicKey,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            IsSuperadmin = true  // hard-delete events require superadmin originator
        });

        await _nodeA.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identityB.NodeId,
            DisplayName = identityB.DisplayName,
            Ed25519PublicKey = identityB.Ed25519PublicKey,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            IsSuperadmin = true  // hard-delete events require superadmin originator
        });
    }

    public async Task DisposeAsync()
    {
        await _nodeA.DisposeAsync();
        await _nodeB.DisposeAsync();
    }

    [Fact]
    public async Task RemoteHardDelete_PurgesDataOnSubscriber()
    {
        // 1. Create article on NodeA, sync to NodeB
        var article = await _nodeA.ArticleService.CreateAsync("Target", "/Work", new List<string>(), "Secret");
        var events = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);
        foreach(var e in events) await _nodeB.ApplyFromAsync(_nodeA, e);

        (await _nodeB.ArticleRepo.GetByIdAsync(article.Id)).Should().NotBeNull();

        // 2. Hard delete on NodeA
        await _nodeA.HardDeleteService.DeleteArticleAsync(article.Id, 1, null, CancellationToken.None);

        // 3. Sync HardDelete event to NodeB
        var lastEvent = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        lastEvent.EventType.Should().Be(EventTypes.HardDelete);

        await _nodeB.ApplyFromAsync(_nodeA, lastEvent);

        // 4. Verify NodeB is purged
        (await _nodeB.ArticleRepo.GetByIdAsync(article.Id)).Should().BeNull();
    }

    [Fact]
    public async Task UpdateAfterHardDelete_IsIgnored()
    {
        // 1. Create article on NodeA, sync to NodeB
        var article = await _nodeA.ArticleService.CreateAsync("Target", "/Work", new List<string>(), "Secret");
        var events = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);
        foreach(var e in events) await _nodeB.ApplyFromAsync(_nodeA, e);

        // 2. Hard delete on NodeB (locally)
        await _nodeB.HardDeleteService.DeleteArticleAsync(article.Id, 1, null, CancellationToken.None);

        // 3. NodeA updates article (doesn't know about hard delete)
        await _nodeA.ArticleService.UpdateAsync(article.Id, title: "New Title");
        var updateEvent = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        updateEvent.EventType.Should().Be(EventTypes.ArticleUpdate);

        // 4. NodeB applies update
        await _nodeB.ApplyFromAsync(_nodeA, updateEvent);

        // 5. Verify NodeB still doesn't have the article
        (await _nodeB.ArticleRepo.GetByIdAsync(article.Id)).Should().BeNull();
    }

    [Fact]
    public async Task RemoteHardDeleteFolder_PurgesAllArticlesOnSubscriber()
    {
        // 1. Create 3 articles under /Work on NodeA, sync to NodeB
        var a1 = await _nodeA.ArticleService.CreateAsync("A1", "/Work", new List<string>(), "body1");
        var a2 = await _nodeA.ArticleService.CreateAsync("A2", "/Work/Sub", new List<string>(), "body2");
        var a3 = await _nodeA.ArticleService.CreateAsync("A3", "/Personal", new List<string>(), "body3");

        foreach (var e in await _nodeA.EventLogRepo.GetAfterSequenceAsync(0))
            await _nodeB.ApplyFromAsync(_nodeA, e);

        (await _nodeB.ArticleRepo.GetByIdAsync(a1.Id)).Should().NotBeNull();
        (await _nodeB.ArticleRepo.GetByIdAsync(a2.Id)).Should().NotBeNull();
        (await _nodeB.ArticleRepo.GetByIdAsync(a3.Id)).Should().NotBeNull();

        // 2. Hard delete folder /Work on NodeA (should cascade over a1 and a2, spare a3)
        await _nodeA.HardDeleteService.DeleteFolderAsync("/Work", 1, null, CancellationToken.None);

        (await _nodeA.ArticleRepo.GetByIdAsync(a1.Id)).Should().BeNull();
        (await _nodeA.ArticleRepo.GetByIdAsync(a2.Id)).Should().BeNull();
        (await _nodeA.ArticleRepo.GetByIdAsync(a3.Id)).Should().NotBeNull();

        // 3. Sync hard_delete event to NodeB
        var lastEvent = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        lastEvent.EventType.Should().Be(EventTypes.HardDelete);
        await _nodeB.ApplyFromAsync(_nodeA, lastEvent);

        // 4. Verify NodeB cascaded correctly
        (await _nodeB.ArticleRepo.GetByIdAsync(a1.Id)).Should().BeNull();
        (await _nodeB.ArticleRepo.GetByIdAsync(a2.Id)).Should().BeNull();
        (await _nodeB.ArticleRepo.GetByIdAsync(a3.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task QueuedUpdateAfterHardDeleteFolder_DoesNotResurrectArticleOrFolder()
    {
        // Reproduces the folder-purge resurrection bug directly, by driving a real queued edit
        // through the real applier -- not just by asserting that audit rows got written.
        //
        // Node roles are deliberately the same shape as the already-passing
        // UpdateAfterHardDelete_IsIgnored test above (deleter = the node that already received the
        // article via sync; the queued update comes from the ORIGINAL creator, applied at the
        // deleter). That is not an arbitrary choice: LamportClock.Update(remoteTs) sets
        // local = max(local, remoteTs) + 1 on every applied event, so a node that has already
        // received-and-applied an article's create event is always at least one tick ahead of that
        // article's creator. IsHardDeletedAsync's gate ("lamport_ts >= evt.LamportTs") depends on
        // the purge's timestamp being >= the resurrecting event's timestamp, and only this ordering
        // guarantees that deterministically -- creator-deletes/receiver-edits (the mirror image)
        // can fail the gate on lamport-ordering grounds alone, with or without this fix, because two
        // nodes that never synced with each other have no real-time ordering to agree on. That is a
        // pre-existing property of the gate design (see EventLogRepository.IsHardDeletedAsync), not
        // something this test is trying to prove; what THIS test isolates is the actual bug: before
        // the fix, a folder purge never wrote a row keyed by the article's own GUID at all, so the
        // gate had literally nothing to compare against, regardless of timestamps.
        //
        // 1. Create an article under /Clients/Acme on NodeA, sync it to NodeB.
        var article = await _nodeA.ArticleService.CreateAsync("Target", "/Clients/Acme", new List<string>(), "Secret");
        foreach (var e in await _nodeA.EventLogRepo.GetAfterSequenceAsync(0))
            await _nodeB.ApplyFromAsync(_nodeA, e);
        (await _nodeB.ArticleRepo.GetByIdAsync(article.Id)).Should().NotBeNull();

        // 2. NodeB hard-deletes the whole /Clients/Acme folder, purging the article.
        await _nodeB.HardDeleteService.DeleteFolderAsync("/Clients/Acme", 1, null, CancellationToken.None);
        (await _nodeB.ArticleRepo.GetByIdAsync(article.Id)).Should().BeNull();

        // 3. NodeA, unaware of the purge, updates the article and queues the resulting event.
        await _nodeA.ArticleService.UpdateAsync(article.Id, title: "Edited, Unaware Of Purge");
        var updateEvent = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        updateEvent.EventType.Should().Be(EventTypes.ArticleUpdate);

        // 4. NodeA's queued update finally reaches NodeB and gets applied.
        await _nodeB.ApplyFromAsync(_nodeA, updateEvent);

        // 5. Before the fix in HardDeleteService.PurgeFolderSubtreeAsync: a folder purge only ever
        // wrote ONE audit row, keyed by the folder's PATH ("/Clients/Acme") -- never by this
        // article's own GUID, which is what EventEntityId.Derive uses as an ordinary article
        // event's entity id (see EventEntityId.cs). IsHardDeletedAsync's
        // "entity_identifier = @entityId" lookup could therefore never match, NodeA's update sailed
        // through the gate in EventApplier.ApplyAsync, and ApplyArticleUpdateCoreAsync -- finding
        // no existing row for the article -- fell back to ApplyArticleCreateCoreAsync, which
        // recreated the article AND re-vivified the folder via its own EnsureExistsAsync call.
        // "Hard delete" wasn't a delete at all for an article whose folder was purged instead of
        // the article itself.
        (await _nodeB.ArticleRepo.GetByIdAsync(article.Id)).Should()
            .BeNull("the purged article must not be resurrected by a peer's queued edit");

        using var conn = _nodeB.Factory.CreateConnection();
        var folderCount = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tbl_folder WHERE path = @path", new { path = "/Clients/Acme" });
        folderCount.Should().Be(0, "the purged folder must not be re-vivified by the same resurrection attempt");
    }

    private class ConcreteFixture : SyncTestFixture { }
}
