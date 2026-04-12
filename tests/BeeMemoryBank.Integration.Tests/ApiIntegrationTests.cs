using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Models;

namespace BeeMemoryBank.Integration.Tests;

public class ApiIntegrationTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;
    private const string Password = "integrationPassword";

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ───────────────────── /health ─────────────────────

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var resp = await _client.GetAsync("/health");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ───────────────────── Session ─────────────────────

    [Fact]
    public async Task Session_Status_InitiallyLocked()
    {
        var resp = await _client.GetAsync("/api/session/status");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("isUnlocked").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Session_Unlock_WrongPassword_Returns401()
    {
        var resp = await _client.PostAsJsonAsync("/api/session/unlock", new { password = "wrong" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ───────────────────── Full cycle ─────────────────────

    [Fact]
    public async Task FullCycle_Init_Unlock_Create_List_GetContent_Update_Delete()
    {
        // Unlock
        var unlock = await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password });
        unlock.EnsureSuccessStatusCode();

        // Create
        var create = await _client.PostAsJsonAsync("/api/articles", new
        {
            title = "Integration Test",
            treePath = "/Tests",
            tags = new[] { "test", "integration" },
            content = "Content for integration test"
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var article = await create.Content.ReadFromJsonAsync<ArticleResponse>();
        article.Should().NotBeNull();
        article!.Title.Should().Be("Integration Test");
        article.Tags.Should().Contain("test");

        // List
        var list = await _client.GetAsync("/api/articles");
        list.EnsureSuccessStatusCode();
        var articles = await list.Content.ReadFromJsonAsync<List<ArticleResponse>>();
        articles.Should().ContainSingle(a => a.Id == article.Id);

        // Get metadata
        var meta = await _client.GetAsync($"/api/articles/{article.Id}");
        meta.EnsureSuccessStatusCode();

        // Get content
        var content = await _client.GetAsync($"/api/articles/{article.Id}/content");
        content.EnsureSuccessStatusCode();
        var contentBody = await content.Content.ReadFromJsonAsync<ArticleContentResponse>();
        contentBody!.Content.Should().Be("Content for integration test");

        // Update
        var update = await _client.PutAsJsonAsync($"/api/articles/{article.Id}", new
        {
            title = "Updated Title",
            content = "New Content"
        });
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<ArticleResponse>();
        updated!.Title.Should().Be("Updated Title");

        // Verify updated content
        var newContent = await _client.GetAsync($"/api/articles/{article.Id}/content");
        var newContentBody = await newContent.Content.ReadFromJsonAsync<ArticleContentResponse>();
        newContentBody!.Content.Should().Be("New Content");

        // Delete
        var delete = await _client.DeleteAsync($"/api/articles/{article.Id}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify deleted
        var afterDelete = await _client.GetAsync($"/api/articles/{article.Id}");
        afterDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Lock
        await LockSessionAsync();
    }

    [Fact]
    public async Task GetContent_WhenLocked_Returns403()
    {
        // Unlock, create article, lock, then try to get content
        await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password });

        var create = await _client.PostAsJsonAsync("/api/articles", new
        {
            title = "Secret Article",
            treePath = "/Secret",
            tags = Array.Empty<string>(),
            content = "Secret Text"
        });
        var article = await create.Content.ReadFromJsonAsync<ArticleResponse>();

        await LockSessionAsync();

        var content = await _client.GetAsync($"/api/articles/{article!.Id}/content");
        content.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task LockSessionAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/session/lock");
        req.Headers.Add("X-User-Role", "superadmin");
        var resp = await _client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task CreateArticle_WhenLocked_Returns403()
    {
        // Ensure locked
        await LockSessionAsync();

        var create = await _client.PostAsJsonAsync("/api/articles", new
        {
            title = "X",
            treePath = "/",
            tags = Array.Empty<string>(),
            content = "y"
        });
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ChangePassword_ThenUnlockWithNewPassword()
    {
        await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password });

        var change = await _client.PostAsJsonAsync("/api/keys/change-password", new
        {
            oldPassword = Password,
            newPassword = "newPassword123"
        });
        change.EnsureSuccessStatusCode();

        // Lock
        await LockSessionAsync();

        // Old password fails
        var oldUnlock = await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password });
        oldUnlock.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // New password works
        var newUnlock = await _client.PostAsJsonAsync("/api/session/unlock", new { password = "newPassword123" });
        newUnlock.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Search_ByTitle_FindsArticle()
    {
        await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password });

        await _client.PostAsJsonAsync("/api/articles", new
        {
            title = "Unique_Title_Search_Test",
            treePath = "/",
            tags = new[] { "search" },
            content = "content"
        });

        await LockSessionAsync();

        var search = await _client.GetAsync("/api/search?q=Unique_Title_Search");
        search.EnsureSuccessStatusCode();
        var results = await search.Content.ReadFromJsonAsync<List<ArticleResponse>>();
        results.Should().ContainSingle(a => a.Title.Contains("Unique_Title_Search_Test"));
    }

    [Fact]
    public async Task Tree_ReturnsKnownPaths()
    {
        await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password });

        await _client.PostAsJsonAsync("/api/articles", new
        {
            title = "Tree test article",
            treePath = "/Work/Dev",
            tags = Array.Empty<string>(),
            content = "x"
        });

        var tree = await _client.GetAsync("/api/tree");
        tree.EnsureSuccessStatusCode();
        var treeBody = await tree.Content.ReadAsStringAsync();
        treeBody.Should().Contain("/Work/Dev");
    }
}
