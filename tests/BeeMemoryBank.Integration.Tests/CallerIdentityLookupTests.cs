using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The API is the authority on who the caller is (release 2.12). A request that names a person in <c>X-User-Id</c> is that person as <c>tbl_user</c> says
/// they are RIGHT NOW: the forwarded role header is not read. A machine-local caller that holds the internal key and names nobody keeps stating its role, as before.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class CallerIdentityLookupTests : IAsyncLifetime
{
    private const string AdminPassword = "AdminLookup123";
    private const string BobPassword = "BobLookup12345";

    private readonly BmbWebApplicationFactory _api = new();
    private HttpClient _machine = null!;
    private int _bobId;

    public async Task InitializeAsync()
    {
        _machine = _api.CreateClient();   // internal key + X-User-Role: superadmin, no X-User-Id: what the tray and the CLI are
        await _api.InitializeNodeAsync(displayName: "LookupNode", password: AdminPassword);
        (await _machine.PostAsJsonAsync("/api/session/unlock", new { password = AdminPassword })).EnsureSuccessStatusCode();
        _bobId = await CreateUserAsync("bob", BobPassword, UserRoles.Superadmin);
    }

    public Task DisposeAsync()
    {
        _machine.Dispose();
        ((IDisposable)_api).Dispose();
        return Task.CompletedTask;
    }

    private async Task<int> CreateUserAsync(string name, string password, string role)
    {
        var created = await _machine.PostAsJsonAsync("/api/users", new { username = name, password, displayName = name, role, passwordHint = "the colour of the sea" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    /// <summary>The Web layer's request: the internal key, a person, and the role the (possibly old) cookie carries.</summary>
    private HttpClient AsPerson(int userId, string roleInCookie = "superadmin", bool withRoleHeader = true)
    {
        var client = _api.CreateClient();
        client.DefaultRequestHeaders.Remove("X-User-Role");
        if (withRoleHeader) client.DefaultRequestHeaders.Add("X-User-Role", roleInCookie);
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString());
        return client;
    }

    private async Task<HttpStatusCode> SuperadminRouteAsync(HttpClient client) => (await client.GetAsync("/api/users")).StatusCode;

    [Fact]
    public async Task A_role_header_does_not_make_an_ordinary_user_a_superadmin()
    {
        var carol = await CreateUserAsync("carol", "CarolLookup123", UserRoles.User);
        using var client = AsPerson(carol);   // the cookie says superadmin, the table says user

        (await SuperadminRouteAsync(client)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_superadmin_the_table_knows_is_served_whatever_the_header_says_or_omits()
    {
        using var withHeader = AsPerson(_bobId);
        using var withoutHeader = AsPerson(_bobId, withRoleHeader: false);
        using var lyingHeader = AsPerson(_bobId, roleInCookie: "user");

        (await SuperadminRouteAsync(withHeader)).Should().Be(HttpStatusCode.OK);
        (await SuperadminRouteAsync(withoutHeader)).Should().Be(HttpStatusCode.OK, "the role is read from the table, not from a header");
        (await SuperadminRouteAsync(lyingHeader)).Should().Be(HttpStatusCode.OK, "and a header that says less changes nothing either");
    }

    [Fact]
    public async Task A_demoted_superadmin_is_refused_by_the_very_next_call()
    {
        using var bob = AsPerson(_bobId);
        (await SuperadminRouteAsync(bob)).Should().Be(HttpStatusCode.OK, "this also puts the answer in the cache the demotion has to defeat");

        // The demotion primitive itself (since 2.12 the API reaches it only through the removal process, whose lock-out is the same kind of change).
        using (var scope = _api.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Services.UserService>().DemoteSuperadminAsync(_bobId, "user", allowOpenRole: true);

        (await SuperadminRouteAsync(bob)).Should().Be(HttpStatusCode.Forbidden, "not after a cache time-to-live: the change itself dropped the answer");
    }

    [Fact]
    public async Task A_deleted_superadmin_is_refused_by_the_very_next_call()
    {
        using var bob = AsPerson(_bobId);
        (await SuperadminRouteAsync(bob)).Should().Be(HttpStatusCode.OK);

        using (var scope = _api.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Services.UserService>().DeleteUserAsync(_bobId);

        (await SuperadminRouteAsync(bob)).Should().Be(HttpStatusCode.Forbidden);
        // Not authenticated at all: no person is left to scope folders for, so the identity-bound routes refuse too.
        (await bob.GetAsync("/api/agents")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_forwarded_id_that_names_nobody_is_not_a_superadmin()
    {
        using var ghost = AsPerson(987654);

        (await SuperadminRouteAsync(ghost)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Two_user_id_headers_are_refused_not_guessed_at()
    {
        Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", BmbWebApplicationFactory.InternalKeyForTests);
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton<IVaultGate>(new FakeGate(false)).BuildServiceProvider() };
        ctx.Request.Headers["X-Internal-Key"] = BmbWebApplicationFactory.InternalKeyForTests;
        ctx.Request.Headers["X-User-Id"] = new Microsoft.Extensions.Primitives.StringValues(["1", "2"]);
        ctx.Request.Headers["X-User-Role"] = "superadmin";

        await new CallerIdentityMiddleware(_ => Task.CompletedTask).InvokeAsync(ctx);

        // A malformed request carries no rights and names nobody.
        CallerIdentity.Extract(ctx).Should().Be(new CallerIdentity(null, null, null, false));
    }

    [Fact]
    public async Task A_machine_local_caller_without_a_user_id_still_states_its_role()
    {
        // The tray's lock and alarms, NodeFrontClient, the CLI and the blind console: the internal key, a role, nobody named.
        (await SuperadminRouteAsync(_machine)).Should().Be(HttpStatusCode.OK);

        using var asUser = _api.CreateClient();
        asUser.DefaultRequestHeaders.Remove("X-User-Role");
        asUser.DefaultRequestHeaders.Add("X-User-Role", "user");
        (await SuperadminRouteAsync(asUser)).Should().Be(HttpStatusCode.Forbidden);

        (await _machine.PostAsync("/api/session/lock", null)).StatusCode.Should().Be(HttpStatusCode.OK, "the tray's lock is a machine-local call that names nobody");
    }

    [Fact]
    public async Task Without_the_internal_key_no_header_counts_at_all()
    {
        using var stranger = _api.CreateDefaultClient();
        stranger.DefaultRequestHeaders.Add("X-User-Role", "superadmin");
        stranger.DefaultRequestHeaders.Add("X-User-Id", _bobId.ToString());

        (await SuperadminRouteAsync(stranger)).Should().NotBe(HttpStatusCode.OK);
    }

    // ---------------------------------------------------------------- the middleware, and a closed sealed database

    /// <summary>A gate that is sealed and closed (or open): the only thing the middleware asks of it.</summary>
    private sealed class FakeGate(bool sealedAndClosed) : IVaultGate
    {
        public VaultState State => sealedAndClosed ? VaultState.Closed : VaultState.Plain;
        public bool IsSealed => sealedAndClosed;
        public bool IsUsable => !sealedAndClosed;
        public bool IsHeld => false;
        public Task<VaultOpenResult> OpenAsync(byte[] masterDek, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IAsyncDisposable> OpenTemporarilyAsync(byte[] masterDek, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CompleteOpenAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IAsyncDisposable> HoldClosedAsync(string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CloseAsync(string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task WaitUsableAsync(CancellationToken ct) => Task.CompletedTask;
        public event Action<VaultState>? StateChanged { add { } remove { } }
    }

    private static async Task<CallerIdentity> IdentityThroughMiddlewareAsync(
        bool databaseClosed, string method, string path, string? userId, string? role, IUserRepository? users = null)
    {
        Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", BmbWebApplicationFactory.InternalKeyForTests);
        var services = new ServiceCollection().AddSingleton<IVaultGate>(new FakeGate(databaseClosed));
        if (users is not null) services.AddSingleton(users);
        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.Headers["X-Internal-Key"] = BmbWebApplicationFactory.InternalKeyForTests;
        if (userId is not null) ctx.Request.Headers["X-User-Id"] = userId;
        if (role is not null) ctx.Request.Headers["X-User-Role"] = role;

        await new CallerIdentityMiddleware(_ => Task.CompletedTask).InvokeAsync(ctx);
        return CallerIdentity.Extract(ctx);
    }

    [Fact]
    public async Task While_a_sealed_database_is_closed_nobody_named_by_a_user_id_is_a_superadmin()
    {
        // The role cannot be looked up, so it is not taken from the header either: a closed node answers 503 to everything else anyway.
        var lockRoute = await IdentityThroughMiddlewareAsync(true, "POST", "/api/session/lock", "7", "superadmin");
        lockRoute.IsSuperadmin.Should().BeFalse();
        lockRoute.UserId.Should().Be(7, "the person is still named");

        var cancel = await IdentityThroughMiddlewareAsync(true, "POST", "/api/snapshots/restore/cancel", "7", "superadmin");
        cancel.IsSuperadmin.Should().BeFalse();
    }

    [Fact]
    public async Task While_a_sealed_database_is_closed_the_one_superadmin_route_it_still_serves_keeps_working_as_today()
    {
        var poll = await IdentityThroughMiddlewareAsync(true, "GET", "/api/vault/seal/progress", "7", "superadmin");
        poll.IsSuperadmin.Should().BeTrue("the progress poll of the migration that closes the file is the one superadmin route a closed node serves");

        // ... for that method and that path only.
        (await IdentityThroughMiddlewareAsync(true, "POST", "/api/vault/seal/progress", "7", "superadmin")).IsSuperadmin.Should().BeFalse();
        (await IdentityThroughMiddlewareAsync(true, "GET", "/api/vault/seal/preflight", "7", "superadmin")).IsSuperadmin.Should().BeFalse();
    }

    [Fact]
    public async Task While_a_sealed_database_is_closed_a_machine_local_caller_is_exactly_what_it_was()
    {
        // The tray's lock and alarms hold the internal key and name nobody: the header is honoured, closed or not.
        (await IdentityThroughMiddlewareAsync(true, "POST", "/api/session/lock", null, "superadmin")).IsSuperadmin.Should().BeTrue();
        (await IdentityThroughMiddlewareAsync(true, "POST", "/api/snapshots/restore/cancel", null, "superadmin")).IsSuperadmin.Should().BeTrue();
        (await IdentityThroughMiddlewareAsync(true, "POST", "/api/session/lock", null, "user")).IsSuperadmin.Should().BeFalse();
    }

    [Fact]
    public async Task A_closed_sealed_node_still_answers_the_lock_and_the_restore_cancel_as_it_always_did()
    {
        // Through the real pipeline, for a machine-local caller: a closed node answers 503 vault_sealed to everything outside its short allow-list, and F1 did not change that.
        await using var sealedNode = new BmbWebApplicationFactory();
        using var machine = sealedNode.CreateClient();
        await sealedNode.InitializeEncryptedNodeAsync(displayName: "ClosedNode", password: AdminPassword);
        sealedNode.Services.GetRequiredService<SessionService>().Lock();
        var gate = sealedNode.Services.GetRequiredService<IVaultGate>();
        for (var i = 0; i < 100 && gate.IsUsable; i++) await Task.Delay(100);   // the lock closes the file in the background
        gate.IsUsable.Should().BeFalse("the lock closed the encrypted database");

        var locked = await machine.PostAsync("/api/session/lock", null);
        var cancelled = await machine.PostAsync($"/api/snapshots/restore/cancel?eventId={Guid.NewGuid()}", null);

        // Not 401/403: the caller was not refused for who it is. It met the closed database, which is 503 for everyone, exactly as before.
        locked.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        cancelled.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await machine.GetAsync("/api/session/status")).StatusCode.Should().Be(HttpStatusCode.OK, "the allow-list still answers");
    }

    [Fact]
    public async Task The_middleware_leaves_an_agents_identity_alone()
    {
        Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", BmbWebApplicationFactory.InternalKeyForTests);
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton<IVaultGate>(new FakeGate(false)).BuildServiceProvider() };
        ctx.Request.Headers["X-Internal-Key"] = BmbWebApplicationFactory.InternalKeyForTests;
        ctx.Request.Headers["X-User-Id"] = "5";
        ctx.Items[CallerIdentity.ItemKey] = new CallerIdentity(9, 3, "helper", false);

        await new CallerIdentityMiddleware(_ => Task.CompletedTask).InvokeAsync(ctx);

        CallerIdentity.Extract(ctx).Should().Be(new CallerIdentity(9, 3, "helper", false));
    }

    [Fact]
    public async Task The_cache_never_keeps_a_value_that_was_read_before_a_change_and_stored_after_it()
    {
        // Generation guard of CallerStateCache: a read that was already under way when the change invalidated the user is not stored.
        var users = new GatedUserRepository { Role = UserRoles.Superadmin };
        var read = CallerStateCache.GetRoleAsync("race-db", 41, users).AsTask();
        await users.Reading.Task;                       // the repository has been asked and has not answered yet
        CallerStateCache.Invalidate(41);                 // the change commits and drops what it knows
        users.Role = UserRoles.User;
        users.Release.SetResult();
        (await read).Should().Be(UserRoles.Superadmin, "the read that started before the change still returns what it read");

        // ... but it did not poison the cache: the next question goes to the table.
        (await CallerStateCache.GetRoleAsync("race-db", 41, users)).Should().Be(UserRoles.User);
    }

    private sealed class GatedUserRepository : IUserRepository
    {
        public string Role { get; set; } = UserRoles.User;
        public TaskCompletionSource Reading { get; } = new();
        public TaskCompletionSource Release { get; } = new();
        private int _calls;

        public async Task<User?> GetByIdAsync(int id)
        {
            var role = Role;
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Reading.SetResult();
                await Release.Task;
            }
            return new User { Id = id, Role = role, IsActive = true };
        }

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
