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
            conceptTags = new[] { "test", "integration" },
            content = "Content for integration test"
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var article = await create.Content.ReadFromJsonAsync<ArticleResponse>();
        article.Should().NotBeNull();
        article!.Title.Should().Be("Integration Test");
        article.ConceptTags.Should().Contain("test");

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

    // Exercises the actual HTTP multipart binding for POST /api/media, not just the service layer:
    // articleId and attachment are plain (unattributed) string/bool parameters on a handler that
    // also takes an IFormFile. Minimal APIs bind un-attributed simple parameters from the query
    // string by default even when a sibling IFormFile is present - they need an explicit
    // [FromForm] to read them from the multipart body the way the client actually sends them.
    // A service-level test bypasses ASP.NET's model binding entirely and can't catch this class of
    // bug; this one goes through the real endpoint.
    [Fact]
    public async Task UploadMedia_MultipartFormFields_BindAndLinkToArticle()
    {
        var unlock = await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password });
        unlock.EnsureSuccessStatusCode();

        var create = await _client.PostAsJsonAsync("/api/articles", new
        {
            title = "Article With Attachment",
            treePath = "/Tests",
            content = "Body text"
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var article = await create.Content.ReadFromJsonAsync<ArticleResponse>();

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("hello attachment"));
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(fileContent, "file", "notes.txt");
        form.Add(new StringContent(article!.Id.ToString()), "articleId");
        form.Add(new StringContent("true"), "attachment");

        var upload = await _client.PostAsync("/api/media", form);
        upload.StatusCode.Should().Be(HttpStatusCode.Created);
        var uploaded = await upload.Content.ReadFromJsonAsync<JsonElement>();
        uploaded.GetProperty("kind").GetString().Should().Be("attachment");

        var media = await _client.GetAsync($"/api/articles/{article.Id}/media");
        media.EnsureSuccessStatusCode();
        var mediaList = await media.Content.ReadFromJsonAsync<JsonElement>();
        mediaList.GetArrayLength().Should().Be(1);
        mediaList[0].GetProperty("id").GetString().Should().Be(uploaded.GetProperty("id").GetString());
        mediaList[0].GetProperty("fileName").GetString().Should().Be("notes.txt");

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
            content = "content"
        });

        await LockSessionAsync();

        var search = await _client.GetAsync("/api/search?q=Unique_Title_Search");
        search.EnsureSuccessStatusCode();
        var results = await search.Content.ReadFromJsonAsync<SearchResponse>();
        results.Articles.Should().ContainSingle(a => a.Title.Contains("Unique_Title_Search_Test"));
    }

    [Fact]
    public async Task Search_Paginates_ArticlesAcrossPages_WithTotalAndHasMore()
    {
        await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password });

        const int count = 25;
        for (int i = 0; i < count; i++)
        {
            var create = await _client.PostAsJsonAsync("/api/articles", new
            {
                title = $"Zpagx_{i:D2}_pagination_item",
                treePath = "/",
                content = "x"
            });
            create.EnsureSuccessStatusCode();
        }

        // Metadata (title) search — matches all 25 by the shared "Zpagx" token.
        var p1 = await GetSearchPageAsync("Zpagx", page: 1, pageSize: 10);
        p1.Page.Should().Be(1);
        p1.PageSize.Should().Be(10);
        p1.Total.Should().Be(count);
        p1.Articles.Should().HaveCount(10);
        p1.HasMore.Should().BeTrue();

        var p2 = await GetSearchPageAsync("Zpagx", page: 2, pageSize: 10);
        p2.Articles.Should().HaveCount(10);
        p2.HasMore.Should().BeTrue();
        p2.Folders.Should().BeEmpty("folders accompany the first page only");

        var p3 = await GetSearchPageAsync("Zpagx", page: 3, pageSize: 10);
        p3.Articles.Should().HaveCount(5);
        p3.HasMore.Should().BeFalse("page 3 is the last: 25 = 10 + 10 + 5");

        // No overlap across pages: every one of the 25 appears exactly once.
        var allIds = p1.Articles.Concat(p2.Articles).Concat(p3.Articles).Select(a => a.Id).ToList();
        allIds.Should().OnlyHaveUniqueItems();
        allIds.Should().HaveCount(count);

        // pageSize is clamped to [1,100]; page floors to 1.
        var clamped = await GetSearchPageAsync("Zpagx", page: 0, pageSize: 999);
        clamped.Page.Should().Be(1);
        clamped.PageSize.Should().Be(100);
    }

    private async Task<SearchResponse> GetSearchPageAsync(string q, int page, int pageSize)
    {
        var resp = await _client.GetAsync($"/api/search?q={q}&page={page}&pageSize={pageSize}");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<SearchResponse>())!;
    }

    [Fact]
    public async Task Tree_ReturnsKnownPaths()
    {
        await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password });

        await _client.PostAsJsonAsync("/api/articles", new
        {
            title = "Tree test article",
            treePath = "/Work/Dev",
            content = "x"
        });

        var tree = await _client.GetAsync("/api/tree");
        tree.EnsureSuccessStatusCode();
        var treeBody = await tree.Content.ReadAsStringAsync();
        treeBody.Should().Contain("/Work/Dev");
    }
}
