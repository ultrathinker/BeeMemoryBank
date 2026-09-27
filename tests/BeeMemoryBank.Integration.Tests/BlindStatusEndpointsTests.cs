using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.BlindStatus;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// GET /api/blind/status: the assembled shape (CONTRACTS §5 base fields present), the extension
/// point (a second IBlindStatusContributor's section lands in the same JSON) and the authorization
/// rule — internal key, or a sync token of a peer whose whitelist row says superadmin.
/// </summary>
public class BlindStatusEndpointsTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeNodeAsync(password: "blindStatusPw");

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task InternalKey_BaseFieldsArePresent()
    {
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/blind/status");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("protocol").GetInt32().Should().BeGreaterThan(0);
        Guid.Parse(json.GetProperty("node_id").GetString()!).Should().NotBe(Guid.Empty);
        json.GetProperty("node_name").GetString().Should().Be("TestNode");
        json.GetProperty("role").GetString().Should().Be("full", "the test node has no BMB_ROLE=blind");
        json.GetProperty("data_path").GetString().Should().NotBeNullOrEmpty();
        json.GetProperty("backups_path").ValueKind.Should().Be(JsonValueKind.Null,
            "no backup repo is configured; the key is still present so consumers can tell 'unset' from 'unsupported'");

        var stored = json.GetProperty("stored");
        stored.GetProperty("articles").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        stored.GetProperty("blobs").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        stored.GetProperty("bytes").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        json.GetProperty("free_bytes").GetInt64().Should().BeGreaterThan(0);

        json.GetProperty("peers").GetArrayLength().Should().Be(0, "a freshly initialized node has no peers but itself");
        json.GetProperty("jobs").GetArrayLength().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task SecondContributor_SectionLandsInSameJson()
    {
        using var factory = new AnchorAndBlindRoleFactory();
        await factory.InitializeNodeAsync(password: "blindStatusPw");
        using var client = factory.CreateClient();

        var json = await (await client.GetAsync("/api/blind/status")).Content.ReadFromJsonAsync<JsonElement>();

        json.GetProperty("role").GetString().Should().Be("blind", "the role comes from the registered INodeRole");
        json.TryGetProperty("anchor", out var anchor).Should().BeTrue(
            "every registered IBlindStatusContributor runs against the same builder");
        anchor.GetProperty("confirmed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task NoCredentials_Unauthorized()
    {
        using var client = _factory.Server.CreateClient(); // no internal-key header

        (await client.GetAsync("/api/blind/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InternalKey_WithARegularUsersRole_IsForbidden()
    {
        // What the Web layer sends for a signed-in regular user on a full node.
        using var client = _factory.Server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        client.DefaultRequestHeaders.Add("X-User-Role", "user");

        (await client.GetAsync("/api/blind/status")).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the status names every peer and the storage layout: superadmin only");
    }

    [Fact]
    public async Task PlainPeerToken_Unauthorized_SuperadminPeerToken_Ok()
    {
        var plainPeer = await AddPeerAsync(_factory, superadmin: false);
        var adminPeer = await AddPeerAsync(_factory, superadmin: true);
        var store = _factory.Services.GetRequiredService<SyncTokenStore>();

        using var plain = _factory.Server.CreateClient();
        plain.DefaultRequestHeaders.Authorization =
            new("Bearer", store.IssueToken(plainPeer));
        (await plain.GetAsync("/api/blind/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a peer token alone must not reveal the mesh layout — the peer has to be a superadmin");

        using var admin = _factory.Server.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new("Bearer", store.IssueToken(adminPeer));
        (await admin.GetAsync("/api/blind/status")).StatusCode.Should().Be(HttpStatusCode.OK);

        // The superadmin path shows every OTHER active peer, with its lag once it has pulled.
        var json = await (await admin.GetAsync("/api/blind/status")).Content.ReadFromJsonAsync<JsonElement>();
        var peers = json.GetProperty("peers");
        peers.GetArrayLength().Should().Be(2);
        peers.EnumerateArray().Select(p => p.GetProperty("name").GetString())
            .Should().Contain(new[] { "plain-peer", "admin-peer" });
    }

    private static async Task<Guid> AddPeerAsync(BmbWebApplicationFactory factory, bool superadmin)
    {
        var peerId = Guid.NewGuid();
        var whitelist = factory.Services.GetRequiredService<IWhitelistRepository>();
        await whitelist.CreateAsync(new WhitelistEntry
        {
            NodeId = peerId,
            DisplayName = (superadmin ? "admin" : "plain") + "-peer",
            Ed25519PublicKey = new byte[32],
            Status = "A",
            IsSuperadmin = superadmin,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LamportTs = 1,
            SourceNodeId = peerId,
        });
        return peerId;
    }

    [Theory]
    [InlineData("GET", "/api/blind/backup/settings")]
    [InlineData("GET", "/api/blind/backup/list")]
    [InlineData("POST", "/api/blind/backup/now")]
    [InlineData("GET", "/api/blind/console/logins")]
    [InlineData("POST", "/api/blind/console/login")]
    [InlineData("POST", "/api/blind/wipe")]
    [InlineData("POST", "/api/blind/wipe/cli")]
    public async Task OnAFullNode_TheBlindOnlyRoutesDoNotExist(string method, string path)
    {
        using var client = _factory.CreateClient(); // internal key AND superadmin: the strongest caller
        var resp = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "POST" ? JsonContent.Create(new { }) : null,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "backups, the console and the wipe belong to the blind role; a full node must not carry them at all");
    }

    /// <summary>
    /// A second node with the fake anchor provider and the blind role. The role comes through DI
    /// instead of the process environment: BMB_ROLE is global to the test process, and
    /// CoreBlindStatusContributor prefers a registered INodeRole.
    /// </summary>
    private sealed class AnchorAndBlindRoleFactory : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton<IBlindStatusContributor>(new FakeAnchorContributor());
                s.AddSingleton<INodeRole>(new EnvironmentNodeRole("blind"));
            });
        }
    }

    /// <summary>Stands in for the recovery contributor (Opus-R1) that will fill "anchor" later.</summary>
    private sealed class FakeAnchorContributor : IBlindStatusContributor
    {
        public Task ContributeAsync(BlindStatusBuilder b, CancellationToken ct)
        {
            b.Set("anchor", new Dictionary<string, object?> { ["confirmed"] = true });
            return Task.CompletedTask;
        }
    }
}
