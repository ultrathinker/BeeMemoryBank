using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-311 fix round 3 (G6): the long writes that run behind a single-flight lease - a copy of an article or a folder, a restore from the trash, a password change, an
/// admin's password reset - could not stop when the client went away: their services took no token, so the lease was held while work nobody waited for ran on, and
/// nothing said it had been abandoned. Each route now hands its service a token linked to the request and to the application's stopping, and each service looks at it only
/// where stopping is safe (before its first write; between the units of a copy, which rolls back; never in the middle of a key-slot rewrite). The routes are called here
/// the way a client that has already disconnected calls them (the handler runs with <c>RequestAborted</c> already cancelled): nothing may change. The services are stopped at their
/// checkpoints through the test seams, to show where a stop is honoured and where it is not.
/// </summary>
public sealed class CancellableWritesTests : IAsyncLifetime
{
    private const string Password = "cancel-writes-Pw-1";
    private const string UserPassword = "UserPass123";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _factory.InitializeNodeAsync("Cancel node", Password);
        _client = _factory.CreateClient();
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The request of a client that has gone away: the handler runs with <c>RequestAborted</c> already cancelled. A handler that stops answers with the cancellation
    /// (the exception comes back to the caller whole, as the server swallows it for a real disconnect); one that ignores the token answers as if nothing were wrong.
    /// What a route must NOT do either way is write: the callers assert that nothing changed.
    /// </summary>
    private async Task AbortedPostAsync(string path, object body, int? userId = null)
    {
        using var client = _factory.CreateClient();
        if (userId is { } id)
        {
            client.DefaultRequestHeaders.Remove("X-User-Role");
            client.DefaultRequestHeaders.Add("X-User-Role", "user");
            client.DefaultRequestHeaders.Add("X-User-Id", id.ToString());
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Web) };
        request.Headers.Add(BmbWebApplicationFactory.ClientGoneHeader, "1");
        try { (await client.SendAsync(request)).Dispose(); }
        catch (OperationCanceledException) { /* the handler stopped at its token */ }
    }

    private async Task<Guid> CreateArticleAsync(string title, string path)
    {
        var create = await _client.PostAsJsonAsync("/api/articles", new { title, treePath = path, conceptTags = new[] { "t" }, content = "body of " + title });
        create.StatusCode.Should().Be(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        return (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateFolderAsync(string path)
    {
        (await _client.PostAsJsonAsync("/api/folders", new { path })).EnsureSuccessStatusCode();
        return await FolderIdAsync(path);
    }

    private async Task<Guid> FolderIdAsync(string path)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IFolderRepository>().GetByPathAsync(path))!.Id;
    }

    private async Task<int> ActiveFolderCountAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IFolderRepository>().GetAllActiveAsync()).Count();
    }

    private async Task<List<string>> TitlesAsync(string path)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IArticleRepository>().ListAsync(path)).Select(a => a.Title).ToList();
    }

    private async Task<int> RegularUserAsync(string username)
    {
        var resp = await _client.PostAsJsonAsync("/api/users", new { username, password = UserPassword, displayName = username, role = "user" });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<bool> UserPasswordWorksAsync(string username, string password)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserService>().AuthenticateAsync(username, password) is not null;
    }

    private async Task<bool> MasterPasswordWorksAsync(string password)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SessionService>().VerifyMasterPasswordAsync(password);
    }

    // ---- the routes, for a client that has gone away --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_copy_of_an_article_for_a_client_that_has_gone_creates_nothing()
    {
        var id = await CreateArticleAsync("To copy", "/Source");
        await CreateFolderAsync("/Target");

        await AbortedPostAsync($"/api/articles/{id}/copy", new { targetFolderPath = "/Target" });

        (await TitlesAsync("/Target")).Should().BeEmpty("the copy looked at its token before its first write");
        (await _client.PostAsJsonAsync($"/api/articles/{id}/copy", new { targetFolderPath = "/Target" })).EnsureSuccessStatusCode();
        (await TitlesAsync("/Target")).Should().ContainSingle("control: the same call from a client that stays does copy");
    }

    [Fact]
    public async Task A_copy_of_a_folder_for_a_client_that_has_gone_creates_nothing()
    {
        var id = await CreateFolderAsync("/Tree");
        await CreateFolderAsync("/Tree/Sub");
        await CreateArticleAsync("Leaf", "/Tree/Sub");
        var before = await ActiveFolderCountAsync();

        await AbortedPostAsync($"/api/folders/{id}/copy", new { targetParentPath = "/" });

        (await ActiveFolderCountAsync()).Should().Be(before, "no folder of the copy was created");
        (await _client.PostAsJsonAsync($"/api/folders/{id}/copy", new { targetParentPath = "/" })).EnsureSuccessStatusCode();
        (await ActiveFolderCountAsync()).Should().BeGreaterThan(before, "control: the same call from a client that stays does copy");
    }

    [Fact]
    public async Task A_restore_of_an_article_from_the_trash_for_a_client_that_has_gone_restores_nothing()
    {
        var id = await CreateArticleAsync("In the trash", "/Docs");
        (await _client.DeleteAsync($"/api/articles/{id}")).EnsureSuccessStatusCode();

        await AbortedPostAsync($"/api/hard-delete/restore/article/{id}", new { });

        (await TitlesAsync("/")).Should().NotContain(t => t.StartsWith("[RESTORED]"));
        (await _client.PostAsync($"/api/hard-delete/restore/article/{id}", null)).EnsureSuccessStatusCode();
        (await TitlesAsync("/")).Should().Contain("[RESTORED] In the trash", "control: the same call from a client that stays does restore");
    }

    [Fact]
    public async Task A_restore_of_a_folder_from_the_trash_for_a_client_that_has_gone_restores_nothing()
    {
        var id = await CreateFolderAsync("/Gone");
        await CreateFolderAsync("/Gone/Inner");
        (await _client.DeleteAsync("/api/folders?path=/Gone")).EnsureSuccessStatusCode();
        var before = await ActiveFolderCountAsync();

        await AbortedPostAsync($"/api/hard-delete/restore/folder/{id}", new { });

        (await ActiveFolderCountAsync()).Should().Be(before, "no restored folder was created");
        (await _client.PostAsync($"/api/hard-delete/restore/folder/{id}", null)).EnsureSuccessStatusCode();
        (await ActiveFolderCountAsync()).Should().BeGreaterThan(before, "control: the same call from a client that stays does restore");
    }

    [Fact]
    public async Task A_master_password_change_for_a_client_that_has_gone_changes_nothing()
    {
        const string changed = "changed-writes-Pw-2";

        await AbortedPostAsync("/api/keys/change-password", new { oldPassword = Password, newPassword = changed });

        (await MasterPasswordWorksAsync(Password)).Should().BeTrue("the key slot was not touched");
        (await MasterPasswordWorksAsync(changed)).Should().BeFalse();
        (await _client.PostAsJsonAsync("/api/keys/change-password", new { oldPassword = Password, newPassword = changed })).EnsureSuccessStatusCode();
        (await MasterPasswordWorksAsync(changed)).Should().BeTrue("control: the same call from a client that stays does change it");
    }

    [Fact]
    public async Task A_self_service_password_change_for_a_client_that_has_gone_changes_nothing()
    {
        var id = await RegularUserAsync("bob");

        await AbortedPostAsync("/api/users/me/change-password", new { oldPassword = UserPassword, newPassword = "NewUserPass456" }, id);

        (await UserPasswordWorksAsync("bob", UserPassword)).Should().BeTrue();
        (await UserPasswordWorksAsync("bob", "NewUserPass456")).Should().BeFalse();
    }

    [Fact]
    public async Task An_admin_reset_of_a_password_for_a_client_that_has_gone_changes_nothing()
    {
        var id = await RegularUserAsync("carol");

        await AbortedPostAsync($"/api/users/{id}/change-password", new { newPassword = "ResetPass789" });

        (await UserPasswordWorksAsync("carol", UserPassword)).Should().BeTrue("the reset looked at its token before it wrote anything");
        (await UserPasswordWorksAsync("carol", "ResetPass789")).Should().BeFalse();
        (await _client.PostAsJsonAsync($"/api/users/{id}/change-password", new { newPassword = "ResetPass789" })).EnsureSuccessStatusCode();
        (await UserPasswordWorksAsync("carol", "ResetPass789")).Should().BeTrue("control: the same call from a client that stays does reset it");
    }

    // ---- the services, stopped at their checkpoints ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_folder_copy_stopped_after_its_first_write_rolls_back_what_it_created()
    {
        var source = await CreateFolderAsync("/Big");
        await CreateFolderAsync("/Big/A");
        await CreateFolderAsync("/Big/B");
        await CreateArticleAsync("One", "/Big/A");
        await CreateArticleAsync("Two", "/Big/B");
        var before = await ActiveFolderCountAsync();
        using var scope = _factory.Services.CreateScope();
        var copy = scope.ServiceProvider.GetRequiredService<CopyService>();
        using var stop = new CancellationTokenSource();
        var writes = 0;
        copy.AfterWrite = () => { if (++writes == 2) stop.Cancel(); };   // after the root and the first subfolder

        var act = () => copy.CopyFolderAsync(source, "/", stop.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        writes.Should().Be(2, "the copy stopped at the next checkpoint, not a write later");
        (await ActiveFolderCountAsync()).Should().Be(before, "what was created before the stop was rolled back: the vault is as it was");
        (await TitlesAsync("/Big (1)")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_folder_restore_stopped_after_its_root_exists_still_writes_the_whole_subtree()
    {
        var id = await CreateFolderAsync("/Old");
        await CreateFolderAsync("/Old/One");
        await CreateFolderAsync("/Old/Two");
        (await _client.DeleteAsync("/api/folders?path=/Old")).EnsureSuccessStatusCode();
        using var scope = _factory.Services.CreateScope();
        var restore = scope.ServiceProvider.GetRequiredService<RestoreService>();
        using var stop = new CancellationTokenSource();
        restore.AfterRootCreated = stop.Cancel;

        var result = await restore.RestoreFolderAsync(id, stop.Token);

        stop.IsCancellationRequested.Should().BeTrue();
        result.RestoredSubfolderCount.Should().Be(2, "a stop after the first write never leaves half a restored tree");
    }

    [Fact]
    public async Task A_restore_that_is_stopped_before_its_first_write_throws_and_creates_nothing()
    {
        var id = await CreateArticleAsync("Stopped", "/Docs");
        (await _client.DeleteAsync($"/api/articles/{id}")).EnsureSuccessStatusCode();
        using var scope = _factory.Services.CreateScope();
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var act = () => scope.ServiceProvider.GetRequiredService<RestoreService>().RestoreArticleAsync(id, stop.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await TitlesAsync("/")).Should().NotContain(t => t.StartsWith("[RESTORED]"));
    }

    [Fact]
    public async Task A_password_replacement_stopped_after_its_hash_is_made_has_written_nothing()
    {
        var id = await RegularUserAsync("dave");
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        using var stop = new CancellationTokenSource();
        users.AfterHashForTests = stop.Cancel;

        var act = () => users.AdminChangePasswordAsync(id, "ResetPass789", stop.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await UserPasswordWorksAsync("dave", UserPassword)).Should().BeTrue("the new hash lived only in memory");
        (await UserPasswordWorksAsync("dave", "ResetPass789")).Should().BeFalse();
    }

    [Fact]
    public async Task A_master_password_change_stopped_after_the_new_key_is_derived_has_not_touched_the_slot()
    {
        using var scope = _factory.Services.CreateScope();
        var keys = scope.ServiceProvider.GetRequiredService<KeyManagementService>();
        using var stop = new CancellationTokenSource();
        keys.AfterDerivationForTests = stop.Cancel;

        var act = () => keys.ChangePasswordAsync(Password, "changed-writes-Pw-2", stop.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await MasterPasswordWorksAsync(Password)).Should().BeTrue();
        (await MasterPasswordWorksAsync("changed-writes-Pw-2")).Should().BeFalse();
    }
}
