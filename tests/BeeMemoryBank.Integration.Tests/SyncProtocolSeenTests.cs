using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Plan 3.1: every node keeps, per whitelist row, the sync protocol that peer last declared. The PC
/// reads it before adding a blind node — a peer still on protocol 2 would accept the blind node's
/// events and seal the master DEK for it.
/// </summary>
public class SyncProtocolSeenTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeNodeAsync(password: "syncProtocolSeenPw");

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Authenticate_RecordsTheDeclaredProtocol_OnThePeersRow()
    {
        var (peerId, _, signer) = await WhitelistPeerAsync();
        var self = await _factory.Services.GetRequiredService<INodeIdentityRepository>().GetAsync();
        using var http = _factory.Server.CreateClient();

        await PeerAuthenticator.AuthenticateAsync(
            signer, http, http.BaseAddress!.ToString(), PeerIdentity(peerId), self!.NodeId);

        var row = await Whitelist.GetByNodeIdAsync(peerId);
        row!.LastProtocolVersion.Should().Be(SyncProtocolVersion.Current);
        row.LastProtocolSeenAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// The value steers a security pre-flight, so only a caller that proved it holds the peer's key
    /// may write it — otherwise anyone could mark an old peer as upgraded.
    /// </summary>
    [Fact]
    public async Task Authenticate_WithABadSignature_RecordsNothing()
    {
        var (peerId, _, _) = await WhitelistPeerAsync();
        using var http = _factory.Server.CreateClient();
        var challenge = await (await http.PostAsync("/api/sync/challenge", null))
            .Content.ReadFromJsonAsync<ChallengeDto>();

        var resp = await http.PostAsJsonAsync("/api/sync/authenticate", new
        {
            NodeId = peerId,
            ChallengeB64 = challenge!.Challenge,
            SignatureB64 = Convert.ToBase64String(new byte[64]),
            ProtocolVersion = 3
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Whitelist.GetByNodeIdAsync(peerId))!.LastProtocolVersion.Should().BeNull();
    }

    [Fact]
    public async Task ReportPosition_RecordsTheDeclaredProtocol()
    {
        var (peerId, _, _) = await WhitelistPeerAsync();
        var token = _factory.Services.GetRequiredService<SyncTokenStore>().IssueToken(peerId, BeeMemoryBank.Sync.SyncProtocolVersion.Current);
        using var http = _factory.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);

        (await http.PostAsync("/api/sync/report-position?sequence=0&protocolVersion=2", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await Whitelist.GetByNodeIdAsync(peerId))!.LastProtocolVersion.Should().Be(2);
    }

    private IWhitelistRepository Whitelist => _factory.Services.GetRequiredService<IWhitelistRepository>();

    private async Task<(Guid PeerId, byte[] PublicKey, INodeAuthSigner Signer)> WhitelistPeerAsync()
    {
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        var peerId = Guid.NewGuid();
        await Whitelist.CreateAsync(new WhitelistEntry
        {
            NodeId = peerId,
            DisplayName = "peer",
            Ed25519PublicKey = pub,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LamportTs = 1,
            SourceNodeId = peerId,
        });
        return (peerId, pub, new RawKeySigner(priv));
    }

    private static NodeIdentity PeerIdentity(Guid peerId) => new() { NodeId = peerId };

    private sealed class RawKeySigner(byte[] privateKey) : INodeAuthSigner
    {
        public byte[] SignChallenge(NodeIdentity identity, byte[] challengePayload) =>
            Ed25519Signer.Sign(privateKey, challengePayload);
    }

    private sealed record ChallengeDto(string Challenge, Guid ServerNodeId);
}
