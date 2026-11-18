using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Release 2.12, "this device was disconnected": a device whose whitelist row is revoked, and that proves it is itself, gets a 401 that says so,
/// signed by the node and bound to the challenge it signed; the apps then say one calm sentence instead of a sync error. Everybody else - a wrong
/// signature, an unknown node, a node that cannot sign because its vault is locked - gets the plain 401 of any refusal, as before.
/// </summary>
public class DeviceRevokedRefusalTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();

    public async Task InitializeAsync()
    {
        await _factory.InitializeNodeAsync(password: "deviceRevokedPw");
        // An open vault signs with the node's key (a locked one cannot: the test of that locks it again).
        (await _factory.Services.GetRequiredService<SessionService>().UnlockAsync("deviceRevokedPw")).Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private IWhitelistRepository Whitelist => _factory.Services.GetRequiredService<IWhitelistRepository>();

    private async Task<NodeIdentity> SelfAsync() => (await _factory.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;

    private async Task<(Guid PeerId, INodeAuthSigner Signer)> PeerAsync(bool revoked)
    {
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        var peerId = Guid.NewGuid();
        await Whitelist.CreateAsync(new WhitelistEntry
        {
            NodeId = peerId, DisplayName = "Anna's phone", Ed25519PublicKey = pub, Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LamportTs = 1, SourceNodeId = peerId,
        });
        if (revoked) await Whitelist.RevokeAsync(peerId, new RowVersion(5, (await SelfAsync()).NodeId));
        return (peerId, new RawKeySigner(priv));
    }

    [Fact]
    public async Task A_revoked_device_that_proves_itself_is_told_so_signed_by_the_node()
    {
        var (peerId, signer) = await PeerAsync(revoked: true);
        var self = await SelfAsync();
        using var http = _factory.Server.CreateClient();

        var act = () => PeerAuthenticator.AuthenticateAsync(signer, http, http.BaseAddress!.ToString(), new NodeIdentity { NodeId = peerId },
            self.NodeId, expectedServerPublicKey: self.Ed25519PublicKey);

        var thrown = (await act.Should().ThrowAsync<ThisDeviceRevokedException>()).Which;
        thrown.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "it stays a refused door for every caller that does not know the type");
        thrown.PeerNodeId.Should().Be(self.NodeId);
        thrown.Message.Should().Be(ThisDeviceRevokedException.Sentence);
        PeerContactLog.Describe(thrown).Should().Contain("an administrator disconnected this device");
    }

    [Fact]
    public async Task Without_the_nodes_key_on_record_the_refusal_stays_a_plain_401()
    {
        var (peerId, signer) = await PeerAsync(revoked: true);
        var self = await SelfAsync();
        using var http = _factory.Server.CreateClient();

        var act = () => PeerAuthenticator.AuthenticateAsync(signer, http, http.BaseAddress!.ToString(), new NodeIdentity { NodeId = peerId }, self.NodeId);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        thrown.Should().NotBeOfType<ThisDeviceRevokedException>();
        thrown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_proof_under_another_key_than_the_one_on_record_is_not_believed()
    {
        var (peerId, signer) = await PeerAsync(revoked: true);
        var self = await SelfAsync();
        var (stranger, _) = Ed25519Signer.GenerateKeyPair();
        using var http = _factory.Server.CreateClient();

        var act = () => PeerAuthenticator.AuthenticateAsync(signer, http, http.BaseAddress!.ToString(), new NodeIdentity { NodeId = peerId },
            self.NodeId, expectedServerPublicKey: stranger);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Should().NotBeOfType<ThisDeviceRevokedException>();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_caller_that_does_not_prove_it_is_the_device_gets_the_plain_401_with_nothing_in_it(bool revoked)
    {
        var (peerId, _) = await PeerAsync(revoked);
        var body = await AuthenticateWithAsync(peerId, new byte[64]);

        body.Status.Should().Be(HttpStatusCode.Unauthorized);
        body.Text.Should().NotContain(DeviceRevokedProof.Refusal, "whether a row is revoked is said only to the device itself");
    }

    [Fact]
    public async Task An_unknown_node_gets_the_plain_401()
    {
        var (_, priv) = Ed25519Signer.GenerateKeyPair();
        var body = await AuthenticateWithAsync(Guid.NewGuid(), null, priv);

        body.Status.Should().Be(HttpStatusCode.Unauthorized);
        body.Text.Should().NotContain(DeviceRevokedProof.Refusal);
    }

    [Fact]
    public async Task A_node_whose_vault_is_locked_cannot_sign_and_says_no_more_than_before()
    {
        var (peerId, signer) = await PeerAsync(revoked: true);
        var self = await SelfAsync();
        _factory.Services.GetRequiredService<SessionService>().Lock();
        using var http = _factory.Server.CreateClient();

        var act = () => PeerAuthenticator.AuthenticateAsync(signer, http, http.BaseAddress!.ToString(), new NodeIdentity { NodeId = peerId },
            self.NodeId, expectedServerPublicKey: self.Ed25519PublicKey);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        thrown.Should().NotBeOfType<ThisDeviceRevokedException>();
        thrown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_active_device_still_gets_its_token()
    {
        var (peerId, signer) = await PeerAsync(revoked: false);
        var self = await SelfAsync();
        using var http = _factory.Server.CreateClient();

        var token = await PeerAuthenticator.AuthenticateAsync(signer, http, http.BaseAddress!.ToString(), new NodeIdentity { NodeId = peerId },
            self.NodeId, expectedServerPublicKey: self.Ed25519PublicKey);

        token.Should().NotBeNullOrEmpty();
    }

    /// <summary>One authenticate by hand: <paramref name="signature"/> as given, or the V2 signature of the challenge under <paramref name="privateKey"/>.</summary>
    private async Task<(HttpStatusCode Status, string Text)> AuthenticateWithAsync(Guid nodeId, byte[]? signature, byte[]? privateKey = null)
    {
        using var http = _factory.Server.CreateClient();
        var challenge = (await (await http.PostAsync("/api/sync/challenge", null)).Content.ReadFromJsonAsync<ChallengeDto>())!;
        if (signature is null)
        {
            var payload = "BMB-CHALLENGE-V2\0"u8.ToArray().Concat(challenge.ServerNodeId.ToByteArray()).Concat(Convert.FromBase64String(challenge.Challenge)).ToArray();
            signature = Ed25519Signer.Sign(privateKey!, payload);
        }
        var resp = await http.PostAsJsonAsync("/api/sync/authenticate", new
        {
            NodeId = nodeId, ChallengeB64 = challenge.Challenge, SignatureB64 = Convert.ToBase64String(signature), ProtocolVersion = SyncProtocolVersion.Current,
        });
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    private sealed class RawKeySigner(byte[] privateKey) : INodeAuthSigner
    {
        public byte[] SignChallenge(NodeIdentity identity, byte[] challengePayload) => Ed25519Signer.Sign(privateKey, challengePayload);
    }

    private sealed record ChallengeDto(string Challenge, Guid ServerNodeId);
}
