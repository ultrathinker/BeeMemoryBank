using System.Net.Http.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Plan 4.2 / 3.1: before adding a blind node the PC asks every reachable full peer how it sees the
/// PC (GET /api/sync/my-standing) and refuses — naming the node — while any peer does not see it as
/// superadmin or any full node is still on a protocol that knows nothing of blind nodes.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class BlindPreflightTests : IAsyncLifetime
{
    private const string Password = "blindPreflightPw1!";
    private readonly BmbWebApplicationFactory _pc = new();
    private readonly BmbWebApplicationFactory _hub = new();
    private HttpClient _toHub = null!;

    public async Task InitializeAsync()
    {
        await _pc.InitializeNodeAsync("PC", Password);
        await _hub.InitializeNodeAsync("Hub", Password);
        await _pc.Services.GetRequiredService<SessionService>().UnlockAsync(Password);
        _toHub = _hub.Server.CreateClient();

        var hub = await IdentityAsync(_hub);
        await _pc.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(Row(hub.NodeId, "Hub", hub.Ed25519PublicKey,
            _toHub.BaseAddress!.ToString().TrimEnd('/'), superadmin: true));
    }

    public Task DisposeAsync()
    {
        _toHub.Dispose();
        _pc.Dispose();
        _hub.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HubThatDoesNotSeeThePcAsSuperadmin_Blocks_AndIsNamed()
    {
        await TrustPcOnHubAsync(superadmin: false);

        var ex = await RunAsync().Should().ThrowAsync<BlindPreflightFailedException>();

        ex.Which.Problems.Should().ContainSingle().Which.Should().Contain("\"Hub\"").And.Contain("promote");
    }

    [Fact]
    public async Task SuperadminEverywhere_AndEveryoneOnProtocol3_Passes()
    {
        await TrustPcOnHubAsync(superadmin: true);

        await RunAsync().Should().NotThrowAsync();
    }

    [Fact]
    public async Task AFullNodeLastSeenOnProtocol2_Blocks_AndIsNamed()
    {
        await TrustPcOnHubAsync(superadmin: true);
        var phone = Guid.NewGuid();
        await _pc.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(Row(phone, "Old phone", new byte[32], null, false));
        await _pc.Services.GetRequiredService<IWhitelistRepository>().RecordProtocolVersionAsync(phone, 2, DateTime.UtcNow);

        var ex = await RunAsync().Should().ThrowAsync<BlindPreflightFailedException>();

        ex.Which.Problems.Should().ContainSingle().Which.Should().Contain("\"Old phone\"").And.Contain("protocol 2");
    }

    /// <summary>
    /// A peer's view counts too: the hub saw the phone on protocol 3 even though the PC never talks
    /// to it directly — the phone only syncs with the hub.
    /// </summary>
    [Fact]
    public async Task APhoneOnlyTheHubHasSeen_OnProtocol3_Passes()
    {
        await TrustPcOnHubAsync(superadmin: true);
        var phone = Guid.NewGuid();
        await _pc.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(
            Row(phone, "Phone", new byte[32], null, false, createdAt: DateTime.UtcNow.AddDays(-30)));
        await _hub.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(Row(phone, "Phone", new byte[32], null, false));
        await _hub.Services.GetRequiredService<IWhitelistRepository>().RecordProtocolVersionAsync(phone, 3, DateTime.UtcNow);

        await RunAsync().Should().NotThrowAsync();
    }

    [Fact]
    public async Task AFullNodeNobodyHasHeardFromForDays_Blocks_ButANewOneDoesNot()
    {
        await TrustPcOnHubAsync(superadmin: true);
        var whitelist = _pc.Services.GetRequiredService<IWhitelistRepository>();
        await whitelist.CreateAsync(Row(Guid.NewGuid(), "Just added", new byte[32], null, false));
        await RunAsync().Should().NotThrowAsync("a new device gets a grace period to sync once");

        await whitelist.CreateAsync(Row(Guid.NewGuid(), "Drawer phone", new byte[32], null, false,
            createdAt: DateTime.UtcNow - BlindPreflight.UnknownProtocolGrace - TimeSpan.FromDays(1)));
        var ex = await RunAsync().Should().ThrowAsync<BlindPreflightFailedException>();
        ex.Which.Problems.Should().ContainSingle().Which.Should().Contain("\"Drawer phone\"");
    }

    /// <summary>
    /// Review L-merge #2: whether this node may call itself superadmin in a package is the network's
    /// answer — every full peer that answers must say so, and one must answer.
    /// </summary>
    [Fact]
    public async Task SuperadminInNetwork_OnlyWhenThePeersSaySo()
    {
        await TrustPcOnHubAsync(superadmin: false);
        (await IsSuperadminAsync()).Should().BeFalse("the hub does not see the PC as superadmin");

        var hubWhitelist = _hub.Services.GetRequiredService<IWhitelistRepository>();
        var pcRow = (await hubWhitelist.GetByNodeIdAsync((await IdentityAsync(_pc)).NodeId))!;
        pcRow.IsSuperadmin = true;
        await hubWhitelist.UpdateAsync(pcRow);
        (await IsSuperadminAsync()).Should().BeTrue();
    }

    /// <summary>
    /// Review L-merge round 2 #2: a second full peer that is reachable but refuses the PC (it does not
    /// have the PC as an active peer: 401 in the handshake) is an answer — "no" — not "offline". One
    /// positive peer is then not enough.
    /// </summary>
    [Fact]
    public async Task APeerThatAnswers401_CountsAsNo_EvenBesideAPositiveOne()
    {
        await TrustPcOnHubAsync(superadmin: true);
        using var other = new BmbWebApplicationFactory();
        await other.InitializeNodeAsync("Other", Password);
        var otherId = (await IdentityAsync(other)).NodeId;
        await _pc.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(
            Row(otherId, "Other", (await IdentityAsync(other)).Ed25519PublicKey, "http://other.test", superadmin: true));
        await _pc.Services.GetRequiredService<IWhitelistRepository>().UpdateAsync(
            Row((await IdentityAsync(_hub)).NodeId, "Hub", (await IdentityAsync(_hub)).Ed25519PublicKey, "http://hub.test", superadmin: true));
        using var routed = new HttpClient(new HostRouter(new Dictionary<string, HttpMessageHandler>
        {
            ["hub.test"] = _hub.Server.CreateHandler(),
            ["other.test"] = other.Server.CreateHandler(),
        }));

        using var scope = _pc.Services.CreateScope();
        var preflight = scope.ServiceProvider.GetRequiredService<BlindPreflight>();

        (await preflight.IsSuperadminInNetworkAsync(routed, CancellationToken.None))
            .Should().BeFalse("\"Other\" answered — with a refusal");
    }

    /// <summary>Sends each request to the test server named by its host.</summary>
    private sealed class HostRouter(Dictionary<string, HttpMessageHandler> byHost) : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpMessageInvoker> _invokers =
            byHost.ToDictionary(kv => kv.Key, kv => new HttpMessageInvoker(kv.Value));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            _invokers[request.RequestUri!.Host].SendAsync(request, ct);
    }

    private async Task<bool> IsSuperadminAsync()
    {
        using var scope = _pc.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BlindPreflight>().IsSuperadminInNetworkAsync(_toHub, CancellationToken.None);
    }

    private Func<Task> RunAsync() => async () =>
    {
        using var scope = _pc.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<BlindPreflight>().RunAsync(_toHub, CancellationToken.None);
    };

    private async Task TrustPcOnHubAsync(bool superadmin)
    {
        var pc = await IdentityAsync(_pc);
        await _hub.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(Row(pc.NodeId, "PC", pc.Ed25519PublicKey, null, superadmin));
    }

    private static WhitelistEntry Row(Guid id, string name, byte[] key, string? address, bool superadmin, DateTime? createdAt = null) => new()
    {
        NodeId = id, DisplayName = name, Ed25519PublicKey = key, ApiAddress = address, IsSuperadmin = superadmin,
        Status = "A", CreatedAt = createdAt ?? DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static async Task<NodeIdentity> IdentityAsync(BmbWebApplicationFactory node) =>
        (await node.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
}
