using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
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
        foreach(var e in events) await _nodeB.EventApplier.ApplyAsync(e);

        (await _nodeB.ArticleRepo.GetByIdAsync(article.Id)).Should().NotBeNull();

        // 2. Hard delete on NodeA
        await _nodeA.HardDeleteService.DeleteArticleAsync(article.Id, 1, null, CancellationToken.None);

        // 3. Sync HardDelete event to NodeB
        var lastEvent = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        lastEvent.EventType.Should().Be(EventTypes.HardDelete);

        await _nodeB.EventApplier.ApplyAsync(lastEvent);

        // 4. Verify NodeB is purged
        (await _nodeB.ArticleRepo.GetByIdAsync(article.Id)).Should().BeNull();
    }

    [Fact]
    public async Task UpdateAfterHardDelete_IsIgnored()
    {
        // 1. Create article on NodeA, sync to NodeB
        var article = await _nodeA.ArticleService.CreateAsync("Target", "/Work", new List<string>(), "Secret");
        var events = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);
        foreach(var e in events) await _nodeB.EventApplier.ApplyAsync(e);

        // 2. Hard delete on NodeB (locally)
        await _nodeB.HardDeleteService.DeleteArticleAsync(article.Id, 1, null, CancellationToken.None);

        // 3. NodeA updates article (doesn't know about hard delete)
        await _nodeA.ArticleService.UpdateAsync(article.Id, title: "New Title");
        var updateEvent = (await _nodeA.EventLogRepo.GetAfterSequenceAsync(0)).Last();
        updateEvent.EventType.Should().Be(EventTypes.ArticleUpdate);

        // 4. NodeB applies update
        await _nodeB.EventApplier.ApplyAsync(updateEvent);

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
            await _nodeB.EventApplier.ApplyAsync(e);

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
        await _nodeB.EventApplier.ApplyAsync(lastEvent);

        // 4. Verify NodeB cascaded correctly
        (await _nodeB.ArticleRepo.GetByIdAsync(a1.Id)).Should().BeNull();
        (await _nodeB.ArticleRepo.GetByIdAsync(a2.Id)).Should().BeNull();
        (await _nodeB.ArticleRepo.GetByIdAsync(a3.Id)).Should().NotBeNull();
    }

    private class ConcreteFixture : SyncTestFixture { }
}
