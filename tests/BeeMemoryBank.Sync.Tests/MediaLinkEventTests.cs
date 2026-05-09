using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Sync.Tests;

public class MediaLinkEventTests : IAsyncLifetime
{
    private SyncTestFixture _nodeA = null!;
    private SyncTestFixture _nodeB = null!;

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

        await CrossAddToWhitelists();
    }

    private async Task CrossAddToWhitelists()
    {
        var identityA = (await _nodeA.NodeRepo.GetAsync())!;
        var identityB = (await _nodeB.NodeRepo.GetAsync())!;
        var now = DateTime.UtcNow;

        await _nodeB.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identityA.NodeId, DisplayName = identityA.DisplayName,
            Ed25519PublicKey = identityA.Ed25519PublicKey,
            Status = "A", CreatedAt = now, UpdatedAt = now
        });

        await _nodeA.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identityB.NodeId, DisplayName = identityB.DisplayName,
            Ed25519PublicKey = identityB.Ed25519PublicKey,
            Status = "A", CreatedAt = now, UpdatedAt = now
        });
    }

    public async Task DisposeAsync()
    {
        await _nodeA.DisposeAsync();
        await _nodeB.DisposeAsync();
    }

    private async Task<Guid> InsertOrphanMediaOnNodeBAsync(Guid mediaId)
    {
        using var conn = _nodeB.Factory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_media (id, article_id, file_name, content_type, file_size,
              encrypted_dek, dek_iv, iv, status, lamport_ts, source_node_id, created_at)
              VALUES (@id, NULL, 'img.png', 'image/png', 200,
              @dek, @dekIv, @iv, 'A', 1, NULL, @now)",
            new { id = mediaId, dek = new byte[32], dekIv = new byte[12], iv = new byte[12], now = DateTime.UtcNow });
        return mediaId;
    }

    private async Task InsertDummyArticleOnNodeBAsync(Guid articleId)
    {
        using var conn = _nodeB.Factory.CreateConnection();
        var now = DateTime.UtcNow.ToString("o");
        await conn.ExecuteAsync(
            "INSERT OR IGNORE INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@id, 'dummy', '/', 'A', @now, @now)",
            new { id = articleId, now });
    }

    [Fact]
    public async Task MediaLinkEvent_LinksOrphanMediaOnRemoteNode()
    {
        var mediaId = Guid.NewGuid();
        var article = await _nodeA.ArticleService.CreateAsync("Article", "/", [], "body");
        var createEvts = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);
        await _nodeB.EventApplier.ApplyAsync(createEvts[0]);

        await InsertOrphanMediaOnNodeBAsync(mediaId);

        await _nodeA.EventLogger.LogMediaLinkAsync(mediaId, article.Id, _nodeA.Clock.Tick());
        var linkEvts = await _nodeA.EventLogRepo.GetAfterSequenceAsync(createEvts.Count);

        await _nodeB.EventApplier.ApplyAsync(linkEvts[0]);

        using var conn = _nodeB.Factory.CreateConnection();
        var linkedArticleId = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT article_id FROM tbl_media WHERE id = @id", new { id = mediaId });
        Guid.Parse(linkedArticleId!).Should().Be(article.Id);
    }

    [Fact]
    public async Task MediaLinkEvent_IgnoresAlreadyLinkedMedia()
    {
        var mediaId = Guid.NewGuid();
        var article = await _nodeA.ArticleService.CreateAsync("Article", "/", [], "body");
        var createEvts = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);
        await _nodeB.EventApplier.ApplyAsync(createEvts[0]);

        var otherArticleId = Guid.NewGuid();
        await InsertDummyArticleOnNodeBAsync(otherArticleId);
        await InsertOrphanMediaOnNodeBAsync(mediaId);
        using (var conn = _nodeB.Factory.CreateConnection())
        {
            await conn.ExecuteAsync(
                "UPDATE tbl_media SET article_id = @aid WHERE id = @id",
                new { aid = otherArticleId, id = mediaId });
        }

        await _nodeA.EventLogger.LogMediaLinkAsync(mediaId, article.Id, _nodeA.Clock.Tick());
        var linkEvts = await _nodeA.EventLogRepo.GetAfterSequenceAsync(createEvts.Count);

        await _nodeB.EventApplier.ApplyAsync(linkEvts[0]);

        using var conn2 = _nodeB.Factory.CreateConnection();
        var linkedArticleId = await conn2.QuerySingleOrDefaultAsync<string>(
            "SELECT article_id FROM tbl_media WHERE id = @id", new { id = mediaId });
        Guid.Parse(linkedArticleId!).Should().Be(otherArticleId);
    }

    private sealed class ConcreteFixture : SyncTestFixture { }
}
