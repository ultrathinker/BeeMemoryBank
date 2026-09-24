using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A sync bearer token must stop working the moment its peer leaves the whitelist, not an hour
/// later when the token itself expires.
/// </summary>
public class SyncTokenRevocationTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeNodeAsync(password: "syncTokenRevocationPw");

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RevokedPeer_TokenIsRejected_AndDropped()
    {
        var peerId = Guid.NewGuid();
        var whitelist = _factory.Services.GetRequiredService<IWhitelistRepository>();
        await whitelist.CreateAsync(new WhitelistEntry
        {
            NodeId = peerId,
            DisplayName = "peer",
            Ed25519PublicKey = new byte[32],
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LamportTs = 1,
            SourceNodeId = peerId,
        });

        var store = _factory.Services.GetRequiredService<SyncTokenStore>();
        var token = store.IssueToken(peerId);

        using var peer = _factory.Server.CreateClient();
        peer.DefaultRequestHeaders.Authorization = new("Bearer", token);

        (await peer.GetAsync("/api/sync/events?afterSequence=0&limit=1")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        await whitelist.RevokeAsync(peerId, new RowVersion(2, peerId));

        (await peer.GetAsync("/api/sync/events?afterSequence=0&limit=1")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await peer.PostAsync("/api/sync/blobs/check", JsonContent.Create(new { hashes = Array.Empty<string>() })))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        store.TryValidateToken(token, out _).Should().BeFalse("a token of a revoked peer is dropped, not kept around");
    }

    [Fact]
    public async Task UnknownPeer_TokenIsRejected()
    {
        var store = _factory.Services.GetRequiredService<SyncTokenStore>();
        var token = store.IssueToken(Guid.NewGuid());

        using var peer = _factory.Server.CreateClient();
        peer.DefaultRequestHeaders.Authorization = new("Bearer", token);

        (await peer.GetAsync("/api/sync/events?afterSequence=0&limit=1")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }
}
