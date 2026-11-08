using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Channels;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// The <c>node_endpoints</c> event on a blind copy (docs/design/lan-peer-sync.md section 8): a hub whose version has the LAN sync serves it to a copy that advertises
/// <c>peer-endpoints-v1</c>, and the copy - through the composition a phone or desktop copy runs - applies it as the control event it is, keeps its pull cursor moving,
/// holds it in its log like every event it relays, and can carry it in a backup. A copy whose database was last opened by a build without migration 044 is migrated to
/// the schema of this release (44 and 45) before its first pull; if the table were ever missing, the event is quarantined after its five failures and does not stop the events behind it.
/// </summary>
public sealed class NodeEndpointsOnACopyTests : IDisposable
{
    private readonly AccountRig _rig = new();
    private readonly HubNetwork _net = new();

    public void Dispose() => _rig.Dispose();

    private async Task<(BlindAccountHost Host, string Id)> PairedAsync(BlindAccountHost? existing = null, string? id = null)
    {
        _net.Answer(HubNetwork.PairingAuthority, HubNetwork.Mode.Hub);
        var host = existing ?? _rig.Host(handler: _ => () => new ForwardTo(_net), overrides: (services, _) => services.AddSingleton<ILocalNetworks>(FixedNetworks.Home()));
        await host.StartAsync();
        id ??= (await host.AddAsync("Home hub")).AccountId!;
        var entry = host.GetSnapshot().Accounts.Single(x => x.Entry.Id == id).Entry;
        host.AcceptCallCode(_net.CallCodeFor(_rig.Secrets.For(entry).LoadPairingSecret()!)).Should().BeNull();
        await SeedAsync(host, id);
        return (host, id);
    }

    private async Task SeedAsync(BlindAccountHost host, string id)
    {
        var services = host.ServicesFor(id);
        await services.GetRequiredService<BlindStartup>().EnsureReadyAsync();
        await services.GetRequiredService<IWhitelistRepository>().CreateAsync(_net.WhitelistRow());
        services.GetRequiredService<BlindPhoneState>().InitialLoadDone = true;
    }

    private static IDbConnectionFactory Db(BlindAccountHost host, string id) => host.ServicesFor(id).GetRequiredService<IDbConnectionFactory>();

    private static async Task<List<string>> FoldersAsync(BlindAccountHost host, string id)
    {
        using var conn = Db(host, id).CreateConnection();
        return (await conn.QueryAsync<string>("SELECT name FROM tbl_folder")).ToList();
    }

    private static async Task<long> CursorAsync(BlindAccountHost host, string id, Guid hub)
    {
        using var scope = host.ServicesFor(id).CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ISyncPositionRepository>().GetAsync(hub))?.LastSequenceNum ?? 0;
    }

    private static async Task<List<SyncQuarantineEntry>> QuarantineAsync(BlindAccountHost host, string id)
    {
        using var scope = host.ServicesFor(id).CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISyncQuarantineRepository>().GetAllAsync();
    }

    // ---- the event in a pull ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHubsAnnouncement_IsWrittenToTheCopy_TheCursorMovesOn_AndNothingIsQuarantined()
    {
        _net.AddFolder("before");
        var announcement = _net.AddNodeEndpoints(HubNetwork.Pairing, "https://192.168.1.60:5311");
        _net.AddFolder("after");
        var (host, id) = await PairedAsync();
        await using var _ = host;

        (await host.For(id).RequestSyncAsync()).Should().Be("Synced.");

        var row = await host.Get<PeerEndpointStore>(id).GetAsync(_net.NodeId);
        row.Should().NotBeNull("the applier wrote the hub's record to the copy's tbl_peer_endpoint");
        row!.Endpoints.Select(e => e.Url).Should().Equal(HubNetwork.Pairing, "https://192.168.1.60:5311");
        row.TlsLeafSpki.Should().Be(_net.Pin);
        row.LamportTs.Should().Be(announcement.LamportTs);
        (await CursorAsync(host, id, _net.NodeId)).Should().Be(3, "the page was applied whole, the announcement in the middle of it");
        (await QuarantineAsync(host, id)).Should().BeEmpty();
        (await FoldersAsync(host, id)).Should().BeEquivalentTo("before", "after");

        // The copy says what it can take in the report it makes at the end of the round: that is what makes a hub serve the event at all.
        _net.Calls.Where(c => c.Path == "/api/sync/report-position").Select(c => Uri.UnescapeDataString(c.Query))
            .Should().OnlyContain(q => q.Contains(MetadataCapabilities.PeerEndpointsV1));
        MetadataCapabilities.Advertised.Should().Contain(MetadataCapabilities.PeerEndpointsV1);
    }

    [Fact]
    public async Task AnAnnouncementAfterTheActivation_IsStillAppliedAsTheControlEventItIs_AndClearContentBesideItIsNot()
    {
        var (host, id) = await PairedAsync();
        await using var _ = host;
        await ActivateAsync(host, id);
        _net.AddNodeEndpoints(HubNetwork.Pairing);
        _net.AddFolder("stragglerfolder");

        (await host.For(id).RequestSyncAsync()).Should().Be("Synced.");

        (await host.Get<PeerEndpointStore>(id).GetAsync(_net.NodeId))!.Endpoints.Select(e => e.Url).Should().Equal(HubNetwork.Pairing);
        (await FoldersAsync(host, id)).Should().BeEmpty("a clear content event is accepted without being stored once the copy has activated");
        (await CursorAsync(host, id, _net.NodeId)).Should().Be(2);
        (await QuarantineAsync(host, id)).Should().BeEmpty();
    }

    [Fact]
    public async Task ANewerAnnouncement_ReplacesTheOlderOne()
    {
        _net.AddNodeEndpoints("https://192.168.1.60:5311");
        var (host, id) = await PairedAsync();
        await using var _ = host;
        await host.For(id).RequestSyncAsync();
        _net.AddNodeEndpoints("https://192.168.1.61:5311");
        await host.For(id).RequestSyncAsync();

        (await host.Get<PeerEndpointStore>(id).GetAsync(_net.NodeId))!.Endpoints.Select(e => e.Url).Should().Equal("https://192.168.1.61:5311");
    }

    // ---- relayed ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheAnnouncementIsHeldInTheCopysLog_AndItsSignatureStillVerifiesWhenABackupCarriesIt()
    {
        var announcement = _net.AddNodeEndpoints(HubNetwork.Pairing);
        var (host, id) = await PairedAsync();
        await using var _ = host;
        await host.For(id).RequestSyncAsync();

        using var scope = host.ServicesFor(id).CreateScope();
        var log = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
        (await log.ExistsAsync(announcement.EventId)).Should().BeTrue();
        var held = (await log.GetAllAfterSequenceAsync(0, 100)).Single(e => e.EventId == announcement.EventId);
        held.EventType.Should().Be(EventTypes.NodeEndpoints);
        held.NodeId.Should().Be(_net.NodeId);

        // The backup writes every event of the log as JSON (BlindPhonePackageSource), and a restore reads it back and checks the signature.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var carried = JsonSerializer.Deserialize<SyncEvent>(JsonSerializer.Serialize(held, web), web)!;
        Ed25519Signer.Verify(_net.PublicKey, EventSignature.BuildPayload(carried), carried.Signature).Should().BeTrue();
        carried.Payload.Should().Be(announcement.Payload);
    }

    // ---- the schema ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ACopyLastOpenedBeforeMigration044_IsMigratedToSchema45_BeforeItsFirstPull()
    {
        _net.AddNodeEndpoints(HubNetwork.Pairing);
        _net.AddFolder("one");
        var (first, id) = await PairedAsync();
        // The database of an app of the release before this one: schema 43, none of the two tables of 044 and none of the whitelist columns of 045.
        using (var conn = Db(first, id).CreateConnection())
        {
            await conn.ExecuteAsync("DROP TABLE tbl_peer_endpoint; DROP TABLE tbl_peer_channel; DROP TRIGGER trg_migration_ledger_guard; DELETE FROM tbl_migration WHERE version IN (44, 45)");
            foreach (var column in Migration045Columns) await conn.ExecuteAsync($"ALTER TABLE tbl_whitelist DROP COLUMN {column}");
            (await conn.ExecuteScalarAsync<long>("SELECT MAX(version) FROM tbl_migration")).Should().Be(43);
        }
        await first.DisposeAsync();

        // The new app starts: the account opens its database (migrations) before any round runs.
        var (host, _) = await ReopenAsync(id);
        await using var _ = host;
        using (var conn = Db(host, id).CreateConnection())
        {
            (await conn.ExecuteScalarAsync<long>("SELECT MAX(version) FROM tbl_migration")).Should().Be(45);
            (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE name IN ('tbl_peer_endpoint', 'tbl_peer_channel')")).Should().Be(2);
            (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('tbl_whitelist')")).Should().Contain(Migration045Columns);
        }

        (await host.For(id).RequestSyncAsync()).Should().Be("Synced.");
        (await host.Get<PeerEndpointStore>(id).GetAsync(_net.NodeId))!.Endpoints.Select(e => e.Url).Should().Equal(HubNetwork.Pairing);
        (await QuarantineAsync(host, id)).Should().BeEmpty();
    }

    /// <summary>The columns migration 045 adds to tbl_whitelist (045_whitelist_concern_versions.sql).</summary>
    private static readonly string[] Migration045Columns =
    [
        "member_lamport_ts", "member_source_node_id", "superadmin_lamport_ts", "superadmin_source_node_id", "name_lamport_ts", "name_source_node_id",
        "address_lamport_ts", "address_source_node_id", "tls_lamport_ts", "tls_source_node_id", "undated_fields",
    ];

    [Fact]
    public async Task IfTheTableWereMissing_TheAnnouncementIsQuarantinedOnce_AndTheEventsBehindItAreNotHeldUp()
    {
        var announcement = _net.AddNodeEndpoints(HubNetwork.Pairing);
        _net.AddFolder("behind");
        var (host, id) = await PairedAsync();
        await using var _ = host;
        using (var conn = Db(host, id).CreateConnection()) await conn.ExecuteAsync("DROP TABLE tbl_peer_endpoint");

        // Four rounds: the event fails cleanly, the cursor stays where it was, the copy tries it again.
        for (var round = 1; round <= SyncEventQuarantine.QuarantineThreshold - 1; round++)
        {
            (await host.For(id).RequestSyncAsync()).Should().Be("Synced.");
            (await CursorAsync(host, id, _net.NodeId)).Should().Be(0, $"round {round}: the event is retried from the same cursor");
            (await QuarantineAsync(host, id)).Single().PermanentFailureCount.Should().Be(round);
        }

        // The fifth failure quarantines it: the cursor passes it and the event behind it is applied in the same round.
        (await host.For(id).RequestSyncAsync()).Should().Be("Synced.");
        (await CursorAsync(host, id, _net.NodeId)).Should().Be(2);
        (await FoldersAsync(host, id)).Should().BeEquivalentTo("behind");
        var entry = (await QuarantineAsync(host, id)).Single();
        entry.EventId.Should().Be(announcement.EventId);
        entry.PermanentFailureCount.Should().Be(SyncEventQuarantine.QuarantineThreshold);

        // And not again: later rounds do not touch it.
        _net.AddFolder("later");
        (await host.For(id).RequestSyncAsync()).Should().Be("Synced.");
        (await FoldersAsync(host, id)).Should().BeEquivalentTo("behind", "later");
        (await QuarantineAsync(host, id)).Single().PermanentFailureCount.Should().Be(SyncEventQuarantine.QuarantineThreshold);
    }

    [Fact]
    public async Task AnAnnouncementThatSpeaksForAnotherNode_IsRefused_NothingIsStored_AndItIsQuarantinedLikeAnyBadEvent()
    {
        var forged = _net.AddForgedNodeEndpoints(Guid.NewGuid(), "https://192.168.1.66:5311");
        _net.AddFolder("behind");
        var (host, id) = await PairedAsync();
        await using var _ = host;

        for (var round = 1; round <= SyncEventQuarantine.QuarantineThreshold; round++) await host.For(id).RequestSyncAsync();

        (await host.Get<PeerEndpointStore>(id).GetAllAsync()).Should().BeEmpty();
        (await QuarantineAsync(host, id)).Single().EventId.Should().Be(forged.EventId);
        (await FoldersAsync(host, id)).Should().BeEquivalentTo("behind");
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------------

    /// <summary>A new app over the same folders and stores: what a restart is.</summary>
    private async Task<(BlindAccountHost Host, string Id)> ReopenAsync(string id)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var host = _rig.Host(handler: _ => () => new ForwardTo(_net), overrides: (services, _) => services.AddSingleton<ILocalNetworks>(FixedNetworks.Home()));
        await host.StartAsync();
        await host.ServicesFor(id).GetRequiredService<BlindStartup>().EnsureReadyAsync();
        return (host, id);
    }

    /// <summary>Spec 14, as the relay test does it: a key row and an activation row, and the policy told.</summary>
    private static async Task ActivateAsync(BlindAccountHost host, string id)
    {
        using (var conn = Db(host, id).CreateConnection())
        {
            var now = DateTime.UtcNow.ToString("O");
            var author = Guid.NewGuid().ToString();
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_metadata_key (key_id, generation, dek_fingerprint, epoch_hint, wrapped, iv, author_node_id, created_at, lamport_ts, event_id)
                  VALUES (@k, 1, @fp, 0, @w, @iv, @a, @now, 1, @e)",
                new { k = new string('c', 32), fp = new string('f', 64), w = new byte[48], iv = RandomNumberGenerator.GetBytes(12), a = author, now, e = Guid.NewGuid().ToString() });
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_metadata_activation (activation_id, key_id, generation, activated_at, author_node_id, event_id, lamport_ts, applied_at)
                  VALUES (@id, @k, 1, @now, @a, @e, 2, @now)",
                new { id = Guid.NewGuid().ToString(), k = new string('c', 32), now, a = author, e = Guid.NewGuid().ToString() });
        }
        host.ServicesFor(id).GetRequiredService<ISyncProtocolPolicy>().MarkActivated();
    }
}
