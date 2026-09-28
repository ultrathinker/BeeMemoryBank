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
/// Review L-stage0 #2: protocol 3 is enforced, not just recorded. A peer that declares an older
/// protocol, or none (an old build), is a node that would accept a blind node's events and seal the
/// master DEK for it — it gets no sync token, and so none of the data endpoints.
/// </summary>
public class SyncProtocolBoundaryTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeNodeAsync(password: "syncProtocolBoundaryPw");

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Authenticate_BelowProtocol3_IsRefused_WithNoToken(int? declared)
    {
        var (peerId, privateKey) = await WhitelistPeerAsync();

        var resp = await AuthenticateAsync(peerId, privateKey, declared);

        resp.StatusCode.Should().Be(HttpStatusCode.UpgradeRequired);
    }

    /// <summary>The control for the theory above: the same signed request at protocol 3 gets a token.</summary>
    [Fact]
    public async Task Authenticate_AtProtocol3_GetsAToken()
    {
        var (peerId, privateKey) = await WhitelistPeerAsync();

        var resp = await AuthenticateAsync(peerId, privateKey, SyncProtocolVersion.Current);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<TokenDto>())!.Token.Should().NotBeNullOrEmpty();
    }

    /// <summary>A refused old peer still shows up as old in the PC's pre-flight.</summary>
    [Fact]
    public async Task Authenticate_AtProtocol2_IsRecorded_ThoughRefused()
    {
        var (peerId, privateKey) = await WhitelistPeerAsync();

        await AuthenticateAsync(peerId, privateKey, 2);

        (await Whitelist.GetByNodeIdAsync(peerId))!.LastProtocolVersion.Should().Be(2);
    }

    /// <summary>
    /// Every token-gated endpoint — events both ways, blobs, report-position, the join snapshot and
    /// the restore file — refuses a token bound to protocol 2, and accepts the same peer at 3.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/sync/events?afterSequence=0")]
    [InlineData("POST", "/api/sync/events")]
    [InlineData("POST", "/api/sync/blobs/check")]
    [InlineData("POST", "/api/sync/blobs/get")]
    [InlineData("POST", "/api/sync/report-position?sequence=0")]
    [InlineData("GET", "/api/sync/snapshot/for-join")]
    [InlineData("GET", "/api/snapshots/restore/00000000-0000-0000-0000-000000000001/file")]
    public async Task DataEndpoints_RefuseATokenBelowProtocol3(string method, string path)
    {
        var (peerId, _) = await WhitelistPeerAsync();
        var store = _factory.Services.GetRequiredService<SyncTokenStore>();

        var current = await SendAsync(method, path, store.IssueToken(peerId, SyncProtocolVersion.Current));
        var old = await SendAsync(method, path, store.IssueToken(peerId, 2));

        current.Should().NotBe(HttpStatusCode.Unauthorized, "the control: this peer is let in at protocol 3");
        old.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<HttpStatusCode> SendAsync(string method, string path, string token)
    {
        using var http = _factory.Server.CreateClient();
        using var req = new HttpRequestMessage(new HttpMethod(method), path);
        req.Headers.Authorization = new("Bearer", token);
        if (path.StartsWith("/api/sync/blobs")) req.Content = JsonContent.Create(new { hashes = Array.Empty<string>() });
        else if (method == "POST") req.Content = JsonContent.Create(Array.Empty<object>());
        return (await http.SendAsync(req)).StatusCode;
    }

    private async Task<HttpResponseMessage> AuthenticateAsync(Guid peerId, byte[] privateKey, int? protocolVersion)
    {
        var http = _factory.Server.CreateClient();
        var challenge = await (await http.PostAsync("/api/sync/challenge", null))
            .Content.ReadFromJsonAsync<ChallengeDto>();
        var payload = "BMB-CHALLENGE-V2\0"u8.ToArray()
            .Concat(challenge!.ServerNodeId.ToByteArray())
            .Concat(Convert.FromBase64String(challenge.Challenge))
            .ToArray();

        return await http.PostAsJsonAsync("/api/sync/authenticate", new
        {
            NodeId = peerId,
            ChallengeB64 = challenge.Challenge,
            SignatureB64 = Convert.ToBase64String(Ed25519Signer.Sign(privateKey, payload)),
            ProtocolVersion = protocolVersion
        });
    }

    private IWhitelistRepository Whitelist => _factory.Services.GetRequiredService<IWhitelistRepository>();

    private async Task<(Guid PeerId, byte[] PrivateKey)> WhitelistPeerAsync()
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
        return (peerId, priv);
    }

    private sealed record ChallengeDto(string Challenge, Guid ServerNodeId);

    private sealed record TokenDto(string Token);
}
