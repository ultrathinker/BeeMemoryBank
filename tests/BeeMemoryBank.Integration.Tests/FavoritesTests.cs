using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Per-user favorites: default alphabetical order, manual up/down ordering, and the folder-ACL
/// boundary (a favorite must never show a title the caller is not allowed to see).
/// </summary>
public class FavoritesTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private const string Password = "testPassword123";

    private HttpClient _admin = null!;
    private int _adminUserId;

    public async Task InitializeAsync()
    {
        _admin = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);

        var unlock = await _admin.PostAsJsonAsync("/api/session/unlock", new { password = Password });
        unlock.EnsureSuccessStatusCode();

        var login = await _admin.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password });
        login.EnsureSuccessStatusCode();
        _adminUserId = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetInt32();

        _admin.DefaultRequestHeaders.Add("X-User-Id", _adminUserId.ToString());
    }

    public Task DisposeAsync()
    {
        _admin.Dispose();
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────

    private async Task<Guid> CreateArticleAsync(string title, string treePath = "/Notes")
    {
        var resp = await _admin.PostAsJsonAsync("/api/articles", new
        {
            title,
            treePath,
            content = "body of " + title
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<(List<string> Titles, bool ManualOrder)> ReadListAsync(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var titles = body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("title").GetString()!)
            .ToList();
        return (titles, body.GetProperty("manualOrder").GetBoolean());
    }

    private Task<(List<string> Titles, bool ManualOrder)> ListAsync(HttpClient? client = null) =>
        (client ?? _admin).GetAsync("/api/favorites").ContinueWith(t => ReadListAsync(t.Result)).Unwrap();

    private async Task StarAsync(Guid articleId, HttpClient? client = null)
    {
        var resp = await (client ?? _admin).PostAsync($"/api/favorites/{articleId}", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private Task<HttpResponseMessage> MoveAsync(Guid articleId, string direction) =>
        _admin.PostAsJsonAsync($"/api/favorites/{articleId}/move", new { direction });

    private HttpClient ClientAs(int userId, string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-User-Role");
        client.DefaultRequestHeaders.Add("X-User-Role", role);
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());
        return client;
    }

    // ─────────────────────────────────────────────────────────────
    // Ordering
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_DefaultsToAlphabeticalOrder_AcrossAlphabetsAndCase()
    {
        // Deliberately mixed case and scripts: SQLite's COLLATE NOCASE would put "apple" after
        // "Zebra" (raw code points) and scatter the Cyrillic titles, which is why the ordering is
        // done with an invariant, case-insensitive comparison instead.
        var ids = new[] { "Zebra", "apple", "Мышь", "банан" }
            .Select(t => CreateArticleAsync(t).Result)
            .ToList();
        foreach (var id in ids) await StarAsync(id);

        var (titles, manualOrder) = await ListAsync();

        titles.Should().Equal("apple", "Zebra", "банан", "Мышь");
        manualOrder.Should().BeFalse();
    }

    [Fact]
    public async Task Move_SwitchesToManualOrder_AndReorderingSurvivesAReload()
    {
        var a = await CreateArticleAsync("alpha");
        var b = await CreateArticleAsync("beta");
        var c = await CreateArticleAsync("gamma");
        foreach (var id in new[] { a, b, c }) await StarAsync(id);

        (await MoveAsync(c, "up")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var (titles, manualOrder) = await ListAsync();
        titles.Should().Equal("alpha", "gamma", "beta");
        manualOrder.Should().BeTrue();
    }

    [Fact]
    public async Task Move_Down_MovesOnePositionOnly()
    {
        var a = await CreateArticleAsync("alpha");
        var b = await CreateArticleAsync("beta");
        var c = await CreateArticleAsync("gamma");
        foreach (var id in new[] { a, b, c }) await StarAsync(id);

        await MoveAsync(a, "down");

        var (titles, _) = await ListAsync();
        titles.Should().Equal("beta", "alpha", "gamma");
    }

    [Fact]
    public async Task Move_AtTheEdge_IsANoOp_AndKeepsTheListAlphabetical()
    {
        var a = await CreateArticleAsync("alpha");
        var b = await CreateArticleAsync("beta");
        foreach (var id in new[] { a, b }) await StarAsync(id);

        // A stray click on the first row's up-arrow must not silently freeze the list into
        // manual mode — otherwise new favorites would stop sorting themselves in.
        (await MoveAsync(a, "up")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var (titles, manualOrder) = await ListAsync();
        titles.Should().Equal("alpha", "beta");
        manualOrder.Should().BeFalse();
    }

    [Fact]
    public async Task NewFavorite_InAManualList_LandsOnTop()
    {
        var a = await CreateArticleAsync("alpha");
        var b = await CreateArticleAsync("beta");
        await StarAsync(a);
        await StarAsync(b);
        await MoveAsync(b, "up"); // list is now manual: beta, alpha

        var z = await CreateArticleAsync("zeta");
        await StarAsync(z);

        var (titles, manualOrder) = await ListAsync();
        titles.Should().Equal("zeta", "beta", "alpha");
        manualOrder.Should().BeTrue();
    }

    [Fact]
    public async Task ResetOrder_ReturnsToAlphabetical()
    {
        var a = await CreateArticleAsync("alpha");
        var b = await CreateArticleAsync("beta");
        await StarAsync(a);
        await StarAsync(b);
        await MoveAsync(b, "up");

        var reset = await _admin.PostAsync("/api/favorites/reset-order", content: null);
        reset.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var (titles, manualOrder) = await ListAsync();
        titles.Should().Equal("alpha", "beta");
        manualOrder.Should().BeFalse();
    }

    [Fact]
    public async Task Move_WithAnUnknownDirection_IsRejected()
    {
        var a = await CreateArticleAsync("alpha");
        await StarAsync(a);

        var resp = await MoveAsync(a, "sideways");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Star_IsIdempotent_AndUnstarRemoves()
    {
        var a = await CreateArticleAsync("alpha");

        await StarAsync(a);
        await StarAsync(a);
        (await ListAsync()).Titles.Should().Equal("alpha");

        var del = await _admin.DeleteAsync($"/api/favorites/{a}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ListAsync()).Titles.Should().BeEmpty();
    }

    [Fact]
    public async Task DeletedArticle_DropsOutOfTheList()
    {
        var a = await CreateArticleAsync("alpha");
        var b = await CreateArticleAsync("beta");
        await StarAsync(a);
        await StarAsync(b);

        (await _admin.DeleteAsync($"/api/articles/{a}")).EnsureSuccessStatusCode();

        (await ListAsync()).Titles.Should().Equal("beta");
    }

    [Fact]
    public async Task Star_OnAMissingArticle_Returns404()
    {
        var resp = await _admin.PostAsync($"/api/favorites/{Guid.NewGuid()}", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Favorites_ArePerUser()
    {
        var a = await CreateArticleAsync("alpha");
        await StarAsync(a);

        int otherUserId;
        using (var scope = _factory.Services.CreateScope())
        {
            var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            otherUserId = await userRepo.CreateAsync(new User
            {
                Username = "colleague",
                DisplayName = "Colleague",
                Role = UserRoles.Superadmin,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
        }

        using var other = ClientAs(otherUserId, UserRoles.Superadmin);

        (await ListAsync(other)).Titles.Should().BeEmpty();
        (await ListAsync()).Titles.Should().Equal("alpha");
    }

    // ─────────────────────────────────────────────────────────────
    // Folder ACL
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RestrictedUser_CannotStarAnArticleOutsideTheirScope()
    {
        var visible = await CreateArticleAsync("visible", "/Open");
        var hidden = await CreateArticleAsync("hidden", "/Closed");

        var userId = await CreateAllowListedUserAsync("/Open");
        using var restricted = ClientAs(userId, UserRoles.User);

        // 404, not 403: the star must not confirm that an article exists in a folder the caller
        // cannot even navigate to.
        var denied = await restricted.PostAsync($"/api/favorites/{hidden}", content: null);
        denied.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var allowed = await restricted.PostAsync($"/api/favorites/{visible}", content: null);
        allowed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ListAsync(restricted)).Titles.Should().Equal("visible");
    }

    [Fact]
    public async Task LosingAccessToAFolder_HidesItsArticlesFromTheFavoritesList()
    {
        var article = await CreateArticleAsync("visible", "/Open");
        await CreateArticleAsync("elsewhere", "/Elsewhere");
        var userId = await CreateAllowListedUserAsync("/Open");

        using var restricted = ClientAs(userId, UserRoles.User);
        await StarAsync(article, restricted);
        (await ListAsync(restricted)).Titles.Should().Equal("visible");

        // Move the user's allow-list off /Open and onto another folder. The favorite row stays in
        // tbl_favorite, but the list must stop leaking the title of an article they can no longer
        // open. (Simply deleting the entry would not test this: with no entries left the user has
        // no allow-list at all, which means full access, not none.)
        using (var scope = _factory.Services.CreateScope())
        {
            var folderRepo = scope.ServiceProvider.GetRequiredService<IFolderRepository>();
            var aclRepo = scope.ServiceProvider.GetRequiredService<IFolderAclRepository>();
            var access = scope.ServiceProvider.GetRequiredService<FolderAccessService>();

            var open = await folderRepo.GetByPathAsync("/Open");
            var elsewhere = await folderRepo.GetByPathAsync("/Elsewhere");
            await aclRepo.AddAsync(new FolderAclEntry
            {
                UserId = userId,
                FolderId = elsewhere!.Id,
                Effect = AclEffect.Allow,
                CreatedAt = DateTime.UtcNow
            });
            await aclRepo.RemoveByUserAndFolderAsync(userId, open!.Id);
            access.InvalidateCache(userId);
        }

        (await ListAsync(restricted)).Titles.Should().BeEmpty();
    }

    [Fact]
    public async Task Reordering_VisibleFavorites_LeavesAHiddenOneWhereItWas()
    {
        var bravo = await CreateArticleAsync("bravo", "/Open");
        var charlie = await CreateArticleAsync("charlie", "/Open");
        var alpha = await CreateArticleAsync("alpha", "/Closed");

        var userId = await CreateAllowListedUserAsync("/Open");
        await AllowFolderAsync(userId, "/Closed");

        using var restricted = ClientAs(userId, UserRoles.User);
        foreach (var id in new[] { bravo, charlie, alpha }) await StarAsync(id, restricted);

        // Curate an order while everything is visible: alpha, charlie, bravo.
        var move = await restricted.PostAsJsonAsync($"/api/favorites/{charlie}/move", new { direction = "up" });
        move.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ListAsync(restricted)).Titles.Should().Equal("alpha", "charlie", "bravo");

        // Lose access to the folder holding the top item, then reorder the two that are still visible.
        await RevokeFolderAsync(userId, "/Closed");
        (await ListAsync(restricted)).Titles.Should().Equal("charlie", "bravo");
        (await restricted.PostAsJsonAsync($"/api/favorites/{bravo}/move", new { direction = "up" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Access comes back: the hidden favorite must still be first. Renumbering the whole list on
        // every move would have silently demoted it to the bottom instead.
        await AllowFolderAsync(userId, "/Closed");
        (await ListAsync(restricted)).Titles.Should().Equal("alpha", "bravo", "charlie");
    }

    [Fact]
    public async Task AfterAllFavoritesAreDeleted_ANewStarStartsAFreshAlphabeticalList()
    {
        var a = await CreateArticleAsync("alpha");
        var b = await CreateArticleAsync("beta");
        await StarAsync(a);
        await StarAsync(b);
        await MoveAsync(b, "up"); // manual order, positions stored

        (await _admin.DeleteAsync($"/api/articles/{a}")).EnsureSuccessStatusCode();
        (await _admin.DeleteAsync($"/api/articles/{b}")).EnsureSuccessStatusCode();

        // The rows survive the soft delete (the articles can come back from the trash), so the
        // ordering mode must be judged on what the user can actually see — otherwise a brand new
        // list inherits negative positions from the deleted ones and claims to be hand-sorted.
        var zeta = await CreateArticleAsync("zeta");
        var gamma = await CreateArticleAsync("gamma");
        await StarAsync(zeta);
        await StarAsync(gamma);

        var (titles, manualOrder) = await ListAsync();
        titles.Should().Equal("gamma", "zeta");
        manualOrder.Should().BeFalse();
    }

    [Fact]
    public async Task Move_WithDuplicateStoredPositions_StillReorders()
    {
        var a = await CreateArticleAsync("alpha");
        var b = await CreateArticleAsync("beta");
        await StarAsync(a);
        await StarAsync(b);
        await MoveAsync(b, "up"); // switch the list to manual order

        // Two rows can legitimately end up on the same position: a new star takes "lowest visible
        // position - 1" while a row hidden from the caller already holds that number. Swapping two
        // identical numbers would leave the arrows doing nothing at all.
        using (var scope = _factory.Services.CreateScope())
        {
            var favRepo = scope.ServiceProvider.GetRequiredService<IFavoriteRepository>();
            await favRepo.SetSortOrdersAsync(_adminUserId, [(a, 5), (b, 5)]);
        }

        // Equal positions leave the title as the only tie-break.
        (await ListAsync()).Titles.Should().Equal("alpha", "beta");

        (await MoveAsync(b, "up")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ListAsync()).Titles.Should().Equal("beta", "alpha");

        // And the repaired positions are distinct again, so the next move works normally.
        (await MoveAsync(a, "up")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ListAsync()).Titles.Should().Equal("alpha", "beta");
    }

    private async Task AllowFolderAsync(int userId, string path)
    {
        using var scope = _factory.Services.CreateScope();
        var folderRepo = scope.ServiceProvider.GetRequiredService<IFolderRepository>();
        var aclRepo = scope.ServiceProvider.GetRequiredService<IFolderAclRepository>();
        var access = scope.ServiceProvider.GetRequiredService<FolderAccessService>();

        var folder = await folderRepo.GetByPathAsync(path);
        await aclRepo.AddAsync(new FolderAclEntry
        {
            UserId = userId,
            FolderId = folder!.Id,
            Effect = AclEffect.Allow,
            CreatedAt = DateTime.UtcNow
        });
        access.InvalidateCache(userId);
    }

    private async Task RevokeFolderAsync(int userId, string path)
    {
        using var scope = _factory.Services.CreateScope();
        var folderRepo = scope.ServiceProvider.GetRequiredService<IFolderRepository>();
        var aclRepo = scope.ServiceProvider.GetRequiredService<IFolderAclRepository>();
        var access = scope.ServiceProvider.GetRequiredService<FolderAccessService>();

        var folder = await folderRepo.GetByPathAsync(path);
        await aclRepo.RemoveByUserAndFolderAsync(userId, folder!.Id);
        access.InvalidateCache(userId);
    }

    /// <summary>Creates a plain user whose ACL is a single Allow entry — i.e. they see that subtree and nothing else.</summary>
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
        folder.Should().NotBeNull($"the article created under {allowedPath} should have created the folder");
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
}
