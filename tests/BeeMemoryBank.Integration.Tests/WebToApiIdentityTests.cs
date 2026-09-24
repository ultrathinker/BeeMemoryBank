extern alias WebProject;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Startup;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using WebProject::BeeMemoryBank.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The Web→API identity pipeline: the display name a Web user carries must reach version history
/// and audit attribution even when it is NOT ASCII.
///
/// <para>The display name deliberately does NOT travel in a header — SocketsHttpHandler refuses
/// non-ASCII header values, and the in-memory TestServer (which never serializes headers) cannot
/// see that failure. The E2E test here therefore runs the API on real Kestrel and calls it through
/// a real HttpClient with the real <see cref="InternalKeyHandler"/>, exactly the hop production
/// uses.</para>
/// </summary>
public class WebToApiIdentityTests
{
    /// <summary>Same key the factory-based tests use — BMB_INTERNAL_KEY is process-wide, and a
    /// differing value set here would race parallel factory tests that read it at send time.</summary>
    private const string InternalKey = BmbWebApplicationFactory.InternalKeyForTests;
    private const string Password = "kestrelPassword";
    private const string DisplayName = "\u0418\u0432\u0430\u043D \u041F\u0435\u0442\u0440\u043E\u0432";

    // ───────────────────── real-Kestrel end-to-end ─────────────────────

    [Fact]
    public async Task NonAsciiDisplayName_WebCallsSucceed_AndVersionRecordsDisplayName()
    {
        Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", InternalKey);
        var dataDir = Path.Combine(Path.GetTempPath(), "bmb_kestrel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        // Mirrors Api's Program.cs host construction, except Kestrel binds a random loopback port
        // instead of TestServer, so every request is really written to a socket.
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseSetting("BeeMemoryBank:DataPath", dataDir);
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        // No forwarded-headers wiring: this test sends no X-Forwarded-* and connects from loopback,
        // which RateLimitMiddleware exempts, so RemoteIpAddress as seen by the peer is correct as-is.
        builder.AddBeeApiServices(dataDir);

        var app = builder.Build();
        await app.RunBeeApiStartupTasksAsync(dataDir);
        app.UseBeeApiPipeline();
        app.MapBeeApiEndpoints();
        await app.StartAsync();

        try
        {
            // Initialize the node and give the admin a non-ASCII display name, so the API can only
            // learn it by resolving X-User-Id through IUserRepository.
            int userId;
            using (var scope = app.Services.CreateScope())
            {
                var init = scope.ServiceProvider.GetRequiredService<InitializationService>();
                await init.InitializeAsync("admin", "KestrelTestNode", Password);
                var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                var user = await users.GetByUsernameAsync("admin")
                           ?? throw new InvalidOperationException("admin user missing");
                user.DisplayName = DisplayName;
                await users.UpdateAsync(user);
                userId = user.Id;
            }

            using var client = CreateWebLikeClient(app.Urls.First(u => u.StartsWith("http://127.0.0.1")), userId);

            // Pre-fix, this exact setup failed on the FIRST request: the handler copied the
            // non-ASCII display name into X-User-DisplayName and SocketsHttpHandler rejected it
            // ("Request headers must contain only ASCII characters") before a byte left the process.
            var health = await client.GetAsync("/health");
            health.StatusCode.Should().Be(HttpStatusCode.OK);

            var unlock = await client.PostAsJsonAsync("/api/session/unlock", new { password = Password });
            unlock.EnsureSuccessStatusCode();

            var create = await client.PostAsJsonAsync("/api/articles", new
            {
                title = "Non-ASCII attribution",
                treePath = "/Tests",
                content = "first body"
            });
            create.StatusCode.Should().Be(HttpStatusCode.Created);
            var article = await create.Content.ReadFromJsonAsync<JsonElement>();
            var id = Guid.Parse(article.GetProperty("id").GetString()!);

            // An edit with a body change creates a version snapshot attributed to the caller.
            var update = await client.PutAsJsonAsync($"/api/articles/{id}", new { content = "second body" });
            update.EnsureSuccessStatusCode();

            var versionsResp = await client.GetAsync($"/api/articles/{id}/versions");
            versionsResp.EnsureSuccessStatusCode();
            var versions = await versionsResp.Content.ReadFromJsonAsync<JsonElement>();
            var updatedBy = versions.EnumerateArray().Single()
                .GetProperty("updatedBy").GetString();
            // The node display name may be prefixed ("KestrelTestNode / \u0418\u0432\u0430\u043D \u041F\u0435\u0442\u0440\u043E\u0432"); the caller's
            // own name must be there verbatim.
            updatedBy.Should().Contain(DisplayName);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort, like the factory */ }
        }
    }

    /// <summary>A real HttpClient behind the REAL InternalKeyHandler, holding a principal like the
    /// one a signed-in Web user produces — including the non-ASCII DisplayName claim.</summary>
    private static HttpClient CreateWebLikeClient(string baseUri, int userId)
    {
        var ctx = new DefaultHttpContext();
        var identity = new ClaimsIdentity(authenticationType: "TestWebCookie");
        identity.AddClaim(new Claim("UserId", userId.ToString()));
        identity.AddClaim(new Claim("DisplayName", DisplayName));
        identity.AddClaim(new Claim(ClaimTypes.Role, "superadmin"));
        ctx.User = new ClaimsPrincipal(identity);

        var handler = new InternalKeyHandler(new HttpContextAccessor { HttpContext = ctx })
        {
            InnerHandler = new SocketsHttpHandler()
        };
        return new HttpClient(handler) { BaseAddress = new Uri(baseUri) };
    }

    // ───────────────────── HttpActorProvider, in-process ─────────────────────

    private static async Task<(ServiceProvider Services, DbConnectionFactory Db)> CreateVaultAsync()
    {
        DapperConfig.Configure();
        Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", InternalKey);
        var db = DbConnectionFactory.CreateInMemory($"bmb_actor_{Guid.NewGuid():N}");
        await new MigrationRunner(db).RunMigrationsAsync();
        var userRepo = new UserRepository(db);
        await userRepo.CreateAsync(new User
        {
            Username = "ivan",
            DisplayName = DisplayName,
            Role = UserRoles.Superadmin,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        var services = new ServiceCollection()
            .AddSingleton<IUserRepository>(userRepo)
            .BuildServiceProvider();
        return (services, db);
    }

    private static HttpActorProvider ProviderFor(HttpContext ctx)
        => new(new HttpContextAccessor { HttpContext = ctx });

    private static DefaultHttpContext WebUserContext(ServiceProvider services, bool withInternalKey = true)
    {
        var ctx = new DefaultHttpContext { RequestServices = services };
        if (withInternalKey)
            ctx.Request.Headers["X-Internal-Key"] = InternalKey;
        ctx.Request.Headers["X-User-Id"] = "1";
        return ctx;
    }

    [Fact]
    public async Task ActorName_WebUser_ResolvedFromTrustedUserId_AndCachedPerRequest()
    {
        var (services, db) = await CreateVaultAsync();
        try
        {
            var counting = new CountingUserRepository((UserRepository)services.GetRequiredService<IUserRepository>());
            using var countingServices = new ServiceCollection()
                .AddSingleton<IUserRepository>(counting)
                .BuildServiceProvider();
            var provider = ProviderFor(WebUserContext(countingServices));

            provider.ActorName.Should().Be(DisplayName);
            provider.ActorName.Should().Be(DisplayName);
            // One lookup for the whole request, not one per attribution read.
            counting.GetByIdCalls.Should().Be(1);
        }
        finally
        {
            services.Dispose();
            db.Dispose();
        }
    }

    [Fact]
    public async Task ActorName_UserIdWithoutInternalKey_IsNull()
    {
        var (services, db) = await CreateVaultAsync();
        try
        {
            // X-User-Id alone is not trusted (CallerIdentity only honours it behind the internal
            // key) — a spoofed id must not let a caller take over attribution.
            ProviderFor(WebUserContext(services, withInternalKey: false)).ActorName.Should().BeNull();
        }
        finally
        {
            services.Dispose();
            db.Dispose();
        }
    }

    [Fact]
    public void ActorName_Agent_ReturnsAgentName_Unchanged()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["AuthAgent"] = new Agent { Id = 7, Name = "helper" };
        ProviderFor(ctx).ActorName.Should().Be("helper");
    }

    /// <summary>Counts GetByIdAsync calls so the per-request memoization in ActorName is observable.</summary>
    private sealed class CountingUserRepository(IUserRepository inner) : IUserRepository
    {
        public int GetByIdCalls { get; private set; }

        public Task<User?> GetByIdAsync(int id)
        {
            GetByIdCalls++;
            return inner.GetByIdAsync(id);
        }

        public Task<User?> GetByUsernameAsync(string username) => inner.GetByUsernameAsync(username);
        public Task<List<User>> ListActiveAsync() => inner.ListActiveAsync();
        public Task<int> CreateAsync(User user) => inner.CreateAsync(user);
        public Task UpdateAsync(User user) => inner.UpdateAsync(user);
        public Task DeleteAsync(int id, string releasedUsername) => inner.DeleteAsync(id, releasedUsername);
        public Task UpdateLastLoginAsync(int id) => inner.UpdateLastLoginAsync(id);
        public Task RepointKeySlotAsync(int oldSlotId, int newSlotId) => inner.RepointKeySlotAsync(oldSlotId, newSlotId);
        public Task<bool> TryAssignKeySlotAsync(int userId, int slotId) => inner.TryAssignKeySlotAsync(userId, slotId);
        public Task ClearKeySlotAsync(int slotId) => inner.ClearKeySlotAsync(slotId);
        public Task<List<int>> GetUserIdsByRoleAsync(string role) => inner.GetUserIdsByRoleAsync(role);
        public Task<Dictionary<string, int>> CountActiveUsersPerRoleAsync() => inner.CountActiveUsersPerRoleAsync();
        public Task<string?> GetSecurityStampAsync(int id) => inner.GetSecurityStampAsync(id);
        public Task<string> BumpSecurityStampAsync(int id) => inner.BumpSecurityStampAsync(id);
    }
}
