using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Hosting.AspNetCore;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>A signer a test can break: it signs as the node does, or it fails, or it signs with a key that is not the node's.</summary>
public sealed class SwitchableNodeSigner(INodeAuthSigner real, Func<string> mode) : INodeAuthSigner
{
    public byte[] SignChallenge(NodeIdentity identity, byte[] challengePayload)
    {
        switch (mode())
        {
            case "fail":
                throw new InvalidOperationException("the identity key is not available in this mode");
            case "other-key":
                var (_, seed) = Ed25519Signer.GenerateKeyPair();
                return Ed25519Signer.Sign(seed, challengePayload);
            default:
                return real.SignChallenge(identity, challengePayload);
        }
    }
}

/// <summary>A hub of its own for the tests of what is signed (fix round 1, BLOCKER-1: the hub ALWAYS signs an invitation, and says so when it cannot).</summary>
public sealed class BlindInviteSigningWorld : IAsyncLifetime
{
    public const string Password = "blindInviteSigningPassword1";

    public string SignerMode { get; set; } = "real";
    public TestPki Pki { get; } = new();
    internal BlindHubHost Hub { get; private set; } = null!;
    public NodeIdentity HubIdentity { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var leaf = Pki.IssueServer("localhost");
        var anchors = new TlsTrustAnchors(Pki.Anchors);
        Hub = await BlindHubHost.StartAsync(leaf, "Hub", Password, services =>
        {
            services.AddSingleton(anchors);
            services.AddSingleton(new HubTrustProbe(anchors, HubProbeAddressGuardTests.AnyAddress));
            services.AddScoped<INodeAuthSigner>(sp => new SwitchableNodeSigner(
                new SessionNodeAuthSigner(sp.GetRequiredService<SessionService>(), sp.GetService<IExternalNodeKey>()), () => SignerMode));
        });
        Hub.Admin.DefaultRequestHeaders.Add("X-User-Id", "owner");
        using var scope = Hub.Services.CreateScope();
        HubIdentity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
    }

    public async Task DisposeAsync()
    {
        await Hub.DisposeAsync();
        Pki.Dispose();
    }
}

public sealed class BlindInviteSigningTests(BlindInviteSigningWorld world) : IClassFixture<BlindInviteSigningWorld>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private Task<HttpResponseMessage> IssueAsync(string trust) =>
        world.Hub.Admin.PostAsJsonAsync("/api/blind-nodes/invites", new { label = "Anna", sourceName = "Alex", lifetimeMinutes = 30, address = world.Hub.Origin, trust });

    private async Task<int> RowsAsync()
    {
        using var conn = world.Hub.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return (int)await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_blind_invite");
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("pin")]
    [InlineData("public-ca")]
    public async Task EveryInvitationTheHubIssues_CarriesItsSignature_ThatVerifiesAgainstTheHubsOwnKey_InEveryTrustMode(string trust)
    {
        world.SignerMode = "real";

        var response = await IssueAsync(trust);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var text = (await response.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("text").GetString()!;
        text.Should().Contain("&g=", "the hub always signs");
        BlindInvitation.TryParse(text, out var invitation).Should().BeTrue("a device takes only a signed invitation");
        invitation!.SignatureIsValid().Should().BeTrue();
        invitation.HubPublicKey.Should().Equal(world.HubIdentity.Ed25519PublicKey, "the signature is by the hub's own identity key, the one the string names");
        invitation.HubNodeId.Should().Be(world.HubIdentity.NodeId);
    }

    [Fact]
    public async Task WhenTheNodeCannotSign_NothingIsIssued_AndNoRowIsWritten_AndTheOwnerIsToldWhy()
    {
        world.SignerMode = "real";
        var before = await RowsAsync();
        world.SignerMode = "fail";
        try
        {
            var response = await IssueAsync("auto");

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("cannot sign an invitation").And.Contain("No invitation was made");
            body.Should().NotContain("&x=").And.NotContain("bmb-invite:");
            (await RowsAsync()).Should().Be(before, "an invitation nobody could use does not take one of the open places");
        }
        finally
        {
            world.SignerMode = "real";
        }
        (await IssueAsync("auto")).StatusCode.Should().Be(HttpStatusCode.OK, "and it works again as soon as the node can sign");
    }

    [Fact]
    public async Task ASignerThatSignsWithAKeyThatIsNotTheNodes_IssuesNothing()
    {
        var before = await RowsAsync();
        world.SignerMode = "other-key";
        try
        {
            var response = await IssueAsync("auto");

            response.IsSuccessStatusCode.Should().BeFalse("a device would refuse that string, so it is not handed out");
            (await response.Content.ReadAsStringAsync()).Should().NotContain("bmb-invite:");
            (await RowsAsync()).Should().Be(before);
        }
        finally
        {
            world.SignerMode = "real";
        }
    }
}
