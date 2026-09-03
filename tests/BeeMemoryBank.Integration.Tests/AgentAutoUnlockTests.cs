using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// H6 fix: only an agent owned by a superadmin may carry a wrapped master DEK and auto-unlock a
/// locked vault (see the AUTO-UNLOCK / H6 remarks on <see cref="AgentAuthMiddleware"/>). An
/// ordinary user's agent authenticates identically either way, but must never be able to unlock
/// the vault itself, and must not silently fail in a way that's distinguishable from an invalid
/// key. These tests exercise <see cref="AgentAuthMiddleware.InvokeAsync"/> directly, the same way
/// <see cref="AgentAuthMiddlewareAccessTrackingTests"/> does.
/// </summary>
public class AgentAutoUnlockTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private SessionService _session = null!;
    private AgentRepository _agentRepo = null!;
    private UserRepository _userRepo = null!;
    private RemoteApiTokenRepository _remoteTokenRepo = null!;

    private const string Password = "autoUnlockTestPassword";

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        _factory = DbConnectionFactory.CreateInMemory($"bmb_autounlock_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        var keySlotRepo = new KeySlotRepository(_factory);
        var nodeRepo = new NodeIdentityRepository(_factory);
        _userRepo = new UserRepository(_factory);
        _agentRepo = new AgentRepository(_factory);
        _remoteTokenRepo = new RemoteApiTokenRepository(_factory);

        var initService = new InitializationService(nodeRepo, keySlotRepo, _userRepo, _factory);
        await initService.InitializeAsync("admin", "AutoUnlockTestNode", Password);

        _session = new SessionService(keySlotRepo);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AgentAuthMiddleware CreateMiddleware() => new(
        next: _ => Task.CompletedTask,
        logger: NullLogger<AgentAuthMiddleware>.Instance);

    private static DefaultHttpContext RequestWith(string apiKey)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = $"Bearer {apiKey}";
        return ctx;
    }

    /// <summary>Creates a regular ("user"-role) owner and returns their id.</summary>
    private async Task<int> CreateRegularOwnerAsync(string username = "bob")
    {
        var id = await _userRepo.CreateAsync(new User
        {
            Username = username,
            DisplayName = username,
            PasswordHash = UserService.HashPassword("BobPass1!"),
            Role = UserRoles.User,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        return id;
    }

    /// <summary>An agent with NO wrapped DEK — the shape AgentEndpoints now produces for a
    /// non-superadmin owner, and what migration 014 leaves behind for a pre-existing one.</summary>
    private async Task<string> CreateUnwrappedAgentAsync(int ownerId, string name = "unwrapped-agent")
    {
        var apiKey = AgentKeyHelper.GenerateApiKey();
        await _agentRepo.CreateAsync(new Agent
        {
            Name = name,
            KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey),
            KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            OwnerUserId = ownerId
        });
        return apiKey;
    }

    /// <summary>An agent with a real wrapped DEK — the shape produced for a superadmin owner.
    /// Requires the session to be unlocked at call time (it must already hold the real master DEK).</summary>
    private async Task<string> CreateWrappedAgentAsync(int ownerId, string name = "wrapped-agent")
    {
        var apiKey = AgentKeyHelper.GenerateApiKey();
        var masterDek = _session.GetMasterDek();
        try
        {
            var (ciphertext, iv, salt) = AgentKeyHelper.EncryptDekV1(apiKey, masterDek);
            await _agentRepo.CreateAsync(new Agent
            {
                Name = name,
                KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey),
                KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
                EncryptedDek = ciphertext,
                DekIV = iv,
                Salt = salt,
                KdfVersion = 1,
                Status = "A",
                CreatedAt = DateTime.UtcNow,
                OwnerUserId = ownerId
            });
        }
        finally
        {
            Array.Clear(masterDek);
        }
        return apiKey;
    }

    [Fact]
    public async Task OrdinaryUsersAgent_WhileVaultIsLocked_CannotUnlockIt()
    {
        var bobId = await CreateRegularOwnerAsync();
        var apiKey = await CreateUnwrappedAgentAsync(bobId);
        _session.IsUnlocked.Should().BeFalse("nothing has unlocked the vault yet");

        var ctx = RequestWith(apiKey);
        await CreateMiddleware().InvokeAsync(ctx, _agentRepo, _userRepo, _session, _remoteTokenRepo);

        _session.IsUnlocked.Should().BeFalse(
            "an ordinary user's agent has no wrapped DEK and must not be able to unlock the vault");
    }

    [Fact]
    public async Task OrdinaryUsersAgent_WhileVaultIsLocked_StillAuthenticatesNormally()
    {
        // Not unlocking the vault is not the same as failing to authenticate. The agent must
        // still resolve to AuthAgent/CallerIdentity and have its access tracked — exactly like a
        // superadmin's agent — so a caller that doesn't need decrypted content (e.g. metadata-only
        // search) keeps working, and one that does gets the ordinary "locked" error downstream
        // (McpSessionGuardMiddleware), not a bogus "your key is invalid".
        var bobId = await CreateRegularOwnerAsync();
        var apiKey = await CreateUnwrappedAgentAsync(bobId);

        var ctx = RequestWith(apiKey);
        await CreateMiddleware().InvokeAsync(ctx, _agentRepo, _userRepo, _session, _remoteTokenRepo);

        ctx.Items["AuthAgent"].Should().NotBeNull();
        var identity = ctx.Items["CallerIdentity"].Should().BeOfType<CallerIdentity>().Subject;
        identity.UserId.Should().Be(bobId);
        identity.IsSuperadmin.Should().BeFalse();
    }

    [Fact]
    public async Task OrdinaryUsersAgent_WhileVaultIsAlreadyUnlocked_WorksCompletelyNormally()
    {
        // The vault being unlocked is process-wide (SessionService.IsUnlocked), not per-caller —
        // so an ordinary user's agent must work exactly as well as anyone else's the moment
        // someone else has already unlocked the node.
        var bobId = await CreateRegularOwnerAsync();
        var apiKey = await CreateUnwrappedAgentAsync(bobId);
        await _session.UnlockAsync(Password);

        var ctx = RequestWith(apiKey);
        await CreateMiddleware().InvokeAsync(ctx, _agentRepo, _userRepo, _session, _remoteTokenRepo);

        _session.IsUnlocked.Should().BeTrue();
        var identity = ctx.Items["CallerIdentity"].Should().BeOfType<CallerIdentity>().Subject;
        identity.UserId.Should().Be(bobId);
        identity.IsSuperadmin.Should().BeFalse();

        var agentRow = (await _agentRepo.GetByKeyHashAsync(AgentKeyHelper.ComputeKeyHash(apiKey)))!;
        agentRow.RequestCount.Should().Be(1, "access tracking must still run for a non-auto-unlocking agent");
    }

    [Fact]
    public async Task SuperadminsAgent_WhileVaultIsLocked_AutoUnlocksIt()
    {
        // Unlock once to mint a REAL wrapped DEK against the real master DEK, then lock again to
        // simulate the node having restarted (or never been unlocked this process) before the
        // agent's first request arrives.
        await _session.UnlockAsync(Password);
        var admin = (await _userRepo.GetByUsernameAsync("admin"))!;
        var apiKey = await CreateWrappedAgentAsync(admin.Id);
        _session.Lock();
        _session.IsUnlocked.Should().BeFalse("sanity check on the fixture");

        var ctx = RequestWith(apiKey);
        await CreateMiddleware().InvokeAsync(ctx, _agentRepo, _userRepo, _session, _remoteTokenRepo);

        _session.IsUnlocked.Should().BeTrue(
            "a superadmin's agent carries a wrapped DEK and must still be able to auto-unlock a locked vault");
        var identity = ctx.Items["CallerIdentity"].Should().BeOfType<CallerIdentity>().Subject;
        identity.IsSuperadmin.Should().BeTrue();
    }

    [Fact]
    public async Task UnrecognizedAgentKey_LeavesAuthAgentUnset_DistinctFromNoTokenAtAll()
    {
        // AGENTS.md invariant: a bee_-prefixed key that doesn't resolve to a tbl_agent row must
        // leave ctx.Items["AuthAgent"] unset entirely, distinct from "no token presented". This
        // fix must not blur that line while gating auto-unlock.
        var ctx = RequestWith("bee_" + new string('0', 32));

        await CreateMiddleware().InvokeAsync(ctx, _agentRepo, _userRepo, _session, _remoteTokenRepo);

        ctx.Items.ContainsKey("AuthAgent").Should().BeFalse();
        ctx.Items.ContainsKey("CallerIdentity").Should().BeFalse();
        _session.IsUnlocked.Should().BeFalse();
    }
}
