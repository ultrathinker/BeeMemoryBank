extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.SharedDoors;
using Microsoft.Extensions.DependencyInjection;
using ApiClient = WebApp::BeeMemoryBank.Web.Services.ApiClient;
using DevicesModel = WebApp::BeeMemoryBank.Web.Pages.Admin.DevicesModel;
using NodeLanClient = WebApp::BeeMemoryBank.Web.Services.NodeLanClient;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// on a 3-node in-process network: B designates ITSELF as accounts node. Nobody marked B shared on A or C (a node
/// may not change its own ownership, so the self path cannot publish that mark), yet A and C accept the designation from B - an active,
/// non-blind, whitelisted superadmin full node naming itself - take its doors list, and count B as shared by definition (its ownership row
/// on them is not written). A self-designation by a node that is no superadmin there, or that a superadmin there marked as somebody's personal
/// device, is still refused.
/// </summary>
public class AccountsHomeSelfDesignationTests : IAsyncLifetime
{
    private const string BPass = "AccountsNodeB1";
    private readonly BmbWebApplicationFactory _a = new();
    private readonly BmbWebApplicationFactory _b = new();
    private readonly BmbWebApplicationFactory _c = new();
    private HttpClient _aClient = null!;
    private HttpClient _bClient = null!;
    private HttpClient _cClient = null!;
    private NodeIdentity _aId = null!, _bId = null!, _cId = null!;

    private sealed class DevicesPage(ApiClient api) : DevicesModel(api, null!, null!)
    {
        protected override NodeLanClient Client =>
            new(new HttpClient(), dataPath: Path.Combine(Path.GetTempPath(), "bmb_no_front_" + Guid.NewGuid().ToString("N")), internalKey: "k");
    }

    public async Task InitializeAsync()
    {
        (_aClient, _bClient, _cClient) = (_a.CreateClient(), _b.CreateClient(), _c.CreateClient());
        await _a.InitializeNodeAsync(displayName: "NodeA", password: "NodeAPass1");
        await _b.InitializeNodeAsync(displayName: "NodeB", password: BPass);
        await _c.InitializeNodeAsync(displayName: "NodeC", password: "NodeCPass1");
        foreach (var (client, pass) in new[] { (_aClient, "NodeAPass1"), (_bClient, BPass), (_cClient, "NodeCPass1") })
            (await client.PostAsJsonAsync("/api/session/unlock", new { password = pass })).EnsureSuccessStatusCode();
        _aId = await IdentityAsync(_a);
        _bId = await IdentityAsync(_b);
        _cId = await IdentityAsync(_c);

        // B, the accounts node to be, holds A and C as shared. A and C hold B with no ownership yet (to review, as after an update from 2.10):
        // nobody marked B shared. A and C are shared nodes themselves.
        await TrustAsync(_b, _aId, OwnershipKinds.Shared);
        await TrustAsync(_b, _cId, OwnershipKinds.Shared);
        await TrustAsync(_a, _bId, OwnershipKinds.Unknown, needsReview: true);
        await TrustAsync(_c, _bId, OwnershipKinds.Unknown, needsReview: true);
        foreach (var node in new[] { _a, _c })
            await node.Services.GetRequiredService<ISharedDoorsStateRepository>().SetSelfOwnershipAsync(OwnershipKinds.Shared, 0, _bId.NodeId);
        _b.Services.GetRequiredService<SuperadminDoorsPublisher>().DebounceDelay = TimeSpan.Zero;
    }

    public Task DisposeAsync()
    {
        foreach (var c in new[] { _aClient, _bClient, _cClient }) c.Dispose();
        foreach (var f in new[] { _a, _b, _c }) f.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<NodeIdentity> IdentityAsync(BmbWebApplicationFactory node)
    {
        using var s = node.Services.CreateScope();
        return (await s.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
    }

    private static async Task TrustAsync(BmbWebApplicationFactory on, NodeIdentity peer, string ownership, bool needsReview = false)
    {
        using var s = on.Services.CreateScope();
        var wl = s.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        await wl.CreateAsync(new WhitelistEntry
        {
            NodeId = peer.NodeId, DisplayName = peer.DisplayName, Ed25519PublicKey = peer.Ed25519PublicKey, Status = WhitelistStatuses.Active,
            IsSuperadmin = true, Ownership = ownership, NeedsReview = needsReview,
            OwnerPersonId = ownership == OwnershipKinds.Personal ? Guid.NewGuid() : null,
            OwnerLabel = ownership == OwnershipKinds.Personal ? "Somebody" : null,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await wl.RecordCapabilitiesAsync(peer.NodeId, MetadataCapabilities.Advertised, null, DateTime.UtcNow);
    }

    /// <summary>
    /// B's node_state, accounts_home_set and doors lists, delivered to <paramref name="to"/> in B's log order as sync would; then the unlocked
    /// node's catch-up. The node_state comes first: B says in it that it holds a superadmin account of its own before it names itself, and a
    /// receiver needs that report to accept a designation that is not a hand-over.
    /// </summary>
    private async Task DeliverFromBAsync(BmbWebApplicationFactory to)
    {
        using var bS = _b.Services.CreateScope();
        using var toS = to.Services.CreateScope();
        var applier = toS.ServiceProvider.GetRequiredService<EventApplier>();
        foreach (var evt in (await bS.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAllAfterSequenceAsync(0, 500))
                 .Where(e => e.EventType is EventTypes.AccountsHomeSet or EventTypes.SuperadminDoorsSet
                             || (e.EventType == EventTypes.NodeState && e.NodeId == _bId.NodeId)))
            await applier.ApplyAsync(evt);
        await toS.ServiceProvider.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync();
    }

    private async Task SetBOnCAsync(bool superadmin, string ownership)
    {
        using var s = _c.Services.CreateScope();
        var wl = s.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var row = (await wl.GetByNodeIdAsync(_bId.NodeId))!;
        row.IsSuperadmin = superadmin;
        row.Ownership = ownership;
        row.OwnerPersonId = ownership == OwnershipKinds.Personal ? Guid.NewGuid() : null;
        row.OwnerLabel = ownership == OwnershipKinds.Personal ? "Somebody" : null;
        await wl.UpdateAsync(row);
    }

    private static async Task<Guid?> HomeOnAsync(BmbWebApplicationFactory node)
    {
        using var s = node.Services.CreateScope();
        return (await s.ServiceProvider.GetRequiredService<IAccountsHomeRepository>().GetAsync())?.NodeId;
    }

    private async Task DesignateBAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, "/api/accounts-home") { Content = JsonContent.Create(new { nodeId = _bId.NodeId }) };
        req.Headers.Add("X-User-Role", "superadmin");
        req.Headers.Add("X-User-Id", "1");
        (await _bClient.SendAsync(req)).EnsureSuccessStatusCode();
    }

    /// <summary>C's accounts node of record is <paramref name="home"/> (written as the designation of that node would have), at Lamport time 1.</summary>
    private async Task HomeOfRecordOnCAsync(Guid home)
    {
        using var s = _c.Services.CreateScope();
        await s.ServiceProvider.GetRequiredService<IAccountsHomeRepository>().UpsertAsync(new AccountsHomeRecord(home, 1, home, Guid.NewGuid(), DateTime.UtcNow));
    }

    /// <summary>
    /// in the home-revoked window (C's accounts node of record is revoked on C) B names itself. Nobody marked B shared on C: refused
    /// - otherwise any superadmin full node could crown itself there. Once a superadmin on C marks B shared (a take-over node is a shared node), the
    /// same designation is accepted.
    /// </summary>
    [Fact]
    public async Task In_the_home_revoked_window_a_node_names_itself_only_when_it_is_marked_shared()
    {
        var (lostPub, _) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
        var lost = new NodeIdentity { NodeId = Guid.NewGuid(), DisplayName = "Lost server", Ed25519PublicKey = lostPub };
        await TrustAsync(_c, lost, OwnershipKinds.Shared);
        using (var s = _c.Services.CreateScope())
        {
            var wl = s.ServiceProvider.GetRequiredService<IWhitelistRepository>();
            var row = (await wl.GetByNodeIdAsync(lost.NodeId))!;
            row.Status = WhitelistStatuses.Revoked;
            await wl.UpdateAsync(row);
        }
        await HomeOfRecordOnCAsync(lost.NodeId);
        await DesignateBAsync();

        (await ((Func<Task>)(() => DeliverFromBAsync(_c))).Should().ThrowAsync<UnauthorizedAccessException>())
            .Which.Message.Should().Contain("only a node marked shared here");
        (await HomeOnAsync(_c)).Should().Be(lost.NodeId, "the self-designation of a node nobody marked shared is refused in the window");

        await SetBOnCAsync(superadmin: true, OwnershipKinds.Shared);
        await DeliverFromBAsync(_c);
        (await HomeOnAsync(_c)).Should().Be(_bId.NodeId, "a node marked shared may take over");
    }

    /// <summary>N4, the other way: the accounts node of record naming itself again needs no shared mark.</summary>
    [Fact]
    public async Task The_accounts_node_of_record_names_itself_again_without_a_shared_mark()
    {
        await HomeOfRecordOnCAsync(_bId.NodeId);
        await DesignateBAsync();

        await DeliverFromBAsync(_c);

        (await HomeOnAsync(_c)).Should().Be(_bId.NodeId);
        using var s = _c.Services.CreateScope();
        (await s.ServiceProvider.GetRequiredService<IAccountsHomeRepository>().GetAsync())!.LamportTs.Should().BeGreaterThan(1, "the newer designation applied");
    }

    [Fact]
    public async Task A_node_that_designates_itself_is_accepted_by_every_full_node_and_counts_as_shared_there_by_definition()
    {
        using (var req = new HttpRequestMessage(HttpMethod.Put, "/api/accounts-home") { Content = JsonContent.Create(new { nodeId = _bId.NodeId }) })
        {
            req.Headers.Add("X-User-Role", "superadmin");
            req.Headers.Add("X-User-Id", "1");
            (await _bClient.SendAsync(req)).EnsureSuccessStatusCode();
        }

        // On C, B is first no superadmin node, then marked as somebody's personal device: both times its self-designation is refused.
        await SetBOnCAsync(superadmin: false, OwnershipKinds.Unknown);
        await ((Func<Task>)(() => DeliverFromBAsync(_c))).Should().ThrowAsync<Exception>();
        await SetBOnCAsync(superadmin: true, OwnershipKinds.Personal);
        (await ((Func<Task>)(() => DeliverFromBAsync(_c))).Should().ThrowAsync<UnauthorizedAccessException>())
            .Which.Message.Should().Contain("personal device");
        (await HomeOnAsync(_c)).Should().BeNull();
        await SetBOnCAsync(superadmin: true, OwnershipKinds.Unknown);

        await DeliverFromBAsync(_a);
        await DeliverFromBAsync(_c);

        foreach (var (node, client, name) in new[] { (_a, _aClient, "A"), (_c, _cClient, "C") })
        {
            (await HomeOnAsync(node)).Should().Be(_bId.NodeId, $"{name} accepts B naming itself");
            (await client.PostAsJsonAsync("/api/session/login", new { username = "admin", password = BPass }))
                .StatusCode.Should().Be(HttpStatusCode.OK, $"B's doors list applied on {name}: B's superadmin signs in there with B's password");
        }

        // Shared by definition, not by a write: B's ownership rows on A and C are as they were, and A's review card does not ask about B.
        using (var s = _a.Services.CreateScope())
            (await s.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(_bId.NodeId))!.Ownership.Should().Be(OwnershipKinds.Unknown);
        using (var s = _c.Services.CreateScope())
            (await s.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(_bId.NodeId))!.Ownership.Should().Be(OwnershipKinds.Unknown);
        var page = new DevicesPage(new ApiClient(_aClient));
        await page.OnGetAsync(CancellationToken.None);
        page.OwnershipReviewEntries.Should().NotContain(e => e.NodeId == _bId.NodeId, "the accounts node is shared by definition");
    }
}
