extern alias WebApp;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ApiClient = WebApp::BeeMemoryBank.Web.Services.ApiClient;
using DevicesModel = WebApp::BeeMemoryBank.Web.Pages.Admin.DevicesModel;
using DeviceModel = WebApp::BeeMemoryBank.Web.Pages.Admin.DeviceModel;
using NodeLanClient = WebApp::BeeMemoryBank.Web.Services.NodeLanClient;

namespace BeeMemoryBank.Integration.Tests;

public class DeviceOwnershipPageAndApiTests : IAsyncLifetime
{
    private const string Password = "deviceOwnershipPage1";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private BmbWebApplicationFactory _api = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _api = new BmbWebApplicationFactory();
        _client = _api.CreateClient();
        await _api.InitializeNodeAsync("OwnershipNode", Password);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _api.Dispose();
        return Task.CompletedTask;
    }

    private sealed class DevicesPage(ApiClient api) : DevicesModel(api, null!, null!)
    {
        protected override NodeLanClient Client =>
            new(new HttpClient(), dataPath: Path.Combine(Path.GetTempPath(), "bmb_no_front_" + Guid.NewGuid().ToString("N")), internalKey: "k");
    }

    private async Task<(Guid NodeId, byte[] PrivKey, byte[] PubKey)> AddPeerAsync(
        string name,
        Guid? fixedId = null,
        string ownership = OwnershipKinds.Unknown,
        bool needsReview = true)
    {
        var id = fixedId ?? Guid.NewGuid();
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        using var scope = _api.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        await repo.CreateAsync(new WhitelistEntry
        {
            NodeId = id,
            DisplayName = name,
            Ed25519PublicKey = pub,
            Status = WhitelistStatuses.Active,
            Ownership = ownership,
            NeedsReview = needsReview,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        return (id, priv, pub);
    }

    [Fact]
    public async Task SetOwnership_RefusesSelfNode_AndPublishesUpdateForPeers()
    {
        using var scope0 = _api.Services.CreateScope();
        var selfId = (await scope0.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        var users = await _client.GetFromJsonAsync<JsonElement>("/api/users", WebJson);
        var ownerPersonId = users.EnumerateArray().First().GetProperty("personId").GetGuid();

        var selfResp = await _client.PutAsJsonAsync($"/api/whitelist/{selfId}/ownership", new { ownership = "shared" });
        selfResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var (peerId, _, _) = await AddPeerAsync("MacBook");
        var head = await scope0.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();

        var okResp = await _client.PutAsJsonAsync($"/api/whitelist/{peerId}/ownership",
            new { ownership = "personal", ownerPersonId });
        okResp.StatusCode.Should().Be(HttpStatusCode.OK, await okResp.Content.ReadAsStringAsync());

        var body = await okResp.Content.ReadFromJsonAsync<JsonElement>(WebJson);
        body.GetProperty("ownership").GetString().Should().Be("personal");
        body.GetProperty("ownerPersonId").GetGuid().Should().Be(ownerPersonId);
        body.GetProperty("needsReview").GetBoolean().Should().BeFalse();

        using var scope = _api.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(peerId);
        row!.Ownership.Should().Be(OwnershipKinds.Personal);
        row.NeedsReview.Should().BeFalse();

        var events = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(head);
        events.Should().Contain(e => e.EventType == EventTypes.WhitelistUpdate && e.Payload.Contains(peerId.ToString("D")));
    }

    [Fact]
    public async Task ReviewSuggestions_FollowRuntimeKindAndBlindRules_AndConfirmAllClearsNeedsReview()
    {
        var blind = await AddPeerAsync("BlindOffsite", BlindNodeId.NewId());
        var serverPeer = await AddPeerAsync("HomeServer");
        var desktopPeer = await AddPeerAsync("DesktopPC");

        // Record node_state for serverPeer (runtime_kind = server -> suggests shared) and desktopPeer (desktop -> suggests first superadmin)
        using (var scope = _api.Services.CreateScope())
        {
            var applier = scope.ServiceProvider.GetRequiredService<EventApplier>();
            var serverEvt = new SyncEvent
            {
                EventId = Guid.NewGuid(),
                NodeId = serverPeer.NodeId,
                LamportTs = 10,
                EventType = EventTypes.NodeState,
                Payload = JsonSerializer.Serialize(new NodeStatePayload(
                    serverPeer.NodeId.ToString("D"), "2.12.0", "linux", NodeRuntimeKinds.Server,
                    NodeStateDraft.IdentityFingerprint(serverPeer.PubKey), null)),
                ProtocolVersion = SyncProtocolVersion.Current,
                CreatedAt = DateTime.UtcNow,
                Signature = []
            };
            serverEvt.Signature = Ed25519Signer.Sign(serverPeer.PrivKey, EventSignature.BuildPayload(serverEvt));
            await applier.ApplyAsync(serverEvt);

            var desktopEvt = new SyncEvent
            {
                EventId = Guid.NewGuid(),
                NodeId = desktopPeer.NodeId,
                LamportTs = 10,
                EventType = EventTypes.NodeState,
                Payload = JsonSerializer.Serialize(new NodeStatePayload(
                    desktopPeer.NodeId.ToString("D"), "2.12.0", "windows", NodeRuntimeKinds.Desktop,
                    NodeStateDraft.IdentityFingerprint(desktopPeer.PubKey), null)),
                ProtocolVersion = SyncProtocolVersion.Current,
                CreatedAt = DateTime.UtcNow,
                Signature = []
            };
            desktopEvt.Signature = Ed25519Signer.Sign(desktopPeer.PrivKey, EventSignature.BuildPayload(desktopEvt));
            await applier.ApplyAsync(desktopEvt);
        }

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/whitelist", WebJson);
        var items = list.EnumerateArray().ToDictionary(x => x.GetProperty("nodeId").GetGuid());
        items[blind.NodeId].GetProperty("suggestedOwnership").GetString().Should().Be("shared");
        items[serverPeer.NodeId].GetProperty("suggestedOwnership").GetString().Should().Be("shared");
        items[desktopPeer.NodeId].GetProperty("suggestedOwnership").GetString().Should().Be("personal");
        items[desktopPeer.NodeId].GetProperty("suggestedOwnerPersonId").GetGuid().Should().NotBe(Guid.Empty);

        var confirmAll = await _client.PostAsJsonAsync("/api/whitelist/review-ownership", new { confirmAll = true });
        confirmAll.StatusCode.Should().Be(HttpStatusCode.OK, await confirmAll.Content.ReadAsStringAsync());

        using var verifyScope = _api.Services.CreateScope();
        var wl = verifyScope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        (await wl.GetByNodeIdAsync(serverPeer.NodeId))!.Ownership.Should().Be(OwnershipKinds.Shared);
        (await wl.GetByNodeIdAsync(serverPeer.NodeId))!.NeedsReview.Should().BeFalse();
        (await wl.GetByNodeIdAsync(desktopPeer.NodeId))!.Ownership.Should().Be(OwnershipKinds.Personal);
        (await wl.GetByNodeIdAsync(desktopPeer.NodeId))!.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task DevicesPage_SingleSuperadminNeverNagged_MultiUserShowsReviewAndDevicePageUpdates()
    {
        var peer = await AddPeerAsync("Pixel9");

        // Single superadmin (1 user): ShowOwnershipReviewCard stays false
        var api = new ApiClient(_client);
        var page1 = new DevicesPage(api);
        await page1.OnGetAsync(CancellationToken.None);
        page1.IsSingleSuperadminBank.Should().BeTrue();
        page1.ShowOwnershipReviewCard.Should().BeFalse();

        // Add multiple ordinary users (still only ONE superadmin): review card must remain hidden
        using (var scope = _api.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            await users.CreateAsync(new User
            {
                Username = "bob",
                DisplayName = "Bob",
                Role = "user",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
            await users.CreateAsync(new User
            {
                Username = "carol",
                DisplayName = "Carol",
                Role = "user",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
        }

        var pageWithOrdinaryUsers = new DevicesPage(api);
        await pageWithOrdinaryUsers.OnGetAsync(CancellationToken.None);
        pageWithOrdinaryUsers.IsSingleSuperadminBank.Should().BeTrue();
        pageWithOrdinaryUsers.OwnershipReviewEntries.Should().Contain(e => e.NodeId == peer.NodeId);
        pageWithOrdinaryUsers.ShowOwnershipReviewCard.Should().BeFalse("one superadmin with ordinary users must not be nagged");

        // Add a second superadmin -> ShowOwnershipReviewCard turns true
        using (var scope = _api.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            await users.CreateAsync(new User
            {
                Username = "alice",
                DisplayName = "Alice",
                Role = UserRoles.Superadmin,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
        }

        var page2 = new DevicesPage(api);
        await page2.OnGetAsync(CancellationToken.None);
        page2.IsSingleSuperadminBank.Should().BeFalse();
        page2.ShowOwnershipReviewCard.Should().BeTrue();
        page2.OwnershipReviewEntries.Should().Contain(e => e.NodeId == peer.NodeId);

        // DeviceModel change ownership form handler
        var bobPersonId = page2.Users.Single(u => u.Username == "bob").PersonId;
        var devicePage = new DeviceModel(api) { NodeId = peer.NodeId };
        await devicePage.OnPostSetPeerOwnershipAsync($"personal:{bobPersonId}");

        using (var scope = _api.Services.CreateScope())
        {
            var updated = await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(peer.NodeId);
            updated!.Ownership.Should().Be(OwnershipKinds.Personal);
            updated.OwnerPersonId.Should().Be(bobPersonId);
            updated.OwnerLabel.Should().Be("Bob");
            updated.NeedsReview.Should().BeFalse();
        }
    }

    [Fact]
    public async Task SuggestAndConfirmAll_WithNoActiveSuperadmin_DefaultsToSharedAndNeverFallsBackToRegularUser()
    {
        // Deactivate initial superadmin and add an active ordinary user (zero active superadmins)
        using (var scope = _api.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            foreach (var sa in await users.ListActiveAsync())
            {
                sa.IsActive = false;
                await users.UpdateAsync(sa);
            }
            await users.CreateAsync(new User
            {
                Username = "bob",
                DisplayName = "Bob",
                Role = UserRoles.User,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
            });
        }

        var desktop = await AddPeerAsync("DesktopNoSuperadmin");

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/whitelist", WebJson);
        var row = list.EnumerateArray().Single(x => x.GetProperty("nodeId").GetGuid() == desktop.NodeId);
        row.GetProperty("suggestedOwnership").GetString().Should().Be("shared", "without an active superadmin the suggestion must fall back to shared, never an ordinary user");
        row.GetProperty("suggestedOwnerPersonId").ValueKind.Should().Be(JsonValueKind.Null);

        var confirmAll = await _client.PostAsJsonAsync("/api/whitelist/review-ownership", new { confirmAll = true });
        confirmAll.StatusCode.Should().Be(HttpStatusCode.OK, await confirmAll.Content.ReadAsStringAsync());
        var body = await confirmAll.Content.ReadFromJsonAsync<JsonElement>(WebJson);
        body.GetProperty("updated").GetInt32().Should().Be(1);
        body.GetProperty("skipped").GetArrayLength().Should().Be(0);

        using var verifyScope = _api.Services.CreateScope();
        var wl = verifyScope.ServiceProvider.GetRequiredService<IWhitelistRepository>();
        var saved = (await wl.GetByNodeIdAsync(desktop.NodeId))!;
        saved.Ownership.Should().Be(OwnershipKinds.Shared);
        saved.OwnerPersonId.Should().BeNull();
        saved.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task ConfirmAll_WhenSuggestedOwnerBecomesInactive_ReportsSkippedItemsAndReasons()
    {
        var toggle = new StaleOwnerSwitch();
        using var factory = new StaleOwnerFactory(toggle);
        using var client = factory.CreateClient();
        await factory.InitializeNodeAsync("StaleSaNode", Password);
        (await client.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();

        var peerId = Guid.NewGuid();
        var (pub, _) = Ed25519Signer.GenerateKeyPair();
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = peerId,
                DisplayName = "DesktopDeactivatedSa",
                Ed25519PublicKey = pub,
                Status = WhitelistStatuses.Active,
                Ownership = OwnershipKinds.Unknown,
                NeedsReview = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        toggle.Armed = true;
        var resp = await client.PostAsJsonAsync("/api/whitelist/review-ownership", new { confirmAll = true });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(WebJson);
        body.GetProperty("updated").GetInt32().Should().Be(0);
        body.TryGetProperty("skipped", out var skipped).Should().BeTrue("confirm-all must report skipped items");
        var skippedList = skipped.EnumerateArray().ToList();
        skippedList.Should().ContainSingle(x =>
            x.GetProperty("nodeId").GetGuid() == peerId
            && !string.IsNullOrWhiteSpace(x.GetProperty("reason").GetString()));
    }

    public sealed class StaleOwnerSwitch
    {
        public bool Armed { get; set; }
        public int CallsSinceArmed { get; set; }
    }

    private sealed class StaleOwnerFactory(StaleOwnerSwitch toggle) : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserRepository>();
                services.AddScoped<IUserRepository>(sp =>
                {
                    var inner = new UserRepository((DbConnectionFactory)sp.GetRequiredService<IDbConnectionFactory>());
                    var proxy = DispatchProxy.Create<IUserRepository, StaleOwnerUserRepoProxy>();
                    var impl = (StaleOwnerUserRepoProxy)(object)proxy;
                    impl.Inner = inner;
                    impl.Toggle = toggle;
                    return proxy;
                });
            });
        }
    }

    public class StaleOwnerUserRepoProxy : DispatchProxy
    {
        public IUserRepository Inner { get; set; } = null!;
        public StaleOwnerSwitch Toggle { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Toggle.Armed && targetMethod?.Name == nameof(IUserRepository.ListActiveAsync))
            {
                Toggle.CallsSinceArmed++;
                if (Toggle.CallsSinceArmed > 1)
                    return Task.FromResult(new List<User>());
            }
            return targetMethod!.Invoke(Inner, args);
        }
    }
}
