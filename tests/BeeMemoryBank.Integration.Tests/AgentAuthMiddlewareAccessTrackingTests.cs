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
/// L1 regression test: <c>AgentAuthMiddleware.InvokeAsync</c> used to fire-and-forget
/// <c>agentRepo.UpdateAccessAsync</c> via <c>.ContinueWith(..., OnlyOnFaulted)</c> instead of
/// awaiting it (the identical bug already found and fixed for remote-token <c>TouchAsync</c> five
/// lines above it). Because <c>agentRepo</c> is request-scoped, the detached task could still be
/// mid-flight when the request's DI scope (and the repo's underlying connection) is disposed at
/// the end of the request, silently dropping the last-accessed-at/request-count update. The fix
/// awaits it directly. This test asserts the tracking write is durably visible immediately after
/// <c>InvokeAsync</c> returns — not "eventually, if the fire-and-forget task happened to win the
/// race" — which is the exact guarantee a detached task can never make.
/// </summary>
public class AgentAuthMiddlewareAccessTrackingTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private SessionService _session = null!;
    private AgentRepository _agentRepo = null!;
    private UserRepository _userRepo = null!;
    private RemoteApiTokenRepository _remoteTokenRepo = null!;
    private int _agentId;
    private string _apiKey = null!;

    private const string Password = "agentAccessTestPassword";

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();

        _factory = DbConnectionFactory.CreateInMemory($"bmb_agent_access_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();

        var keySlotRepo = new KeySlotRepository(_factory);
        var nodeRepo = new NodeIdentityRepository(_factory);
        _userRepo = new UserRepository(_factory);
        _agentRepo = new AgentRepository(_factory);
        _remoteTokenRepo = new RemoteApiTokenRepository(_factory);

        var initService = new InitializationService(nodeRepo, keySlotRepo, _userRepo, _factory);
        await initService.InitializeAsync("admin", "AgentAccessTestNode", Password);

        // Pre-unlock the session (as if a prior request already unlocked it): the middleware's
        // agent-DEK auto-unlock branch is irrelevant to this test, and pre-unlocking means the
        // test agent row's EncryptedDek/DekIV never need to be real ciphertext.
        _session = new SessionService(keySlotRepo);
        await _session.UnlockAsync(Password);

        var owner = (await _userRepo.GetByIdAsync(1))!;
        _apiKey = AgentKeyHelper.GenerateApiKey();
        _agentId = await _agentRepo.CreateAsync(new Agent
        {
            Name = "access-tracking-test-agent",
            KeyPrefix = _apiKey[..12],
            KeyHash = AgentKeyHelper.ComputeKeyHash(_apiKey),
            EncryptedDek = [],
            DekIV = [],
            KdfVersion = 1,
            Salt = [],
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            OwnerUserId = owner.Id
        });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task InvokeAsync_UpdatesAgentAccessTracking_SynchronouslyBeforeReturning()
    {
        var before = (await _agentRepo.GetByIdAsync(_agentId))!;
        before.RequestCount.Should().Be(0);
        before.LastAccessedAt.Should().BeNull();

        var middleware = new AgentAuthMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<AgentAuthMiddleware>.Instance);

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = $"Bearer {_apiKey}";

        await middleware.InvokeAsync(ctx, _agentRepo, _userRepo, _session, _remoteTokenRepo);

        // No delay, no retry loop, no Task.Yield -- if the update were still a detached
        // fire-and-forget task, this read would frequently observe the pre-update row.
        var after = (await _agentRepo.GetByIdAsync(_agentId))!;
        after.RequestCount.Should().Be(1, "UpdateAccessAsync must be awaited, not fire-and-forget");
        after.LastAccessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task InvokeAsync_CalledTwice_IncrementsRequestCountEachTime()
    {
        var middleware = new AgentAuthMiddleware(
            next: _ => Task.CompletedTask,
            logger: NullLogger<AgentAuthMiddleware>.Instance);

        for (var i = 1; i <= 2; i++)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.Authorization = $"Bearer {_apiKey}";
            await middleware.InvokeAsync(ctx, _agentRepo, _userRepo, _session, _remoteTokenRepo);

            var row = (await _agentRepo.GetByIdAsync(_agentId))!;
            row.RequestCount.Should().Be(i);
        }
    }
}
