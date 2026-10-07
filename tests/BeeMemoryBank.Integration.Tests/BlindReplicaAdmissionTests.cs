using System.Net;
using System.Net.Http.Headers;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A hub answers the internet (ADR 0007): with a valid blind peer's token the package is still bandwidth, so each peer may start only
/// so many downloads per window. The check comes after the token and the blind mark, so a caller without either learns nothing
/// about the limit, and one peer's use never costs another peer its turn.
/// </summary>
public sealed class BlindReplicaAdmissionTests : IDisposable
{
    private readonly BlindNodeFactory _node = new();

    public void Dispose() => _node.Dispose();

    private async Task<(Guid Id, string Token)> BlindPeerAsync(string name)
    {
        var id = BlindNodeId.NewId();
        await _node.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = name, Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey, Status = WhitelistStatuses.Active,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        return (id, _node.Services.GetRequiredService<SyncTokenStore>().IssueToken(id, SyncProtocolVersion.Current));
    }

    private async Task<HttpResponseMessage> ReplicaAsync(string? token)
    {
        using var http = _node.Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/blind/replica");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    [Fact]
    public void ThePeerIsAdmitted_UpToTheLimit_AndThenRefused_WithoutTheRefusalsCounting()
    {
        var cache = _node.Services.GetRequiredService<BlindReplicaPackageCache>();
        var peer = Guid.NewGuid();

        for (var i = 0; i < BlindReplicaPackageCache.AdmissionsPerWindow; i++)
            cache.TryAdmit(peer).Should().BeTrue($"request {i + 1} is within the limit");
        cache.TryAdmit(peer).Should().BeFalse();
        cache.TryAdmit(peer).Should().BeFalse();
        cache.TryAdmit(Guid.NewGuid()).Should().BeTrue("another peer has its own budget");
        BlindReplicaPackageCache.AdmissionsPerWindow.Should().BeGreaterThanOrEqualTo(10,
            "a first load with several resumes of a flaky connection must fit");
    }

    [Fact]
    public async Task APeerThatUsedItsTurns_GetsA429WithRetryAfter_AndAnotherPeerStillGetsThePackage()
    {
        var greedy = await BlindPeerAsync("greedy");
        var other = await BlindPeerAsync("other");
        var cache = _node.Services.GetRequiredService<BlindReplicaPackageCache>();
        for (var i = 0; i < BlindReplicaPackageCache.AdmissionsPerWindow; i++) cache.TryAdmit(greedy.Id);

        using var refused = await ReplicaAsync(greedy.Token);
        using var served = await ReplicaAsync(other.Token);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.RetryAfter.Should().NotBeNull();
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        served.Headers.GetValues("X-BMB-Package-Sha256").Should().ContainSingle();
    }

    [Fact]
    public async Task TheLimitIsCheckedAfterTheTokenAndTheBlindMark_SoACallerWithoutThemLearnsNothingAboutIt()
    {
        var full = Guid.NewGuid();
        await _node.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = full, DisplayName = "full", Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey, Status = WhitelistStatuses.Active,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        var fullToken = _node.Services.GetRequiredService<SyncTokenStore>().IssueToken(full, SyncProtocolVersion.Current);
        var cache = _node.Services.GetRequiredService<BlindReplicaPackageCache>();
        for (var i = 0; i < BlindReplicaPackageCache.AdmissionsPerWindow; i++) cache.TryAdmit(full);

        using var noToken = await ReplicaAsync(null);
        using var badToken = await ReplicaAsync("not a token");
        using var notBlind = await ReplicaAsync(fullToken);

        noToken.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        badToken.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        notBlind.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a full node is not told about a limit it is not served under");
    }

    [Fact]
    public async Task ARevokedBlindCopy_LosesItsTokenAtOnce()
    {
        var peer = await BlindPeerAsync("lost phone");
        using (var before = await ReplicaAsync(peer.Token)) before.StatusCode.Should().Be(HttpStatusCode.OK);

        await _node.Services.GetRequiredService<IWhitelistRepository>().RevokeAsync(peer.Id, new RowVersion(long.MaxValue / 2, Guid.NewGuid()));

        using var after = await ReplicaAsync(peer.Token);
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "revoking the copy in Admin is what ends a lost phone's access");
    }
}
