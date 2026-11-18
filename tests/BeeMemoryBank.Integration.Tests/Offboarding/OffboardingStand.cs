using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Api.Services.Offboarding;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests.Offboarding;

/// <summary>
/// The in-process stand of the removal acceptance suites (release 2.12.0, plan 5): an ACCOUNTS node with two superadmins (the administrator who stays
/// and the person who leaves), and on request a SHARED full node of the organisation, the leaver's PERSONAL full node and another superadmin's
/// personal node, every one a real API host joined to the accounts node, synced with the real sync client. Plain by default, sealed on request.
///
/// <para>S1 (identity re-key) and S2 (shared doors) run against <see cref="StandRekeyStep"/> and
/// <see cref="StandSharedDoorsStep"/>: a test re-keys a node by giving it a new key fingerprint and confirms a shared node's list, exactly as the real
/// steps will report through the same interfaces.</para>
/// </summary>
internal sealed class OffboardingStand : IAsyncDisposable
{
    public const string AdminPassword = "Admin-Password-1";
    public const string LeaverPassword = "Leaver-Password-1";
    public const string OtherPassword = "Other-Password-1";

    public BmbWebApplicationFactory Accounts { get; private set; } = null!;
    public BmbWebApplicationFactory? Shared { get; private set; }
    public BmbWebApplicationFactory? Personal { get; private set; }
    public BmbWebApplicationFactory? OthersDevice { get; private set; }

    public int AdminId { get; private set; }
    public int LeaverId { get; private set; }
    public Guid LeaverPersonId { get; private set; }
    public Guid AccountsId { get; private set; }
    public Guid SharedId { get; private set; }
    public Guid PersonalId { get; private set; }
    public Guid OthersDeviceId { get; private set; }

    public StandRekeyStep Rekey { get; } = new();
    public StandSharedDoorsStep SharedDoors { get; } = new();

    private readonly List<BmbWebApplicationFactory> _nodes = [];

    public static async Task<OffboardingStand> CreateAsync(bool shared = false, bool personal = false, bool othersDevice = false, bool sealedAccounts = false,
        bool realSteps = false, Action<IServiceCollection>? othersDeviceServices = null, bool serverAccounts = false, BlindNodeFactory? blind = null)
    {
        var stand = new OffboardingStand();
        stand.Accounts = stand.NewNode(realSteps);
        // A blind copy of the organisation the accounts node reaches it in-process; it is paired by PairBlindAsync.
        if (blind is not null) stand.Accounts.RouteOutboundHttpThrough(blind.Server.CreateHandler());
        if (serverAccounts)
        {
            // A server host with "start unlocked after a restart" available (the real ServerAutoUnlockService).
            var accounts = stand.Accounts;
            var before = accounts.TestServices;
            accounts.TestServices = services =>
            {
                before?.Invoke(services);
                services.Replace(ServiceDescriptor.Singleton(typeof(BeeMemoryBank.Infrastructure.AutoUnlock.HostRole?),
                    (BeeMemoryBank.Infrastructure.AutoUnlock.HostRole?)BeeMemoryBank.Infrastructure.AutoUnlock.HostRole.Server));
                BeeMemoryBank.Infrastructure.AutoUnlock.ServerAutoUnlockServiceCollectionExtensions.AddServerAutoUnlock(services, accounts.DataPath);
            };
        }
        if (sealedAccounts)
            await stand.Accounts.InitializeEncryptedNodeAsync("Accounts", AdminPassword);
        else
        {
            await stand.Accounts.InitializeNodeAsync("Accounts", AdminPassword);
            (await stand.Accounts.Services.GetRequiredService<SessionService>().UnlockAsync(AdminPassword)).Should().BeTrue();
        }

        using (var scope = stand.Accounts.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            stand.AdminId = (await users.GetByUsernameAsync("admin"))!.Id;
            var leaver = await scope.ServiceProvider.GetRequiredService<UserService>()
                .CreateUserAsync("leaver", "Lee Leaver", LeaverPassword, UserRoles.Superadmin, passwordHint: "the usual one");
            stand.LeaverId = leaver.Id;
            stand.LeaverPersonId = leaver.PersonId;
            stand.AccountsId = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
            // The administrator's door is a sealed (v2) door from the set-up; sign in once so it is so on every path.
            (await scope.ServiceProvider.GetRequiredService<UserService>().AuthenticateAsync("admin", AdminPassword)).Should().NotBeNull();
        }
        await stand.UpgradeAdminDoorAsync();

        if (shared)
        {
            (stand.Shared, stand.SharedId) = await stand.JoinAsync("Shared server", realSteps);
            await stand.SetOwnershipAsync(stand.SharedId, OwnershipKinds.Shared, null);
        }
        if (personal)
        {
            (stand.Personal, stand.PersonalId) = await stand.JoinAsync("Lee's laptop", realSteps);
            await stand.SetOwnershipAsync(stand.PersonalId, OwnershipKinds.Personal, stand.LeaverPersonId);
        }
        if (othersDevice)
        {
            (stand.OthersDevice, stand.OthersDeviceId) = await stand.JoinAsync("Admin's phone", realSteps, othersDeviceServices);
            Guid adminPerson;
            using (var scope = stand.Accounts.Services.CreateScope())
                adminPerson = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(stand.AdminId))!.PersonId;
            await stand.SetOwnershipAsync(stand.OthersDeviceId, OwnershipKinds.Personal, adminPerson);
        }
        return stand;
    }

    private BmbWebApplicationFactory NewNode(bool realSteps)
    {
        var node = new BmbWebApplicationFactory();
        if (!realSteps)
            node.TestServices = services =>
            {
                services.AddSingleton<IIdentityRekeyStep>(Rekey);
                services.AddSingleton<ISharedDoorsStep>(SharedDoors);
            };
        _nodes.Add(node);
        return node;
    }

    /// <summary>A password sign-in makes the administrator's door a v2 door (plan A2) when it is not one yet.</summary>
    private async Task UpgradeAdminDoorAsync()
    {
        using var scope = Accounts.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var admin = (await users.GetByIdAsync(AdminId))!;
        await scope.ServiceProvider.GetRequiredService<UserService>().UpgradeKeySlotDoorAsync(admin, AdminPassword);
    }

    private async Task<(BmbWebApplicationFactory Node, Guid Id)> JoinAsync(string name, bool realSteps, Action<IServiceCollection>? extraServices = null)
    {
        var node = NewNode(realSteps);
        if (extraServices is not null)
        {
            var before = node.TestServices;
            node.TestServices = services =>
            {
                before?.Invoke(services);
                extraServices(services);
            };
        }
        using var accountsClient = Accounts.CreateClient();
        await node.JoinNodeAsync(accountsClient, name, AdminPassword);
        (await node.Services.GetRequiredService<SessionService>().UnlockAsync(AdminPassword)).Should().BeTrue();
        using var scope = node.Services.CreateScope();
        var id = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        Rekey.Keys[id] = "fp-" + id.ToString("N")[..8] + "-1";
        return (node, id);
    }

    /// <summary>A shared full node joins now (during a process, for the tests of a wait set that must follow the current shared nodes).</summary>
    public async Task<(BmbWebApplicationFactory Node, Guid Id)> AddSharedNodeAsync(string name)
    {
        var (node, id) = await JoinAsync(name, realSteps: false);
        await SetOwnershipAsync(id, OwnershipKinds.Shared, null);
        return (node, id);
    }

    /// <summary>Pairs <paramref name="blind"/> with the accounts node from a fresh pair code (what the Devices page does); returns its node id.</summary>
    public async Task<Guid> PairBlindAsync(BlindNodeFactory blind)
    {
        using var console = blind.CreateClient();
        var body = await console.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/blind/pair-code");
        var code = BeeMemoryBank.Sync.Blind.BlindPairCode.Parse(body.GetProperty("code").GetString()!);
        using var admin = Accounts.CreateClient();
        var paired = await admin.PostAsJsonAsync("/api/blind-nodes/", new { code = code.ToString() });
        paired.StatusCode.Should().Be(HttpStatusCode.OK, await paired.Content.ReadAsStringAsync());
        return (await blind.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
    }

    /// <summary>One sync round of the accounts node with its blind copy (what its sync loop does; the accounts node learns what the copy can do).</summary>
    public async Task SyncBlindAsync(Guid blindId)
    {
        using var http = Accounts.Services.GetRequiredService<IHttpClientFactory>().CreateClient("SyncScheduler");
        var row = (await Accounts.Services.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(blindId))!;
        using var scope = Accounts.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SyncClient>().SyncWithAsync(http, row.ApiAddress!, blindId);
    }

    /// <summary>The accounts node says whose device a peer is (and the review flag goes), as the Devices page does.</summary>
    public async Task SetOwnershipAsync(Guid nodeId, string ownership, Guid? ownerPerson)
    {
        using var scope = Accounts.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_whitelist SET ownership = @o, owner_person_id = @p, owner_label = @l, needs_review = 0 WHERE node_id = @n COLLATE NOCASE",
            new { o = ownership, p = ownerPerson?.ToString("D"), l = ownerPerson is null ? null : "someone", n = nodeId.ToString("D") });
        await conn.ExecuteAsync("UPDATE tbl_whitelist SET needs_review = 0");
    }

    public OffboardingService Service(BmbWebApplicationFactory? node = null) => (node ?? Accounts).Services.GetRequiredService<OffboardingService>();

    /// <summary>An API client of <paramref name="node"/> acting as the signed-in user <paramref name="userId"/> (the Web forwards the id; the API reads the role).</summary>
    public static HttpClient ClientAs(BmbWebApplicationFactory node, int userId)
    {
        var client = node.CreateClient();
        client.DefaultRequestHeaders.Remove("X-User-Role");
        client.DefaultRequestHeaders.Add("X-User-Id", userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return client;
    }

    /// <summary>Starts the removal of the leaver as the administrator, through the service (the default: delete).</summary>
    public Task<Guid> StartRemovalAsync(string target = "delete", string? role = null, IReadOnlyList<DemotionFolderRule>? rules = null, bool allowOpen = false,
        int? userId = null, int? actor = null, string? password = null) =>
        Service().StartAsync(new OffboardingStartRequest(userId ?? LeaverId, target, role, rules, allowOpen, password ?? AdminPassword), actor ?? AdminId);

    /// <summary>One sync round: <paramref name="puller"/> runs the real sync client against <paramref name="remote"/> (pull and push).</summary>
    public static async Task SyncAsync(BmbWebApplicationFactory puller, BmbWebApplicationFactory remote)
    {
        Guid remoteId;
        using (var scope = remote.Services.CreateScope())
            remoteId = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        using var http = remote.CreateClient();
        using var local = puller.Services.CreateScope();
        await local.ServiceProvider.GetRequiredService<SyncClient>().SyncWithAsync(http, "", remoteId);
    }

    /// <summary>
    /// The challenge-response sign-in of <paramref name="node"/> at <paramref name="server"/> (what the sync client does first). Returns the bearer token,
    /// or null with the status when the server refuses.
    /// </summary>
    public static async Task<(string? Token, HttpStatusCode Status)> AuthenticateAsync(BmbWebApplicationFactory node, BmbWebApplicationFactory server)
    {
        using var http = server.CreateClient();
        var challenge = await (await http.PostAsync("/api/sync/challenge", null)).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var serverNodeId = Guid.Parse(challenge.GetProperty("serverNodeId").GetString()!);
        var challengeB64 = challenge.GetProperty("challenge").GetString()!;
        using var scope = node.Services.CreateScope();
        var identity = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        var payload = "BMB-CHALLENGE-V2\0"u8.ToArray().Concat(serverNodeId.ToByteArray()).Concat(Convert.FromBase64String(challengeB64)).ToArray();
        var dek = scope.ServiceProvider.GetRequiredService<SessionService>().GetMasterDek();
        byte[] signature;
        try
        {
            signature = BeeMemoryBank.Crypto.NodeIdentityVault.SignWithIdentity(identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV,
                identity.Ed25519PrivateKeyV, identity.NodeId, dek, payload);
        }
        finally { Array.Clear(dek); }
        var auth = await http.PostAsJsonAsync("/api/sync/authenticate", new
        {
            NodeId = identity.NodeId, ChallengeB64 = challengeB64, SignatureB64 = Convert.ToBase64String(signature),
            ProtocolVersion = SyncProtocolVersion.Current,
        });
        if (!auth.IsSuccessStatusCode) return (null, auth.StatusCode);
        return ((await auth.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("token").GetString(), auth.StatusCode);
    }

    /// <summary>
    /// Rewrites <paramref name="username"/>'s password door on <paramref name="node"/> as the v1 wrap of an earlier release (same salt, same password):
    /// a superadmin who has not signed in there since 2.12. A rotation then keeps a bridge from the old master key on that node.
    /// </summary>
    public static async Task MakeDoorV1Async(BmbWebApplicationFactory node, string username, string password)
    {
        using var scope = node.Services.CreateScope();
        var sp = scope.ServiceProvider;
        // On a joined node the account row may not point at the door the join wrote: then the node's one password door is that door.
        var slots = await sp.GetRequiredService<IKeySlotRepository>().GetAllAsync();
        var slot = (await sp.GetRequiredService<IUserRepository>().GetByUsernameAsync(username))?.KeySlotId is { } own
            ? slots.Single(s => s.SlotId == own)
            : slots.Single(s => s.SlotType == KeySlotTypes.User);
        var slotId = slot.SlotId;
        var kek = BeeMemoryBank.Crypto.KeyDerivation.DeriveKek(password, slot.Salt!);
        var dek = sp.GetRequiredService<SessionService>().GetMasterDek();
        try
        {
            var (wrapped, iv) = BeeMemoryBank.Crypto.MasterKeyManager.WrapMasterDek(dek, kek);
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            await conn.ExecuteAsync(
                "UPDATE tbl_key_slot SET encrypted_master_dek = @wrapped, iv = @iv, door_version = 1, door_public_key = NULL, dek_fp = NULL WHERE slot_id = @slotId",
                new { wrapped, iv, slotId });
        }
        finally
        {
            Array.Clear(dek);
            Array.Clear(kek);
        }
    }

    /// <summary>The node announces its state now (what the background announcer does every few seconds).</summary>
    public static async Task AnnounceAsync(BmbWebApplicationFactory node)
    {
        // At once, as after an open or a rotation: a test does not wait out the debounce of a changed report.
        // While the node applies a rotation by itself, its identity key and its session key change one after the other: a tick in between cannot sign,
        // and the background announcer logs that and tries again shortly. So does this.
        var announcer = node.Services.GetRequiredService<NodeStateAnnouncer>();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                announcer.RequestImmediateCheck();
                await announcer.TickAsync(DateTime.UtcNow);
                return;
            }
            catch (System.Security.Cryptography.CryptographicException) when (attempt < 10)
            {
                await Task.Delay(200);
            }
        }
    }

    public async Task<OffboardingView> ViewAsync(Guid processId, BmbWebApplicationFactory? node = null) =>
        (await Service(node).GetAsync(processId))!;

    /// <summary>Decides the S3 review as pre-selected and moves on.</summary>
    public async Task AcceptReviewAsync(Guid processId) =>
        await Service().ResolveReviewAsync(processId, [], acceptDefaults: true, AdminId);

    /// <summary>
    /// Keeps every superadmin item still open on the review of <paramref name="node"/> (the accounts node by default), as an administrator who
    /// looked at each one does: "decide as pre-selected" never decides one.
    /// </summary>
    public async Task KeepOpenSuperadminsAsync(Guid processId, BmbWebApplicationFactory? node = null, int? actor = null)
    {
        var open = (await ViewAsync(processId, node)).Review.Where(i => i.Decision is null && OffboardingService.NeedsOwnDecision(i))
            .Select(i => new OffboardingDecision(i.Id, "keep", i.DefaultRemove ? "Looked at it: it stays." : null)).ToList();
        if (open.Count > 0) await Service(node).ResolveReviewAsync(processId, open, acceptDefaults: false, actor ?? AdminId);
    }

    /// <summary>Re-keys a node in the eyes of the stand's S1 step (what the re-key reports).</summary>
    public void RekeyNode(Guid nodeId) => Rekey.Keys[nodeId] = Rekey.Keys.TryGetValue(nodeId, out var old) ? old + "-next" : "fp-new";

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes) node.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>S1 of the stand: available; a test re-keys a node by changing its fingerprint here.</summary>
internal sealed class StandRekeyStep : IIdentityRekeyStep
{
    public ConcurrentDictionary<Guid, string> Keys { get; } = new();
    public ConcurrentBag<(Guid Process, IReadOnlyList<Guid> Shared)> Started { get; } = [];
    public bool Available { get; set; } = true;

    public bool IsAvailable => Available;
    public const string Reason = "This version cannot give a node a new identity key yet.";
    public string? UnavailableReason => Available ? null : Reason;

    public Task StartAsync(Guid processId, IReadOnlyList<Guid> sharedNodes, DateTime notBeforeUtc, CancellationToken ct = default)
    {
        Started.Add((processId, sharedNodes));
        return Task.CompletedTask;
    }

    public Task<string?> CurrentKeyFingerprintAsync(Guid nodeId, CancellationToken ct = default) =>
        Task.FromResult<string?>(Keys.GetOrAdd(nodeId, id => "fp-" + id.ToString("N")[..8] + "-1"));

    /// <summary>The devices a test says cannot follow a new identity key; none unless a test adds one.</summary>
    public ConcurrentDictionary<Guid, bool> CannotFollow { get; } = new();

    public Task<IReadOnlyList<Guid>> CannotFollowAsync(Guid self, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Guid>>(CannotFollow.Keys.Where(k => k != self).ToList());
}

/// <summary>S2 of the stand: available; a test confirms a shared node by adding it to <see cref="Confirmed"/>.</summary>
internal sealed class StandSharedDoorsStep : ISharedDoorsStep
{
    public ConcurrentDictionary<Guid, bool> Confirmed { get; } = new();
    public List<IReadOnlyCollection<Guid>> Published { get; } = [];
    public bool Available { get; set; } = true;

    public bool IsAvailable => Available;
    public const string Reason = "This version cannot hand the shared nodes a new list of superadmins yet.";
    public string? UnavailableReason => Available ? null : Reason;

    public Task<long> PublishWithoutAsync(IReadOnlyCollection<Guid> removedPersonIds, CancellationToken ct = default)
    {
        lock (Published) Published.Add(removedPersonIds);
        return Task.FromResult((long)Published.Count);
    }

    public Task<OffboardingNodeReport> ReportAsync(Guid nodeId, long listVersion, CancellationToken ct = default) =>
        Task.FromResult(new OffboardingNodeReport(nodeId, Confirmed.ContainsKey(nodeId), Confirmed.ContainsKey(nodeId) ? null : "waits for the list"));
}
