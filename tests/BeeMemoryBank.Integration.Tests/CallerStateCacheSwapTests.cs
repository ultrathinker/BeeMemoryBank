using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// <see cref="CallerStateCache"/> is keyed by the database's path, and a restore or a reset replaces the users table under an unchanged path while
/// user ids restart. What the cache holds about an id then belongs to a person who is no longer there: the next call has to be answered from the NEW table.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class CallerStateCacheSwapTests : IAsyncLifetime
{
    private const string Password = "swapCachePassword";

    private readonly BmbWebApplicationFactory _node = new();
    private HttpClient _machine = null!;
    private int _bobId;

    public async Task InitializeAsync()
    {
        _machine = _node.CreateClient();   // the internal key and a role, no X-User-Id: what the tray and the CLI are
        await _node.InitializeEncryptedNodeAsync(displayName: "SwapNode", password: Password);
        (await _machine.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();

        using var scope = _node.Services.CreateScope();
        _bobId = await scope.ServiceProvider.GetRequiredService<IUserRepository>().CreateAsync(new User
        {
            Username = "bob", DisplayName = "Bob", Role = UserRoles.User, IsActive = true, CreatedAt = DateTime.UtcNow,
        });
    }

    public Task DisposeAsync()
    {
        _machine.Dispose();
        _node.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient AsBob()
    {
        var client = _node.CreateClient();
        client.DefaultRequestHeaders.Remove("X-User-Role");
        client.DefaultRequestHeaders.Add("X-User-Role", "superadmin");   // what a cookie issued to a superadmin carries
        client.DefaultRequestHeaders.Add("X-User-Id", _bobId.ToString());
        return client;
    }

    private async Task SetBobsRoleAsync(string role)
    {
        using var scope = _node.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync("UPDATE tbl_user SET role = @role WHERE id = @id", new { role, id = _bobId });
        CallerStateCache.Invalidate(_bobId);   // the test's own change, so what the cache holds next is what the TEST warms
    }

    [Fact]
    public async Task After_a_restore_an_id_that_was_a_superadmin_and_is_an_ordinary_user_is_not_a_superadmin_on_the_very_next_call()
    {
        // The snapshot is taken while bob is an ordinary user ...
        var created = await _machine.PostAsync("/api/snapshots", null);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var fileName = (await created.Content.ReadFromJsonAsync<SnapshotCreated>())!.FileName;

        // ... and bob becomes a superadmin afterwards, so the cache learns it.
        await SetBobsRoleAsync(UserRoles.Superadmin);
        using var bob = AsBob();
        (await bob.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.OK, "bob is a superadmin now, and the cache has been told");

        var restored = await _machine.PostAsJsonAsync("/api/snapshots/restore", new { fileName, masterPassword = Password, createBackupFirst = false, standaloneMode = true });
        restored.StatusCode.Should().Be(HttpStatusCode.OK, await restored.Content.ReadAsStringAsync());
        (await _machine.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();

        (await bob.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the restored table says user; the restore dropped what the cache knew, so the answer is not the old table's");
    }

    [Fact]
    public async Task After_a_node_reset_the_cache_holds_nothing_about_the_wiped_users()
    {
        var databaseId = _node.Services.GetRequiredService<IDbConnectionFactory>().DatabaseId;
        (await CallerStateCache.GetRoleAsync(databaseId, _bobId, new FixedRole(UserRoles.Superadmin))).Should().Be(UserRoles.Superadmin);

        var reset = await _machine.PostAsJsonAsync("/api/init/reset", new { masterPassword = Password });
        reset.StatusCode.Should().Be(HttpStatusCode.OK, await reset.Content.ReadAsStringAsync());

        (await CallerStateCache.GetRoleAsync(databaseId, _bobId, new FixedRole(UserRoles.User))).Should().Be(UserRoles.User,
            "the reset emptied the users table; an id that is handed out again must not inherit the wiped person's role");
    }

    private sealed record SnapshotCreated(string FileName);

    /// <summary>Answers <see cref="IUserRepository.GetByIdAsync"/> with one role for every id; the rest is not used by the cache.</summary>
    private sealed class FixedRole(string role) : IUserRepository
    {
        public Task<User?> GetByIdAsync(int id) => Task.FromResult<User?>(new User { Id = id, Role = role, IsActive = true });

        public Task<User?> GetByUsernameAsync(string username) => throw new NotSupportedException();
        public Task<List<User>> ListActiveAsync() => throw new NotSupportedException();
        public Task<int> CreateAsync(User user) => throw new NotSupportedException();
        public Task UpdateAsync(User user) => throw new NotSupportedException();
        public Task DeleteAsync(int id, string releasedUsername) => throw new NotSupportedException();
        public Task UpdateLastLoginAsync(int id) => throw new NotSupportedException();
        public Task RepointKeySlotAsync(int oldSlotId, int newSlotId) => throw new NotSupportedException();
        public Task<bool> TryAssignKeySlotAsync(int userId, int slotId) => throw new NotSupportedException();
        public Task<bool> TryReplacePasswordAsync(int userId, string expectedRole, int? expectedKeySlotId, int? newKeySlotId, string passwordHash, bool setHint, string? passwordHint) => throw new NotSupportedException();
        public Task ClearKeySlotAsync(int slotId) => throw new NotSupportedException();
        public Task<List<int>> GetUserIdsByRoleAsync(string role) => throw new NotSupportedException();
        public Task<Dictionary<string, int>> CountActiveUsersPerRoleAsync() => throw new NotSupportedException();
        public Task<string?> GetSecurityStampAsync(int id) => throw new NotSupportedException();
        public Task<string?> GetPasswordHintAsync(string username) => throw new NotSupportedException();
        public Task SetPasswordHintAsync(int id, string? hint) => throw new NotSupportedException();
        public Task<string> BumpSecurityStampAsync(int id) => throw new NotSupportedException();
    }
}
