using System.Net.Http.Json;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>When a node publishes an integrity anchor (plan 5.5).</summary>
public class StateAnchorSchedulerTests : IAsyncLifetime
{
    private const string Password = "AnchorPass1";
    private readonly RecoveryTestFactory _factory = new(RecoveryHostKind.Hub, 8L << 30);
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        await _factory.Services.GetRequiredService<SessionService>().UnlockAsync(Password);
        (await _client.PostAsJsonAsync("/api/articles", new { title = "A", treePath = "/N", content = "x" })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private StateAnchorScheduler Scheduler() => _factory.Services.GetRequiredService<StateAnchorScheduler>();

    [Fact]
    public async Task NotASuperadminByTheNetworksStanding_NoAnchor()
    {
        using var plain = new RecoveryTestFactory(RecoveryHostKind.Hub, 8L << 30, superadmin: false);
        await plain.InitializeNodeAsync(password: Password);
        await plain.Services.GetRequiredService<SessionService>().UnlockAsync(Password);
        using (var scope = plain.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = BlindNodeId.NewId(), DisplayName = "Blind", Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
                Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });

        (await plain.Services.GetRequiredService<StateAnchorScheduler>().PublishIfDueAsync(DateTime.UtcNow)).Should().BeFalse();
    }

    [Fact]
    public async Task WithoutBlindNode_NoAnchor()
    {
        (await Scheduler().PublishIfDueAsync(DateTime.UtcNow)).Should().BeFalse();
        (await AnchorCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task CaughtUpWithABlindNode_Publishes_ThenOnlyAfterAChangeAndTheInterval()
    {
        await AddPeerAsync(BlindNodeId.NewId(), apiAddress: null);
        var now = DateTime.UtcNow;

        (await Scheduler().PublishIfDueAsync(now)).Should().BeTrue();
        (await Scheduler().PublishIfDueAsync(now.AddHours(5))).Should().BeFalse("nothing changed since the last anchor");

        (await _client.PostAsJsonAsync("/api/articles", new { title = "B", treePath = "/N", content = "y" })).EnsureSuccessStatusCode();
        (await Scheduler().PublishIfDueAsync(now.AddMinutes(10))).Should().BeFalse("too soon after the last anchor");
        (await Scheduler().PublishIfDueAsync(now.AddHours(2))).Should().BeTrue();

        (await AnchorCountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task PeerWeHaveNotPulledFromRecently_MeansNotCaughtUp()
    {
        await AddPeerAsync(BlindNodeId.NewId(), apiAddress: "https://blind.test:5610");

        (await Scheduler().PublishIfDueAsync(DateTime.UtcNow)).Should().BeFalse();
    }

    [Fact]
    public async Task QuarantinedEvent_MeansNotCaughtUp()
    {
        await AddPeerAsync(BlindNodeId.NewId(), apiAddress: null);
        using (var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
        {
            var cols = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('tbl_sync_quarantine') WHERE \"notnull\" = 1 AND dflt_value IS NULL")).ToList();
            var values = cols.ToDictionary(c => c, c => (object)(c.Contains("count") ? 1 : c.EndsWith("_at") ? DateTime.UtcNow.ToString("O") : Guid.NewGuid().ToString()));
            await conn.ExecuteAsync(
                $"INSERT INTO tbl_sync_quarantine ({string.Join(",", cols)}) VALUES ({string.Join(",", cols.Select(c => "@" + c))})",
                new DynamicParameters(values));
        }

        (await Scheduler().PublishIfDueAsync(DateTime.UtcNow)).Should().BeFalse();
    }

    [Fact]
    public async Task PublishedAnchor_VerifiesUnderTheCurrentKey()
    {
        await AddPeerAsync(BlindNodeId.NewId(), apiAddress: null);
        await Scheduler().PublishIfDueAsync(DateTime.UtcNow);

        var dek = _factory.Services.GetRequiredService<SessionService>().GetMasterDek();
        using var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        var identity = (await _factory.Services.CreateScope().ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var events = await _factory.Services.CreateScope().ServiceProvider.GetRequiredService<IEventLogRepository>()
            .GetRecentAsync(10, 0, BeeMemoryBank.Sync.EventTypes.StateAnchor);
        var result = await BeeMemoryBank.Sync.Recovery.StateAnchorService.VerifyAsync(conn, dek, events,
            new Dictionary<Guid, byte[]> { [identity.NodeId] = identity.Ed25519PublicKey }, headProven: true);

        result.Confirmed.Should().BeTrue();
    }

    [Fact]
    public async Task BlindStatus_SaysWhetherTheStateStillMatchesTheNewestAnchor_WithoutTheDek()
    {
        using var scope = _factory.Services.CreateScope();
        var status = scope.ServiceProvider.GetRequiredService<RecoveryStatusService>();
        (await status.NewestAnchorStateAsync()).Should().BeNull();

        await AddPeerAsync(BlindNodeId.NewId(), apiAddress: null);
        await Scheduler().PublishIfDueAsync(DateTime.UtcNow);
        _factory.Services.GetRequiredService<SessionService>().Lock(); // what a blind node has: no key

        (await status.NewestAnchorStateAsync())!.Value.StateMatches.Should().BeTrue();

        using (var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_article SET title = 'Tampered'");
        (await status.NewestAnchorStateAsync())!.Value.StateMatches.Should().BeFalse();
    }

    private async Task<long> AnchorCountAsync()
    {
        using var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_state_anchor");
    }

    private async Task AddPeerAsync(Guid nodeId, string? apiAddress)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = nodeId, DisplayName = "Blind", Ed25519PublicKey = Ed25519Signer.GenerateKeyPair().publicKey,
            ApiAddress = apiAddress, Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
    }
}
