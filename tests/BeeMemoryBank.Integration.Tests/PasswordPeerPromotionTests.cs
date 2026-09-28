extern alias WebApp;

using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;
using ApiClient = WebApp::BeeMemoryBank.Web.Services.ApiClient;
using PromotePasswordPeersModel = WebApp::BeeMemoryBank.Web.Pages.PromotePasswordPeersModel;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-42 for rows recorded before "whoever knows the master password is a superadmin": the Admin
/// page lists them and promotes the confirmed ones, one <c>whitelist_update</c> each — never a blind
/// node, never a revoked one, never a row that is already a superadmin. And a re-join with the same key
/// raises the row by itself.
/// </summary>
public class PasswordPeerPromotionTests : IAsyncLifetime
{
    private const string Password = "passwordPeerPromotion1";

    private BmbWebApplicationFactory _api = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _api = new BmbWebApplicationFactory();
        _client = _api.CreateClient();
        await _api.InitializeNodeAsync("Host", Password);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _api.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ConfirmedDevices_ArePromoted_OneEventEach_BlindRevokedAndSuperadminsLeftAlone()
    {
        var oldPhone = await AddRowAsync(Guid.NewGuid(), isSuperadmin: false);
        var admin = await AddRowAsync(Guid.NewGuid(), isSuperadmin: true);
        var blind = await AddRowAsync(BlindNodeId.NewId(), isSuperadmin: false);
        var revoked = await AddRowAsync(Guid.NewGuid(), isSuperadmin: false, status: "R");

        var page = new PromotePasswordPeersModel(new ApiClient(_client));
        await page.OnGetAsync();
        page.Candidates.Select(c => c.NodeId).Should().Equal([oldPhone],
            "only an active, non-blind device that is not a superadmin yet is offered");

        // A tampered form naming everything must still promote only the candidate.
        await page.OnPostAsync([oldPhone, admin, blind, revoked]);

        (await RowAsync(oldPhone)).IsSuperadmin.Should().BeTrue();
        (await RowAsync(blind)).IsSuperadmin.Should().BeFalse("a blind node is never a superadmin");
        (await RowAsync(revoked)).IsSuperadmin.Should().BeFalse();
        (await UpdatesAboutAsync(oldPhone)).Should().ContainSingle()
            .Which.IsSuperadmin.Should().BeTrue("the rest of the mesh must hear the promotion");
        (await UpdatesAboutAsync(blind)).Should().BeEmpty();
        (await UpdatesAboutAsync(admin)).Should().BeEmpty("an already-superadmin row needs no event");
    }

    [Fact]
    public async Task ReJoin_WithTheSameKey_RaisesAnOldContentOnlyRow_AndTellsTheMesh()
    {
        var nodeId = Guid.NewGuid();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        object Join() => new
        {
            masterPassword = Password, nodeId, displayName = "OldPhone",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey), apiAddress = (string?)null
        };
        (await _client.PostAsJsonAsync("/api/join", Join())).EnsureSuccessStatusCode();

        // What a pre-BMB-42 join left behind.
        using (var scope = _api.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            var row = (await repo.GetByNodeIdAsync(nodeId))!;
            row.IsSuperadmin = false;
            await repo.UpdateAsync(row);
        }

        (await _client.PostAsJsonAsync("/api/join", Join())).EnsureSuccessStatusCode();

        (await RowAsync(nodeId)).IsSuperadmin.Should().BeTrue("the re-join proved the master password again");
        (await UpdatesAboutAsync(nodeId)).Should().ContainSingle()
            .Which.IsSuperadmin.Should().BeTrue("the whitelist_update of the re-join carries the promotion");
    }

    [Fact]
    public async Task ReJoin_WithoutAnAddress_KeepsTheKnownOne_LocallyAndInTheEvent()
    {
        var nodeId = Guid.NewGuid();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        object Join(string? apiAddress) => new
        {
            masterPassword = Password, nodeId, displayName = "Laptop",
            ed25519PublicKeyB64 = Convert.ToBase64String(publicKey), apiAddress
        };
        (await _client.PostAsJsonAsync("/api/join", Join("https://laptop.test:5311"))).EnsureSuccessStatusCode();

        // Phones and `bmb join` send no address when they re-join.
        (await _client.PostAsJsonAsync("/api/join", Join(null))).EnsureSuccessStatusCode();

        (await RowAsync(nodeId)).ApiAddress.Should().Be("https://laptop.test:5311",
            "a re-join that does not say where the peer is must not make this node forget it");
        (await UpdatesAboutAsync(nodeId)).Should().ContainSingle()
            .Which.ApiAddress.Should().Be("https://laptop.test:5311",
                "the other nodes are told the same address, so the mesh agrees on it");
    }

    [Fact]
    public async Task PromoteEndpoint_RefusesABlindNode_AndTellsNobody()
    {
        var blind = await AddRowAsync(BlindNodeId.NewId(), isSuperadmin: false);

        var resp = await _client.PutAsJsonAsync($"/api/whitelist/{blind}/superadmin", new { isSuperadmin = true });

        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        (await RowAsync(blind)).IsSuperadmin.Should().BeFalse("a blind node is never a superadmin");
        (await UpdatesAboutAsync(blind)).Should().BeEmpty("a refused promotion must not reach the mesh");
    }

    [Theory]
    [InlineData("auto-accept-restore")]
    [InlineData("auto-accept-dek-rotation")]
    public async Task AutoAcceptEndpoints_RefuseABlindNode_AndStoreNothing(string endpoint)
    {
        var blind = await AddRowAsync(BlindNodeId.NewId(), isSuperadmin: true); // a polluted row

        var resp = await _client.PutAsJsonAsync($"/api/whitelist/{blind}/{endpoint}", new { autoAccept = true });

        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        using var scope = _api.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Storage.Sqlite.DbConnectionFactory>().CreateConnection();
        var (restore, rotation) = await Dapper.SqlMapper.QuerySingleAsync<(long, long)>(conn,
            "SELECT auto_accept_restore, auto_accept_dek_rotation FROM tbl_whitelist WHERE node_id = @Id COLLATE NOCASE",
            new { Id = blind.ToString() });
        (restore, rotation).Should().Be((0L, 0L), "a blind node is never trusted to restore or rewrap this vault unattended");
    }

    private async Task<Guid> AddRowAsync(Guid nodeId, bool isSuperadmin, string status = "A")
    {
        using var scope = _api.Services.CreateScope();
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = nodeId,
            DisplayName = nodeId.ToString("N")[..6],
            Ed25519PublicKey = publicKey,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsSuperadmin = isSuperadmin
        });
        return nodeId;
    }

    private async Task<WhitelistEntry> RowAsync(Guid nodeId)
    {
        using var scope = _api.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>()
            .GetByNodeIdAsync(nodeId, includeDeleted: true))!;
    }

    private async Task<List<WhitelistUpdatePayload>> UpdatesAboutAsync(Guid nodeId)
    {
        using var scope = _api.Services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(0, 1000);
        return events.Where(e => e.EventType == EventTypes.WhitelistUpdate)
            .Select(e => JsonSerializer.Deserialize<WhitelistUpdatePayload>(e.Payload)!)
            .Where(p => p.NodeId == nodeId)
            .ToList();
    }
}
