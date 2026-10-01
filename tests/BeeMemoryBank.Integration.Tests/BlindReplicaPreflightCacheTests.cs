using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Review S3-r2 #2: the superadmin pre-flight (it calls every full peer) belongs to building a replica
/// package, not to serving one. A Range resume of a cached package must cost no peer calls, whatever
/// the peers would answer by then.
/// </summary>
public sealed class BlindReplicaPreflightCacheTests : IAsyncLifetime
{
    private const string Password = "replicaPreflightPw1!";

    private readonly CountingPeerHandler _peers = new();
    private readonly BmbWebApplicationFactory _full = new();

    public async Task InitializeAsync()
    {
        _full.RouteOutboundHttpThrough(_peers);
        await _full.InitializeNodeAsync("Hub", Password);
        using var client = _full.CreateClient();
        (await client.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password }))
            .EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _full.Dispose();
        _peers.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ARangeResume_OfACachedReplica_AsksNoPeerAndServesTheSameBytes()
    {
        var whitelist = _full.Services.GetRequiredService<IWhitelistRepository>();
        await whitelist.CreateAsync(NewEntry(Guid.NewGuid(), "full peer", apiAddress: "https://peer.test:5301"));
        var phone = BlindNodeId.NewId();
        await whitelist.CreateAsync(NewEntry(phone, "phone", apiAddress: null));
        var token = _full.Services.GetRequiredService<SyncTokenStore>().IssueToken(phone, SyncProtocolVersion.Current);
        using var http = _full.Server.CreateClient();

        using var first = await GetReplicaAsync(http, token, range: null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var peerCallsAfterBuild = _peers.Calls;
        peerCallsAfterBuild.Should().BeGreaterThan(0, "building the package asks the full peer for the producer's standing");

        using var resume = await GetReplicaAsync(http, token, new RangeHeaderValue(0, 63));

        resume.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        _peers.Calls.Should().Be(peerCallsAfterBuild, "a resume of the cached package must not call the peers again");
        resume.Headers.GetValues("X-BMB-Package-Sha256").Should().Equal(first.Headers.GetValues("X-BMB-Package-Sha256"));
        resume.Headers.GetValues("X-BMB-Snapshot-Signature").Should().Equal(first.Headers.GetValues("X-BMB-Snapshot-Signature"));
    }

    private static Task<HttpResponseMessage> GetReplicaAsync(HttpClient http, string token, RangeHeaderValue? range)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/blind/replica");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Range = range;
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private static WhitelistEntry NewEntry(Guid nodeId, string name, string? apiAddress) => new()
    {
        NodeId = nodeId,
        DisplayName = name,
        Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
        Status = WhitelistStatuses.Active,
        ApiAddress = apiAddress,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    /// <summary>A peer that is never reachable; it only counts how often it was asked.</summary>
    private sealed class CountingPeerHandler : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            throw new HttpRequestException("peer unreachable (test)");
        }
    }
}
