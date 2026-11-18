using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.SharedDoors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// end to end: a shared node that ran 2.10 when the accounts node was designated stored the accounts_home_set and
/// the doors list without applying them (unknown types to it). At the first start of this build the replay applies them - the home set before
/// the doors list, as the log has them - and the accounts node's superadmin signs in on the shared node with the accounts node's password. A
/// second start changes nothing.
/// </summary>
public class LateEventReplayIntegrationTests : IAsyncLifetime
{
    private const string HomePass = "HomeAdmin1";
    private const string SharedPass = "SharedInstaller1";
    private readonly BmbWebApplicationFactory _home = new();
    private readonly BmbWebApplicationFactory _shared = new();
    private HttpClient _homeClient = null!;
    private HttpClient _sharedClient = null!;

    public async Task InitializeAsync()
    {
        _homeClient = _home.CreateClient();
        _sharedClient = _shared.CreateClient();
        await _home.InitializeNodeAsync(displayName: "HomeNode", password: HomePass);
        await _shared.InitializeNodeAsync(displayName: "SharedBackup", password: SharedPass);
        (await _homeClient.PostAsJsonAsync("/api/session/unlock", new { password = HomePass })).EnsureSuccessStatusCode();
        (await _sharedClient.PostAsJsonAsync("/api/session/unlock", new { password = SharedPass })).EnsureSuccessStatusCode();

        using var homeS = _home.Services.CreateScope();
        using var sharedS = _shared.Services.CreateScope();
        var homeId = (await homeS.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var sharedId = (await sharedS.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        foreach (var (wl, peer) in new[] { (homeS.ServiceProvider.GetRequiredService<IWhitelistRepository>(), sharedId),
                     (sharedS.ServiceProvider.GetRequiredService<IWhitelistRepository>(), homeId) })
        {
            await wl.CreateAsync(new WhitelistEntry
            {
                NodeId = peer.NodeId, DisplayName = peer.DisplayName, Ed25519PublicKey = peer.Ed25519PublicKey, Status = WhitelistStatuses.Active,
                IsSuperadmin = true, Ownership = OwnershipKinds.Shared, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await wl.RecordCapabilitiesAsync(peer.NodeId, MetadataCapabilities.Advertised, null, DateTime.UtcNow);
        }
        await sharedS.ServiceProvider.GetRequiredService<ISharedDoorsStateRepository>().SetSelfOwnershipAsync(OwnershipKinds.Shared, 0, homeId.NodeId);
        _home.Services.GetRequiredService<SuperadminDoorsPublisher>().DebounceDelay = TimeSpan.Zero;

        // The accounts node designates itself and publishes its doors list.
        using var req = new HttpRequestMessage(HttpMethod.Put, "/api/accounts-home") { Content = JsonContent.Create(new { nodeId = homeId.NodeId }) };
        req.Headers.Add("X-User-Role", "superadmin");
        req.Headers.Add("X-User-Id", "1");
        (await _homeClient.SendAsync(req)).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _homeClient.Dispose();
        _sharedClient.Dispose();
        _home.Dispose();
        _shared.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// What the 2.10 build of the shared node did with the accounts node's 2.12 events: stored them, applied nothing. The accounts node's
    /// node_state among them (it says there that it holds a superadmin account of its own before it names itself, which a receiver needs for
    /// a designation that is not a hand-over); in the accounts node's log order.
    /// </summary>
    private async Task StoredByTheOlderBuildAsync()
    {
        using var homeS = _home.Services.CreateScope();
        using var sharedS = _shared.Services.CreateScope();
        var homeId = (await homeS.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        var sharedLog = sharedS.ServiceProvider.GetRequiredService<IEventLogRepository>();
        foreach (var evt in (await homeS.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAllAfterSequenceAsync(0, 500))
                 .Where(e => e.EventType is EventTypes.AccountsHomeSet or EventTypes.SuperadminDoorsSet
                             || (e.EventType == EventTypes.NodeState && e.NodeId == homeId)))
            await sharedLog.AppendIfNotExistsAsync(evt, new EventRowLocal());
    }

    /// <summary>A start of this build on the shared node: the stored-event repairs (the replay among them), then the unlocked node's catch-up.</summary>
    private async Task StartTheSharedNodeAsync()
    {
        await StoredEventRepair.RunAsync(_shared.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        using var sharedS = _shared.Services.CreateScope();
        await sharedS.ServiceProvider.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync();
    }

    /// <summary>
    /// a superadmin was removed on the accounts node while the shared node still ran 2.10 - it stored the tombstone and
    /// applied nothing, so the person's account there kept opening the node. At the first start of this build the replay applies the tombstone and
    /// the account is closed; a second start changes nothing.
    /// </summary>
    [Fact]
    public async Task A_removal_that_began_while_the_shared_node_was_old_closes_the_persons_account_there_at_its_first_start()
    {
        int userId;
        Guid person;
        using (var sharedS = _shared.Services.CreateScope())
        {
            var lee = await sharedS.ServiceProvider.GetRequiredService<UserService>().CreateUserAsync("lee", "Lee Leaver", "Lee-Password-1", UserRoles.User);
            (userId, person) = (lee.Id, lee.PersonId);
        }
        SyncEvent tombstone;
        using (var homeS = _home.Services.CreateScope())
        {
            (await homeS.ServiceProvider.GetRequiredService<IEventLogger>().LogPersonRemovedAsync(person, "deleted", DateTime.UtcNow, Guid.NewGuid()))
                .Should().BeTrue();
            tombstone = (await homeS.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAllAfterSequenceAsync(0, 500))
                .Single(e => e.EventType == EventTypes.PersonRemoved);
        }
        using (var sharedS = _shared.Services.CreateScope())
            await sharedS.ServiceProvider.GetRequiredService<IEventLogRepository>().AppendIfNotExistsAsync(tombstone, new EventRowLocal());
        (await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "lee", password = "Lee-Password-1" }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "precondition: the old build applied nothing, the account still opens the node");

        await StartTheSharedNodeAsync();

        using (var sharedS = _shared.Services.CreateScope())
        {
            var account = await sharedS.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId);
            (account is { IsActive: true }).Should().BeFalse("the removed person's account is closed at the first start");
        }
        (await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "lee", password = "Lee-Password-1" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the tombstone stored by the older build applies at the first start");

        await StartTheSharedNodeAsync();
        (await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "lee", password = "Lee-Password-1" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_accounts_node_designated_while_the_shared_node_was_old_is_learned_at_its_first_start_and_its_list_applies()
    {
        await StoredByTheOlderBuildAsync();
        using (var sharedS = _shared.Services.CreateScope())
            (await sharedS.ServiceProvider.GetRequiredService<IAccountsHomeRepository>().GetAsync()).Should().BeNull("precondition: never applied");
        (await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = HomePass }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "precondition: the shared node still has its own accounts");

        await StartTheSharedNodeAsync();

        var homeId = (await _home.Services.CreateScope().ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        long applied;
        using (var sharedS = _shared.Services.CreateScope())
        {
            (await sharedS.ServiceProvider.GetRequiredService<IAccountsHomeRepository>().GetAsync())!.NodeId.Should().Be(homeId);
            applied = (await sharedS.ServiceProvider.GetRequiredService<ISharedDoorsStateRepository>().GetAsync()).AppliedVersion;
            applied.Should().BeGreaterThan(0, "the doors list applies once its accounts node is known");
        }
        (await _sharedClient.PostAsJsonAsync("/api/session/login", new { username = "admin", password = HomePass }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "the accounts node's superadmin signs in with the accounts node's password");

        await StartTheSharedNodeAsync();
        using (var sharedS = _shared.Services.CreateScope())
        {
            (await sharedS.ServiceProvider.GetRequiredService<IAccountsHomeRepository>().GetAsync())!.NodeId.Should().Be(homeId);
            (await sharedS.ServiceProvider.GetRequiredService<ISharedDoorsStateRepository>().GetAsync()).AppliedVersion.Should().Be(applied,
                "a second start replays nothing");
        }
    }
}
