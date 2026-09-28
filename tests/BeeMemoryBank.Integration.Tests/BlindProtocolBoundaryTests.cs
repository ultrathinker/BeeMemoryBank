using System.Net;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Review L-stage0 round 2 #3: the blind node's own sync surface — seed upload, seed progress,
/// replica — is closed to a token bound to protocol 2 like the rest (SyncProtocolBoundaryTests).
/// The first seed arrives before any token exists: it is authorized by the pairing secret alone,
/// out of band of protocol negotiation, and tested on its own below.
/// </summary>
public class BlindProtocolBoundaryTests : IDisposable
{
    private readonly BlindNodeFactory _blind = new();

    public void Dispose() => _blind.Dispose();

    [Theory]
    [InlineData("POST", "/api/blind/seed?seedId=6f1c7a52-8d41-4a5e-9d0b-6a3cf0f1a001&offset=0&total=4&sha256=00")]
    [InlineData("GET", "/api/blind/seed/6f1c7a52-8d41-4a5e-9d0b-6a3cf0f1a001")]
    [InlineData("GET", "/api/blind/replica")]
    public async Task BlindRoutes_RefuseATokenBelowProtocol3(string method, string path)
    {
        var peer = await WhitelistSuperadminAsync();
        var store = _blind.Services.GetRequiredService<SyncTokenStore>();

        var current = await SendAsync(method, path, bearer: store.IssueToken(peer, SyncProtocolVersion.Current));
        var old = await SendAsync(method, path, bearer: store.IssueToken(peer, 2));

        current.Should().NotBe(HttpStatusCode.Unauthorized, "the control: this peer is let in at protocol 3");
        old.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The bootstrap: a valid pairing secret lets the first seed in with no token at all, and a
    /// presented secret is decided on its own — a wrong one is not rescued by a valid token.
    /// </summary>
    [Fact]
    public async Task SeedBootstrap_IsDecidedByThePairingSecretAlone()
    {
        var peer = await WhitelistSuperadminAsync();
        string secret;
        using (var scope = _blind.Services.CreateScope())
            secret = (await scope.ServiceProvider.GetRequiredService<BlindPairing>().GetCodeAsync(renew: false)).Secret;
        var token = _blind.Services.GetRequiredService<SyncTokenStore>().IssueToken(peer, SyncProtocolVersion.Current);
        const string progress = "/api/blind/seed/6f1c7a52-8d41-4a5e-9d0b-6a3cf0f1a002";

        var withSecret = await SendAsync("GET", progress, pairSecret: secret);
        var wrongSecretValidToken = await SendAsync("GET", progress, bearer: token, pairSecret: "wrong");

        withSecret.Should().Be(HttpStatusCode.NotFound, "authorized; there is just no such seed");
        wrongSecretValidToken.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<HttpStatusCode> SendAsync(string method, string path, string? bearer = null, string? pairSecret = null)
    {
        using var http = _blind.Server.CreateClient();
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (bearer != null) req.Headers.Authorization = new("Bearer", bearer);
        if (pairSecret != null)
        {
            // The seeder proof for the seed id in the path, made with pairSecret (BlindSeederProof).
            var seedId = Guid.Parse(path.Split('/').Last().Split('?')[0]);
            var nodeId = Guid.NewGuid();
            var key = Ed25519Signer.GenerateKeyPair().publicKey;
            req.Headers.Add(BlindSeederProof.NodeIdHeader, nodeId.ToString());
            req.Headers.Add(BlindSeederProof.KeyHeader, Convert.ToBase64String(key));
            req.Headers.Add(BlindSeederProof.MacHeader, BlindSeederProof.Compute(pairSecret, seedId, nodeId, key));
        }
        if (method == "POST") req.Content = new ByteArrayContent(new byte[4]);
        return (await http.SendAsync(req)).StatusCode;
    }

    private async Task<Guid> WhitelistSuperadminAsync()
    {
        var id = Guid.NewGuid();
        await _blind.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = id, DisplayName = "PC", Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            Status = "A", IsSuperadmin = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        return id;
    }
}
