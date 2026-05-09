using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Core.Tests;

public class HardDeleteServiceTests : TestFixture
{
    private HardDeleteService _hardDeleteService = null!;
    private string _tempMediaDir = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        
        await InitService.InitializeAsync("admin", "TestNode", "password123");
        await Session.UnlockAsync("password123");

        _tempMediaDir = Path.Combine(Path.GetTempPath(), "bmb_media_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempMediaDir);

        var nodeRepo = new NodeIdentityRepository(Factory);
        var clock = new LamportClock();

        _hardDeleteService = new HardDeleteService(
            Factory, 
            new NullEventLogger(), 
            clock, 
            nodeRepo, 
            new MediaStorageOptions(_tempMediaDir));
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_tempMediaDir))
            Directory.Delete(_tempMediaDir, true);
    }

    [Fact]
    public async Task DeleteArticle_RemovesDataAndFiles()
    {
        // Arrange
        var article = await ArticleService.CreateAsync("Test Article", "/Work", new List<string>(), "Secret content");
        var mediaId = Guid.NewGuid();
        var mediaPath = Path.Combine(_tempMediaDir, $"{mediaId}.enc");
        await File.WriteAllBytesAsync(mediaPath, new byte[] { 1, 2, 3 });
        
        using (var conn = Factory.CreateConnection())
        {
            await conn.ExecuteAsync(
                "INSERT INTO tbl_media (id, article_id, file_name, content_type, file_size, encrypted_dek, dek_iv, iv, status, created_at) " +
                "VALUES (@id, @aid, 'test.png', 'image/png', 3, x'00', x'00', x'00', 'A', '2026-01-01')",
                new { id = mediaId, aid = article.Id });
        }

        // Act
        var result = await _hardDeleteService.DeleteArticleAsync(article.Id, 1, null, CancellationToken.None);

        // Assert
        result.DeletedArticles.Should().Be(1);
        result.DeletedMedia.Should().Be(1);

        using (var conn = Factory.CreateConnection())
        {
            var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_article WHERE id = @id", new { id = article.Id });
            count.Should().Be(0);

            var mediaCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_media WHERE article_id = @id", new { id = article.Id });
            mediaCount.Should().Be(0);
        }

        File.Exists(mediaPath).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteFolder_RemovesEverythingRecursively()
    {
        // Arrange
        await ArticleService.CreateAsync("Art 1", "/Work/Sub", new List<string>(), "Content 1");
        await ArticleService.CreateAsync("Art 2", "/Work/Sub", new List<string>(), "Content 2");
        await ArticleService.CreateAsync("Art 3", "/Work", new List<string>(), "Content 3");
        await ArticleService.CreateAsync("Art 4", "/Personal", new List<string>(), "Content 4");

        // Act
        var result = await _hardDeleteService.DeleteFolderAsync("/Work", 1, null, CancellationToken.None);

        // Assert
        result.DeletedArticles.Should().Be(3);
        result.DeletedFolders.Should().BeGreaterThanOrEqualTo(2); // /Work and /Work/Sub

        using (var conn = Factory.CreateConnection())
        {
            var artCount = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_article");
            artCount.Should().Be(1); // Only Art 4 remains
        }
    }

    [Fact]
    public async Task List_ReturnsCorrectItems()
    {
        // Arrange
        await ArticleService.CreateAsync("Alpha", "/A", new List<string>(), "Content");
        await ArticleService.CreateAsync("Beta", "/B", new List<string>(), "Content");
        var beta = (await ArticleService.ListAsync("/B")).First();
        await ArticleService.DeleteAsync(beta.Id);

        // Act
        var all = await _hardDeleteService.ListAsync(1, 100, null, HardDeleteStatusFilter.All, CancellationToken.None);
        var deleted = await _hardDeleteService.ListAsync(1, 100, null, HardDeleteStatusFilter.DeletedOnly, CancellationToken.None);

        // Assert
        all.Items.Should().Contain(i => i.Title == "Alpha");
        all.Items.Should().Contain(i => i.Title == "Beta");
        
        deleted.Items.Should().NotContain(i => i.Title == "Alpha");
        deleted.Items.Should().Contain(i => i.Title == "Beta");
    }
}
