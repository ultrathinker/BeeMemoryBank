using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Who may read a comment through the API (BMB-36, found on the test stand in BMB-31):
///  - a protected article's comments open only for a browser session that unlocked the article, and
///    commenting on it needs that unlock;
///  - GET /api/comments applies the article's folder ACL — it used to list any article's comments
///    to any caller, including a user denied the folder.
/// </summary>
public sealed class CommentAccessTests : IAsyncLifetime
{
    private const string Password = "integrationPassword";
    private const string Phrase = "correct-horse";

    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _browser = null!;

    public async Task InitializeAsync()
    {
        await _factory.InitializeNodeAsync(password: Password);
        _browser = Browser("browser-a");
        (await _browser.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _browser.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient Browser(string webSession)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Web-Session", webSession);
        return client;
    }

    private async Task<Guid> CreateArticleAsync(string path)
    {
        var create = await _browser.PostAsJsonAsync("/api/articles", new { title = "t", treePath = path, content = "body" });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await create.Content.ReadFromJsonAsync<ArticleResponse>())!.Id;
    }

    [Fact]
    public async Task Protected_articles_comments_open_only_in_a_session_that_unlocked_it()
    {
        var id = await CreateArticleAsync("/Secret");
        // /protect also unlocks it for this browser session.
        (await _browser.PostAsJsonAsync($"/api/articles/{id}/protect", new { passphrase = Phrase })).EnsureSuccessStatusCode();
        (await _browser.PostAsJsonAsync("/api/comments", new { articleId = id, text = "behind the phrase" })).EnsureSuccessStatusCode();

        var mine = await _browser.GetFromJsonAsync<List<CommentBody>>($"/api/comments?articleId={id}");
        mine!.Single().Text.Should().Be("behind the phrase");

        using var other = Browser("browser-b");
        var theirs = await other.GetFromJsonAsync<List<CommentBody>>($"/api/comments?articleId={id}");
        theirs!.Single().Locked.Should().BeTrue("the other session never unlocked the article");
        theirs.Single().Text.Should().NotContain("behind the phrase");

        var write = await other.PostAsJsonAsync("/api/comments", new { articleId = id, text = "sneaky" });
        write.StatusCode.Should().Be(HttpStatusCode.Forbidden, "commenting needs the article unlocked");
    }

    [Fact]
    public async Task A_user_denied_the_folder_cannot_list_its_comments()
    {
        var visible = await CreateArticleAsync("/Open");
        var hidden = await CreateArticleAsync("/Closed");
        (await _browser.PostAsJsonAsync("/api/comments", new { articleId = hidden, text = "for admins" })).EnsureSuccessStatusCode();

        var userId = await CreateAllowListedUserAsync("/Open");
        using var restricted = _factory.CreateClient();
        restricted.DefaultRequestHeaders.Remove("X-User-Role");
        restricted.DefaultRequestHeaders.Add("X-User-Role", UserRoles.User);
        restricted.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());

        (await restricted.GetAsync($"/api/comments?articleId={visible}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var denied = await restricted.GetAsync($"/api/comments?articleId={hidden}");
        // 404 in practice: the caller's scope hides the article itself (it must not even confirm it exists).
        denied.StatusCode.Should().BeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.NotFound]);
        (await denied.Content.ReadAsStringAsync()).Should().NotContain("for admins");
    }

    private async Task<int> CreateAllowListedUserAsync(string allowedPath)
    {
        using var scope = _factory.Services.CreateScope();
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var folderRepo = scope.ServiceProvider.GetRequiredService<IFolderRepository>();
        var aclRepo = scope.ServiceProvider.GetRequiredService<IFolderAclRepository>();
        var access = scope.ServiceProvider.GetRequiredService<FolderAccessService>();

        var userId = await userRepo.CreateAsync(new User
        {
            Username = "restricted-" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Restricted",
            Role = UserRoles.User,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        var folder = await folderRepo.GetByPathAsync(allowedPath);
        await aclRepo.AddAsync(new FolderAclEntry
        {
            UserId = userId,
            FolderId = folder!.Id,
            Effect = AclEffect.Allow,
            CreatedAt = DateTime.UtcNow
        });
        access.InvalidateCache(userId);
        return userId;
    }

    private sealed record CommentBody(int Id, Guid ArticleId, string Text, DateTime CreatedAt, bool Locked);
}
