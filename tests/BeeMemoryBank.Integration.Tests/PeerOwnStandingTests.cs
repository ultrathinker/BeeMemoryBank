using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// This node's standing for its own superadmin-only work (anchors, retiring boxes): what its peers answer on
/// GET /api/sync/my-standing, the question the blind pre-flight asks too.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class PeerOwnStandingTests : IAsyncLifetime
{
    private const string Password = "peerStandingPw1!";
    private readonly BmbWebApplicationFactory _pc = new();
    private readonly BmbWebApplicationFactory _hub = new();

    public async Task InitializeAsync()
    {
        await _hub.InitializeNodeAsync("Hub", Password);
        _pc.RouteOutboundHttpThrough(_hub.Server.CreateHandler());
        await _pc.InitializeNodeAsync("PC", Password);
        await _pc.Services.GetRequiredService<SessionService>().UnlockAsync(Password);
    }

    public Task DisposeAsync()
    {
        _pc.Dispose();
        _hub.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task APeerThatSeesThisNodeAsSuperadmin_SaysSo()
    {
        await LinkAsync(pcIsSuperadminOnHub: true);

        (await _pc.Services.GetRequiredService<IOwnStandingProvider>().IsSuperadminAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task APeerThatDoesNot_SaysNo()
    {
        await LinkAsync(pcIsSuperadminOnHub: false);

        (await _pc.Services.GetRequiredService<IOwnStandingProvider>().IsSuperadminAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task NoPeerToAsk_IsNotASuperadmin()
    {
        (await _pc.Services.GetRequiredService<IOwnStandingProvider>().IsSuperadminAsync()).Should().BeFalse();
    }

    private async Task LinkAsync(bool pcIsSuperadminOnHub)
    {
        var hub = (await _hub.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var pc = (await _pc.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        await _pc.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(Row(hub.NodeId, "Hub", hub.Ed25519PublicKey, "http://hub.test", true));
        await _hub.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(Row(pc.NodeId, "PC", pc.Ed25519PublicKey, null, pcIsSuperadminOnHub));
    }

    private static WhitelistEntry Row(Guid id, string name, byte[] key, string? address, bool superadmin) => new()
    {
        NodeId = id, DisplayName = name, Ed25519PublicKey = key, ApiAddress = address, IsSuperadmin = superadmin,
        Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };
}
