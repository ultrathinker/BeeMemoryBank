using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-42 through the real <c>POST /api/init/join</c>: the joiner records the host as a superadmin,
/// keeps the host's word for every inherited peer, and never records a blind node as a superadmin —
/// even when the host's own row claims it (a blind node must not steer cluster state, plan 3.2).
/// </summary>
public class JoinInheritedAuthorityTests : IAsyncLifetime
{
    private const string MasterPassword = "inheritedAuthorityPassword1";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private BmbWebApplicationFactory _host = null!;
    private HttpClient _hostClient = null!;

    public async Task InitializeAsync()
    {
        _host = new BmbWebApplicationFactory();
        await _host.InitializeNodeAsync("Host", MasterPassword);
        _hostClient = _host.CreateClient();
        (await _hostClient.PostAsJsonAsync("/api/session/unlock", new { Password = MasterPassword }))
            .EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _hostClient.Dispose();
        _host.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task InitJoin_RecordsHostAndInheritedPeers_WithTheirAuthority_ButNeverABlindSuperadmin()
    {
        var superPeer = await AddHostPeerAsync(Guid.NewGuid(), "SuperPeer", isSuperadmin: true);
        var plainPeer = await AddHostPeerAsync(Guid.NewGuid(), "PlainPeer", isSuperadmin: false);
        var blindPeer = await AddHostPeerAsync(BlindNodeId.NewId(), "BlindPeer", isSuperadmin: true);

        using var joiner = new BmbWebApplicationFactory();
        joiner.RouteOutboundHttpThrough(_host.Server.CreateHandler());
        using var joinerClient = joiner.CreateClient();
        var joinResp = await joinerClient.PostAsJsonAsync("/api/init/join", new
        {
            adminUsername = "admin",
            displayName = "Joiner",
            remoteUrl = "http://host",
            password = MasterPassword
        }, JsonOpts);
        joinResp.IsSuccessStatusCode.Should().BeTrue(await joinResp.Content.ReadAsStringAsync());

        Guid hostId;
        using (var hostScope = _host.Services.CreateScope())
            hostId = (await hostScope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;

        using var scope = joiner.Services.CreateScope();
        var rows = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        (await rows.GetByNodeIdAsync(hostId))!.IsSuperadmin.Should().BeTrue("the host proved the master password");
        (await rows.GetByNodeIdAsync(superPeer))!.IsSuperadmin.Should().BeTrue("the host reports it as a superadmin");
        (await rows.GetByNodeIdAsync(plainPeer))!.IsSuperadmin.Should().BeFalse("the host reports it as a plain peer");
        (await rows.GetByNodeIdAsync(blindPeer))!.IsSuperadmin.Should().BeFalse(
            "a blind node is never a superadmin, whatever the host's row says");
    }

    private async Task<Guid> AddHostPeerAsync(Guid nodeId, string name, bool isSuperadmin)
    {
        using var scope = _host.Services.CreateScope();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = nodeId,
            DisplayName = name,
            Ed25519PublicKey = publicKey,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsSuperadmin = isSuperadmin
        });
        return nodeId;
    }
}
