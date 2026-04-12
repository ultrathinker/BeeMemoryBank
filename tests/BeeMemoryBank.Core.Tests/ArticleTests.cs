namespace BeeMemoryBank.Core.Tests;

public class ArticleTests : TestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("TestNode", "password");
        await Session.UnlockAsync("password");
    }

    [Fact]
    public async Task CreateAndRead_Roundtrip()
    {
        var article = await ArticleService.CreateAsync("My Article", "/Work", ["tag1", "tag2"], "Article content in Russian");

        var metadata = await ArticleService.GetMetadataAsync(article.Id);
        metadata.Should().NotBeNull();
        metadata!.Title.Should().Be("My Article");
        metadata.TreePath.Should().Be("/Work");
        metadata.Tags.Should().Contain("tag1").And.Contain("tag2");

        var content = await ArticleService.GetContentAsync(article.Id);
        content.Should().Be("Article content in Russian");
    }

    [Fact]
    public async Task Create_WhenLocked_Throws()
    {
        Session.Lock();
        var act = async () => await ArticleService.CreateAsync("X", "/", [], "text");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetContent_WhenLocked_Throws()
    {
        var article = await ArticleService.CreateAsync("X", "/", [], "text");
        Session.Lock();

        var act = async () => await ArticleService.GetContentAsync(article.Id);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdateContent_ReencryptsWithSameKey()
    {
        var article = await ArticleService.CreateAsync("Title", "/", [], "Old text");

        await ArticleService.UpdateAsync(article.Id, plaintext: "New text");

        var content = await ArticleService.GetContentAsync(article.Id);
        content.Should().Be("New text");
    }

    [Fact]
    public async Task UpdateMetadataOnly_DoesNotTouchBody()
    {
        var originalText = "Text should not change";
        var article = await ArticleService.CreateAsync("Old Title", "/", [], originalText);

        // Remember ciphertext BEFORE metadata update
        var bodyBefore = await GetBodyAsync(article.Id);

        await ArticleService.UpdateAsync(article.Id, title: "New Title");

        // ciphertext should remain the same
        var bodyAfter = await GetBodyAsync(article.Id);
        bodyAfter!.Ciphertext.Should().Equal(bodyBefore!.Ciphertext);
        bodyAfter.IV.Should().Equal(bodyBefore.IV);

        // Content reads correctly
        var content = await ArticleService.GetContentAsync(article.Id);
        content.Should().Be(originalText);

        // Metadata updated
        var meta = await ArticleService.GetMetadataAsync(article.Id);
        meta!.Title.Should().Be("New Title");
    }

    [Fact]
    public async Task Delete_SoftDeletes()
    {
        var article = await ArticleService.CreateAsync("Deletable", "/", [], "text");
        await ArticleService.DeleteAsync(article.Id);

        var metadata = await ArticleService.GetMetadataAsync(article.Id);
        metadata.Should().BeNull(); // soft deleted — not returned
    }

    [Fact]
    public async Task List_ByTreePath_FiltersCorrectly()
    {
        await ArticleService.CreateAsync("Article 1", "/Work/ProjectA", [], "text1");
        await ArticleService.CreateAsync("Article 2", "/Work/ProjectB", [], "text2");
        await ArticleService.CreateAsync("Article 3", "/Personal", [], "text3");

        var workArticles = await ArticleService.ListAsync("/Work");
        workArticles.Should().HaveCount(2);
        workArticles.Should().OnlyContain(a => a.TreePath.StartsWith("/Work"));

        var allArticles = await ArticleService.ListAsync();
        allArticles.Should().HaveCount(3);
    }

    private async Task<BeeMemoryBank.Core.Models.EncryptedArticleBody?> GetBodyAsync(Guid id)
    {
        // Access bodyRepo via DI — for the test we use ArticleService directly
        // via GetContentAsync + Storage (no need for reflection, just read through the service)
        // But to compare ciphertext we need bodyRepo directly.
        // Create it locally (same factory):
        var bodyRepo = new BeeMemoryBank.Storage.Sqlite.ArticleBodyRepository(Factory);
        return await bodyRepo.GetByArticleIdAsync(id);
    }
}
