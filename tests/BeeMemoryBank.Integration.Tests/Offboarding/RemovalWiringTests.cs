using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services.Offboarding;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests.Offboarding;

/// <summary>
/// The removal with the real steps of release 2.12.0 S1 with the identity re-key and the signed request to the shared nodes, S2 with
/// the doors list. Real API hosts, the real sync client, no stand doubles.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class RemovalWiringTests
{
    internal static async Task<NodeIdentity> IdentityAsync(BmbWebApplicationFactory node)
    {
        using var scope = node.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
    }

    internal static async Task<string> FingerprintAsync(BmbWebApplicationFactory node) =>
        NodeStateDraft.IdentityFingerprint((await IdentityAsync(node)).Ed25519PublicKey);

    /// <summary>The node's identity seed as somebody who copied its database and knew its master key would have it.</summary>
    internal static async Task<byte[]> SeedAsync(BmbWebApplicationFactory node)
    {
        var identity = await IdentityAsync(node);
        var dek = node.Services.GetRequiredService<SessionService>().GetMasterDek();
        try
        {
            return BeeMemoryBank.Crypto.NodeIdentityVault.GetDecryptedPrivateKey(identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV,
                identity.Ed25519PrivateKeyV, identity.NodeId, dek);
        }
        finally { Array.Clear(dek); }
    }

    /// <summary>
    /// One round each way (a sync pulls and pushes): every node learns the other's capabilities and every event goes across. Few rounds on purpose:
    /// the sign-in of a sync is rate-limited per caller.
    /// </summary>
    internal static async Task ExchangeAsync(BmbWebApplicationFactory a, BmbWebApplicationFactory b)
    {
        await OffboardingStand.SyncAsync(a, b);
        await OffboardingStand.SyncAsync(b, a);
    }

    internal static async Task<List<NodeRekeyRequestPayload>> RequestsAsync(BmbWebApplicationFactory node)
    {
        using var scope = node.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return (await conn.QueryAsync<string>("SELECT payload FROM tbl_event WHERE event_type = @t", new { t = EventTypes.NodeRekeyRequest }))
            .Select(p => JsonSerializer.Deserialize<NodeRekeyRequestPayload>(p)!).ToList();
    }

    internal static async Task<List<string?>> RotationReasonsAsync(BmbWebApplicationFactory node, Guid author)
    {
        using var scope = node.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return (await conn.QueryAsync<string>("SELECT payload FROM tbl_event WHERE event_type = @t AND node_id = @n COLLATE NOCASE",
                new { t = EventTypes.NodeKeyRotated, n = author.ToString("D") }))
            .Select(p => JsonSerializer.Deserialize<NodeKeyRotatedPayload>(p)!.Reason).ToList();
    }

    /// <summary>The removal runs until S1 is done: the accounts node re-keys itself, the shared node serves the request, the reports come back.</summary>
    internal static async Task RunS1Async(OffboardingStand stand, Guid id)
    {
        for (var round = 0; round < 6; round++)
        {
            await stand.Service().AdvanceAsync(id);
            await ExchangeAsync(stand.Shared!, stand.Accounts);
            await stand.Service(stand.Shared).ServeRekeyRequestsAsync(id);
            await ExchangeAsync(stand.Shared!, stand.Accounts);
            await stand.Service().AdvanceAsync(id);
            if ((await stand.ViewAsync(id)).Steps[1].State == "done") return;
        }
    }

    internal static async Task<OffboardingStand> RealStandAsync()
    {
        var stand = await OffboardingStand.CreateAsync(shared: true, realSteps: true);
        await ExchangeAsync(stand.Shared!, stand.Accounts);
        return stand;
    }

    [Fact]
    public async Task S1_rekeys_the_accounts_node_and_the_shared_node_once_each_for_the_process()
    {
        await using var stand = await RealStandAsync();
        var accountsBefore = await FingerprintAsync(stand.Accounts);
        var sharedBefore = await FingerprintAsync(stand.Shared!);

        var id = await stand.StartRemovalAsync();
        await stand.Service().AdvanceAsync(id);

        // The accounts node re-keyed itself for the process and asked the shared node, in a signed request that names the process.
        var accountsAfter = await FingerprintAsync(stand.Accounts);
        accountsAfter.Should().NotBe(accountsBefore);
        (await RotationReasonsAsync(stand.Accounts, stand.AccountsId)).Should().Equal(EventApplier.RemovalRekeyReason(id));
        var request = (await RequestsAsync(stand.Accounts)).Should().ContainSingle().Subject;
        request.ProcessId.Should().Be(id.ToString("D"));
        request.Nodes.Should().Equal(stand.SharedId.ToString("D"));
        var view = await stand.ViewAsync(id);
        view.Steps[1].State.Should().Be("waiting");
        view.Steps[1].Waits.Single(w => w.NodeId == stand.SharedId).State.Should().Be("waiting");
        view.Steps[1].Waits.Single(w => w.NodeId == stand.AccountsId).State.Should().Be("confirmed");

        // The shared node gets the request and serves it: a new key, for this process, once.
        await ExchangeAsync(stand.Shared!, stand.Accounts);
        await stand.Service(stand.Shared).ServeRekeyRequestsAsync(id);
        var sharedAfter = await FingerprintAsync(stand.Shared!);
        sharedAfter.Should().NotBe(sharedBefore);
        await stand.Service(stand.Shared).ServeRekeyRequestsAsync(id);
        (await FingerprintAsync(stand.Shared!)).Should().Be(sharedAfter, "a request is served once per process");
        (await RotationReasonsAsync(stand.Shared!, stand.SharedId)).Should().Equal(EventApplier.RemovalRekeyReason(id));

        // Its rotation reaches the accounts node: S1 is done, and no pass re-keys anybody again.
        await ExchangeAsync(stand.Shared!, stand.Accounts);
        await stand.Service().AdvanceAsync(id);
        (await stand.ViewAsync(id)).Steps[1].State.Should().Be("done");
        await stand.Service().AdvanceAsync(id);
        (await FingerprintAsync(stand.Accounts)).Should().Be(accountsAfter);
        (await RequestsAsync(stand.Accounts)).Should().ContainSingle("a node already asked is not asked again");
    }

    [Fact]
    public async Task A_rekey_request_from_a_node_that_does_not_drive_the_removal_is_refused_and_rekeys_nobody()
    {
        await using var stand = await RealStandAsync();
        var id = await stand.StartRemovalAsync();
        await stand.Service().AdvanceAsync(id);
        await ExchangeAsync(stand.Shared!, stand.Accounts);

        // The shared node (a superadmin's full node, but not the driver) asks the accounts node for a new key.
        using (var scope = stand.Shared!.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IEventLogger>().LogNodeRekeyRequestAsync(id, [stand.AccountsId]);
        SyncEvent forged;
        using (var scope = stand.Shared!.Services.CreateScope())
        using (var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection())
        {
            var eventId = await conn.ExecuteScalarAsync<string>("SELECT event_id FROM tbl_event WHERE event_type = @t AND node_id = @n COLLATE NOCASE",
                new { t = EventTypes.NodeRekeyRequest, n = stand.SharedId.ToString("D") });
            forged = (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetByIdAsync(eventId))!;
        }
        var before = await FingerprintAsync(stand.Accounts);
        using (var scope = stand.Accounts.Services.CreateScope())
        {
            var apply = () => scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(forged);
            await apply.Should().ThrowAsync<UnauthorizedAccessException>("only the node that drives a removal asks for new keys");
        }
        await stand.Service().AdvanceAsync(id);
        (await FingerprintAsync(stand.Accounts)).Should().Be(before, "the accounts node re-keyed once, for the process it drives, and not for the forged request");
    }

    [Fact]
    public async Task A_shared_node_whose_identity_key_is_kept_outside_its_database_is_named_with_what_to_do_and_never_passed_silently()
    {
        await using var stand = await RealStandAsync();
        var id = await stand.StartRemovalAsync();
        await stand.Service().AdvanceAsync(id);

        // The contributor of such a node says "external" in its node_state (its seed lives in an external key store: the re-key service refuses it).
        await ExchangeAsync(stand.Shared!, stand.Accounts);   // the request is there before the key "moves out"
        using (var scope = stand.Shared!.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            var version = await conn.ExecuteScalarAsync<int>("SELECT ed25519_private_key_v FROM tbl_node_identity");
            await conn.ExecuteAsync("UPDATE tbl_node_identity SET ed25519_private_key_v = 2");
            try
            {
                var draft = new NodeStateDraft();
                await new IdentityRekeyNodeStateContributor(sp.GetRequiredService<INodeIdentityRepository>(), sp.GetRequiredService<IDbConnectionFactory>())
                    .ContributeAsync(draft);
                draft.Extra[NodeStateExtraKeys.IdentityRekey].Should().Be(NodeStateExtraKeys.RekeyExternal);
                // Asked for a new key, such a node does nothing: the re-key refuses it, and nothing pretends otherwise.
                var before = await FingerprintAsync(stand.Shared!);
                await stand.Service(stand.Shared).ServeRekeyRequestsAsync(id);
                (await FingerprintAsync(stand.Shared!)).Should().Be(before);
            }
            finally
            {
                await conn.ExecuteAsync("UPDATE tbl_node_identity SET ed25519_private_key_v = @version", new { version });
            }
        }

        // That report reaches the accounts node: the page names the node with a plain sentence, the step waits, and revoking the node is the way out.
        using (var scope = stand.Accounts.Services.CreateScope())
        using (var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT OR REPLACE INTO tbl_node_state (node_id, app_version, platform, runtime_kind, identity_key_fp, dek_fp, attention_flags_json, extra_json,
                      lamport_ts, event_id, updated_at)
                  VALUES (@n, '2.12.0', 'linux', 'server', 'fp', 'dek', '[]', @x, 999999, NULL, @now)",
                new { n = stand.SharedId.ToString("D"), x = "{\"identity_rekey\":\"external\"}", now = DateTime.UtcNow.ToString("O") });
        await stand.Service().AdvanceAsync(id);
        var view = await stand.ViewAsync(id);
        view.Steps[1].State.Should().Be("waiting");
        var wait = view.Steps[1].Waits.Single(w => w.NodeId == stand.SharedId);
        wait.Why.Should().Be(IdentityRekeyStep.ExternalThere);
        wait.CanRevoke.Should().BeTrue();

        using (var admin = OffboardingStand.ClientAs(stand.Accounts, stand.AdminId))
            (await admin.PostAsync($"/api/offboarding/{id}/nodes/{stand.SharedId}/revoke", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        await stand.Service().AdvanceAsync(id);
        (await stand.ViewAsync(id)).Steps[1].State.Should().Be("done");
    }

    /// <summary>
    /// Toolkit 9 (plan 5): an identity seed of the accounts node and one of a shared node, copied while the person was a superadmin, are useless after
    /// S1: on a peer that applied the re-key, the old seed opens no sync session (challenge-response) and an event it signs is refused.
    /// </summary>
    [Fact]
    public async Task Toolkit_9_a_stolen_identity_seed_of_the_accounts_node_and_of_a_shared_node_is_useless_after_S1()
    {
        await using var stand = await RealStandAsync();
        var stolenAccounts = await SeedAsync(stand.Accounts);
        var stolenShared = await SeedAsync(stand.Shared!);
        (await SignInWithSeedAsync(stand.Shared!, stand.AccountsId, stolenAccounts)).Should().Be(HttpStatusCode.OK, "before S1 the seed is the node's key");

        var id = await stand.StartRemovalAsync();
        await RunS1Async(stand, id);
        (await stand.ViewAsync(id)).Steps[1].State.Should().Be("done");

        // Challenge-response: refused on the other node, which applied the rotation.
        (await SignInWithSeedAsync(stand.Shared!, stand.AccountsId, stolenAccounts)).Should().Be(HttpStatusCode.Unauthorized);
        (await SignInWithSeedAsync(stand.Accounts, stand.SharedId, stolenShared)).Should().Be(HttpStatusCode.Unauthorized);

        // An event the old seed signs now is refused there.
        foreach (var (target, author, seed) in new[] { (stand.Shared!, stand.AccountsId, stolenAccounts), (stand.Accounts, stand.SharedId, stolenShared) })
        {
            using var scope = target.Services.CreateScope();
            var lamport = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxLamportTimestampAsync() + 5;
            var evt = new SyncEvent
            {
                EventId = Guid.NewGuid(), NodeId = author, LamportTs = lamport, EventType = EventTypes.SnapshotCheckpoint,
                Payload = JsonSerializer.Serialize(new { cp = 1 }), ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow, Signature = [],
            };
            evt.EntityId = EventEntityId.Derive(evt);
            evt.Signature = Ed25519Signer.Sign(seed, EventSignature.BuildPayload(evt));
            var apply = () => scope.ServiceProvider.GetRequiredService<EventApplier>().ApplyAsync(evt);
            await apply.Should().ThrowAsync<InvalidDataException>("an event of a retired key, signed after its rotation, is refused");
        }
        Array.Clear(stolenAccounts);
        Array.Clear(stolenShared);
    }

    /// <summary>The challenge-response sign-in of node <paramref name="nodeId"/> at <paramref name="server"/>, signed with <paramref name="seed"/>.</summary>
    internal static async Task<HttpStatusCode> SignInWithSeedAsync(BmbWebApplicationFactory server, Guid nodeId, byte[] seed)
    {
        using var http = server.CreateClient();
        var challenge = await (await http.PostAsync("/api/sync/challenge", null)).Content.ReadFromJsonAsync<JsonElement>();
        var serverNodeId = Guid.Parse(challenge.GetProperty("serverNodeId").GetString()!);
        var challengeB64 = challenge.GetProperty("challenge").GetString()!;
        var payload = "BMB-CHALLENGE-V2\0"u8.ToArray().Concat(serverNodeId.ToByteArray()).Concat(Convert.FromBase64String(challengeB64)).ToArray();
        var auth = await http.PostAsJsonAsync("/api/sync/authenticate", new
        {
            NodeId = nodeId, ChallengeB64 = challengeB64, SignatureB64 = Convert.ToBase64String(Ed25519Signer.Sign(seed, payload)),
            ProtocolVersion = SyncProtocolVersion.Current,
        });
        return auth.StatusCode;
    }

    /// <summary>The accounts node says, with a signed whitelist update, that <paramref name="nodeId"/> is a shared node (what the Devices page does).</summary>
    internal static async Task MakeSharedAsync(BmbWebApplicationFactory accounts, Guid nodeId)
    {
        using var scope = accounts.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var version = await sp.GetRequiredService<IEventLogger>().LogWhitelistUpdateAsync(nodeId, apiAddress: null, displayName: null,
            ownership: OwnershipKinds.Shared);
        var repo = sp.GetRequiredService<IWhitelistRepository>();
        if (await repo.GetByNodeIdAsync(nodeId) is not { } peer) return;   // the node itself: the event tells the others
        peer.Ownership = OwnershipKinds.Shared;
        peer.OwnerPersonId = null;
        peer.OwnerLabel = null;
        peer.NeedsReview = false;
        peer.UpdatedAt = DateTime.UtcNow;
        peer.SetVersionOf(WhitelistConcerns.Ownership, version);
        peer.SetRowVersion(version);
        await repo.UpdateAsync(peer);
    }

    /// <summary>The real stand with the accounts node as accounts home: the shared node holds mirrors of the accounts node's superadmins.</summary>
    internal static async Task<OffboardingStand> SharedDoorsStandAsync(bool setHome = true)
    {
        var stand = await RealStandAsync();
        // Both servers are the organisation's: each says so of the other (a node never sets its own ownership, and a shared node accepts only a
        // shared node as accounts node).
        await MakeSharedAsync(stand.Accounts, stand.SharedId);
        await MakeSharedAsync(stand.Shared!, stand.AccountsId);
        await ExchangeAsync(stand.Shared!, stand.Accounts);
        if (setHome)
        {
            using (var scope = stand.Accounts.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Vault.SharedDoors.AccountsHomeService>().SetAsync(stand.AccountsId, actor: "admin");
            await ExchangeAsync(stand.Shared!, stand.Accounts);
        }
        return stand;
    }

    /// <summary>What a failing doors assertion prints: the shared node's accounts and doors state, and what the accounts node published.</summary>
    internal static async Task<string> DoorsDiagnosticsAsync(OffboardingStand stand)
    {
        var text = new System.Text.StringBuilder();
        foreach (var (name, node) in new[] { ("accounts", stand.Accounts), ("shared", stand.Shared!) })
        {
            using var scope = node.Services.CreateScope();
            using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            var users = await conn.QueryAsync<(string, string, int, int, string, long?)>(
                "SELECT username, role, replicated, is_active, account_state, key_slot_id FROM tbl_user");
            var slots = await conn.QueryAsync<(long, string, long?)>("SELECT slot_id, slot_type, door_version FROM tbl_key_slot");
            var state = await conn.QueryFirstOrDefaultAsync<(string?, long, long, long, string?, long)>(
                "SELECT self_ownership, last_issued_version, latest_received_version, applied_version, last_error, pending_apply FROM tbl_shared_doors_state");
            var home = await conn.ExecuteScalarAsync<string?>("SELECT node_id FROM tbl_accounts_home");
            var caps = await conn.QueryAsync<(string, string?, string?)>("SELECT display_name, ownership, last_capabilities FROM tbl_whitelist");
            text.Append($"[{name}] users={string.Join(";", users)} slots={string.Join(";", slots)} state={state} home={home} wl={string.Join(";", caps)} ");
        }
        return text.ToString();
    }

    internal static async Task<HttpStatusCode> LoginAsync(BmbWebApplicationFactory node, string username, string password)
    {
        using var anonymous = node.CreateClient();
        return (await anonymous.PostAsJsonAsync("/api/session/login", new { username, password })).StatusCode;
    }

    /// <summary>A new device asks <paramref name="node"/> to join with <paramref name="password"/> (the first call of every join).</summary>
    internal static async Task<HttpStatusCode> JoinWithAsync(BmbWebApplicationFactory node, string password)
    {
        using var client = node.CreateClient();
        var (publicKey, seed) = Ed25519Signer.GenerateKeyPair();
        Array.Clear(seed);
        return (await client.PostAsJsonAsync("/api/join", new
        {
            masterPassword = password, nodeId = Guid.NewGuid(), displayName = "A new laptop", ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
            apiAddress = (string?)null, protocolVersion = BeeMemoryBank.Sync.Metadata.EventClasses.SealedProtocol,
        })).StatusCode;
    }

    /// <summary>Runs the removal's passes, with the shared node syncing, serving its request and reporting, until <paramref name="step"/> is done.</summary>
    internal static async Task RunUntilAsync(OffboardingStand stand, Guid id, int step, int rounds = 8)
    {
        for (var round = 0; round < rounds; round++)
        {
            await stand.Service().AdvanceAsync(id);
            await ExchangeAsync(stand.Shared!, stand.Accounts);
            await stand.Service(stand.Shared).ServeRekeyRequestsAsync(id);
            await OffboardingStand.AnnounceAsync(stand.Shared!);
            await ExchangeAsync(stand.Shared!, stand.Accounts);
            await stand.Service().AdvanceAsync(id);
            if ((await stand.ViewAsync(id)).Steps[step].State == "done") return;
        }
    }

    /// <summary>
    /// Toolkit 8 (plan 5): the leaver's old password on a shared node. Before the removal it signs in there (a mirror of the accounts node's list);
    /// after S2 - the list without them applied on the shared node and confirmed in its node_state - it opens no sign-in and no join, while the
    /// administrator who stays still signs in there with their own password.
    /// </summary>
    [Fact]
    public async Task Toolkit_8_the_old_password_on_a_shared_node_opens_no_sign_in_and_no_join_after_S2()
    {
        await using var stand = await SharedDoorsStandAsync();
        (await LoginAsync(stand.Shared!, "leaver", OffboardingStand.LeaverPassword)).Should().Be(HttpStatusCode.OK, "the shared node holds the accounts node's list: "
            + await DoorsDiagnosticsAsync(stand));

        var id = await stand.StartRemovalAsync();
        await RunUntilAsync(stand, id, step: 2);
        var view = await stand.ViewAsync(id);
        view.Steps[2].State.Should().Be("done");
        view.Steps[2].Waits.Single(w => w.NodeId == stand.SharedId).State.Should().Be("confirmed");

        (await LoginAsync(stand.Shared!, "leaver", OffboardingStand.LeaverPassword)).Should().Be(HttpStatusCode.Unauthorized);
        (await JoinWithAsync(stand.Shared!, OffboardingStand.LeaverPassword)).Should().NotBe(HttpStatusCode.OK, "the old password joins no device there");
        (await LoginAsync(stand.Shared!, "admin", OffboardingStand.AdminPassword)).Should().Be(HttpStatusCode.OK, "those who stay notice nothing");
        using (var scope = stand.Shared!.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<IUserRepository>().ListActiveAsync())
                .Should().NotContain(u => u.PersonId == stand.LeaverPersonId, "the list without the person removed their mirror and its door");
    }

    [Fact]
    public async Task S2_waits_with_a_plain_sentence_while_no_accounts_node_is_set_and_publishes_once_this_node_is_it()
    {
        await using var stand = await SharedDoorsStandAsync(setHome: false);
        var id = await stand.StartRemovalAsync();
        await stand.Service().AdvanceAsync(id);
        var view = await stand.ViewAsync(id);
        view.Steps[2].State.Should().NotBe("done");
        view.Steps[2].Sentence.Should().Be(SharedDoorsStep.NoAccountsNode);

        using (var scope = stand.Accounts.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Vault.SharedDoors.AccountsHomeService>().SetAsync(stand.AccountsId, actor: "admin");
        await RunUntilAsync(stand, id, step: 2);
        (await stand.ViewAsync(id)).Steps[2].State.Should().Be("done");
    }

    private static async Task<T> OnAsync<T>(BmbWebApplicationFactory node, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = node.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    private static Task<int> RevokeOnAsync(BmbWebApplicationFactory node, Guid nodeId) => OnAsync(node, async sp =>
    {
        using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return await conn.ExecuteAsync("UPDATE tbl_whitelist SET status = 'R' WHERE node_id = @n COLLATE NOCASE", new { n = nodeId.ToString("D") });
    });

    /// <summary>
    /// The take-over again, now with shared doors in the shared node holds only MIRRORS of the accounts node's list - the administrator's
    /// account there is a mirror. The accounts node is lost and revoked; the administrator takes the removal over on the shared node. Their mirror
    /// becomes an account of their own there (only a node with one may become the accounts node), the shared node becomes the
    /// accounts node, and it runs the removal to its end; the leaver's mirror goes.
    /// </summary>
    [Fact]
    public async Task A_shared_node_with_mirrors_only_takes_a_removal_over_becomes_the_accounts_node_and_finishes_it()
    {
        await using var stand = await SharedDoorsStandAsync();
        var adminHere = await OnAsync(stand.Shared!, async sp => (await sp.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!);
        adminHere.Replicated.Should().BeTrue("the shared node holds the accounts node's list: " + await DoorsDiagnosticsAsync(stand));

        var id = await stand.StartRemovalAsync();
        await stand.Service().AdvanceAsync(id);
        await ExchangeAsync(stand.Shared!, stand.Accounts);
        await RevokeOnAsync(stand.Shared!, stand.AccountsId);
        (await stand.ViewAsync(id, stand.Shared)).CanTakeOver.Should().BeTrue();

        await stand.Service(stand.Shared).TakeOverAsync(id, OffboardingStand.AdminPassword, adminHere.Id);

        (await OnAsync(stand.Shared!, async sp => (await sp.GetRequiredService<IAccountsHomeRepository>().GetAsync())!.NodeId))
            .Should().Be(stand.SharedId, "the take-over made this node the accounts node");
        (await OnAsync(stand.Shared!, async sp => (await sp.GetRequiredService<IUserRepository>().GetByIdAsync(adminHere.Id))!.Replicated))
            .Should().BeFalse("the administrator's mirror became an account of their own here");
        (await LoginAsync(stand.Shared!, "admin", OffboardingStand.AdminPassword)).Should().Be(HttpStatusCode.OK);

        for (var round = 0; round < 6 && (await stand.ViewAsync(id, stand.Shared)).Open; round++)
        {
            await stand.Service(stand.Shared).AdvanceAsync(id);
            await stand.Service(stand.Shared).ResolveReviewAsync(id, [], acceptDefaults: true, adminHere.Id);
        }
        var closed = await stand.ViewAsync(id, stand.Shared);
        closed.Open.Should().BeFalse("the shared node finished the removal: " + string.Join(" | ", closed.Steps.Select(st => $"{st.Number}:{st.State}:{st.Sentence}")));
        (await OnAsync(stand.Shared!, async sp => (await sp.GetRequiredService<IUserRepository>().ListActiveAsync()).Count(u => u.PersonId == stand.LeaverPersonId)))
            .Should().Be(0, "the leaver's mirror is gone on the node that finished it");
        (await LoginAsync(stand.Shared!, "leaver", OffboardingStand.LeaverPassword)).Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A blind copy in the stand paired with the accounts node, it holds no master key and no account. It is an S1 row only until its
    /// first sync shows it can follow the new identity key (the accounts node re-keys without waiting for it); no other step
    /// waits for it - not the shared nodes' list (it never gets one), not the rotation - and the removal closes with it still paired.
    /// </summary>
    [Fact]
    public async Task A_paired_blind_copy_is_an_S1_row_until_its_first_sync_and_the_removal_closes_with_it_paired()
    {
        using var blind = new BlindNodeFactory(drainWaitSeconds: 10);
        _ = blind.Services;
        await using var stand = await OffboardingStand.CreateAsync(realSteps: true, blind: blind);
        var blindId = await stand.PairBlindAsync(blind);
        // Until the accounts node has synced with its copy once, it does not know the copy can follow a new identity key: it re-keys without waiting
        //, and S1 lists the copy as one that cannot follow yet.
        var id = await stand.StartRemovalAsync();
        await stand.AcceptReviewAsync(id);
        var first = await stand.ViewAsync(id);
        first.Steps[1].Waits.Single(w => w.NodeId == stand.AccountsId).State.Should().Be("confirmed", "this node re-keyed at once");
        first.Steps[1].Waits.Single(w => w.NodeId == blindId).Why.Should().Contain("cannot follow the new identity key");

        await stand.SyncBlindAsync(blindId);
        for (var round = 0; round < 6 && (await stand.ViewAsync(id)).Open; round++)
        {
            await stand.Service().AdvanceAsync(id);
            await stand.SyncBlindAsync(blindId);
        }

        var view = await stand.ViewAsync(id);
        view.Open.Should().BeFalse("nothing waits for a blind copy once it synced: " + string.Join(" | ", view.Steps.Select(st =>
            $"{st.Number}:{st.State}:{st.Sentence} [{string.Join("; ", st.Waits.Select(w => $"{w.NodeName}={w.State}:{w.Why}"))}]")));
        view.Steps.Where(st => st.Number != OffboardingSteps.Rekey).SelectMany(st => st.Waits).Should().NotContain(w => w.NodeId == blindId);
        view.Steps[1].Waits.Single(w => w.NodeId == blindId).State.Should().Be("confirmed", "it shows it can follow once it synced");
        (await OnAsync(stand.Accounts, async sp => (await sp.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(blindId))!.Status))
            .Should().Be("A", "the blind copy stays paired");
    }

    /// <summary>
    /// "the accounts node changed" is raised on the nodes that received the change, a superadmin acknowledges it there (API, Devices
    /// page), and the node where a superadmin made the change itself clears it by that designation.
    /// </summary>
    [Fact]
    public async Task The_accounts_node_change_is_acknowledged_by_a_superadmin_and_cleared_by_the_home_nodes_own_designation()
    {
        await using var stand = await SharedDoorsStandAsync();
        var adminHere = await OnAsync(stand.Shared!, async sp => (await sp.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!);
        using (var admin = OffboardingStand.ClientAs(stand.Shared!, adminHere.Id))
        {
            var status = await admin.GetFromJsonAsync<JsonElement>("/api/shared-doors/status");
            status.GetProperty("accountsHomeChanged").GetBoolean().Should().BeTrue("the shared node received the designation");
            var ack = await admin.PostAsync("/api/shared-doors/accounts-home-changed/acknowledge", null);
            ack.StatusCode.Should().Be(HttpStatusCode.OK);
            (await admin.GetFromJsonAsync<JsonElement>("/api/shared-doors/status")).GetProperty("accountsHomeChanged").GetBoolean().Should().BeFalse();
        }
        (await OnAsync(stand.Shared!, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_audit_log WHERE action = 'accounts_home_change_acknowledged'");
        })).Should().Be(1);

        // The accounts node made the change: its own (re-)designation clears a flag it carries.
        await OnAsync(stand.Accounts, async sp =>
        {
            await sp.GetRequiredService<ISharedDoorsStateRepository>().MarkAccountsHomeChangedAsync();
            await sp.GetRequiredService<BeeMemoryBank.Vault.SharedDoors.AccountsHomeService>().SetAsync(stand.AccountsId, actor: "admin");
            (await sp.GetRequiredService<ISharedDoorsStateRepository>().GetAsync()).AccountsHomeChanged.Should().BeFalse();
            return 0;
        });
    }

    /// <summary>
    /// Single writer: a node does not take a removal over while the accounts node is another node still in the network - that is
    /// the trace of a take-over done elsewhere; two at once would split the nodes between two accounts nodes.
    /// </summary>
    [Fact]
    public async Task A_take_over_is_refused_while_the_accounts_node_is_another_node_still_in_the_network()
    {
        await using var stand = await SharedDoorsStandAsync();
        var adminHere = await OnAsync(stand.Shared!, async sp => (await sp.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!);
        var id = await stand.StartRemovalAsync();
        await stand.Service().AdvanceAsync(id);
        await ExchangeAsync(stand.Shared!, stand.Accounts);
        await RevokeOnAsync(stand.Shared!, stand.AccountsId);

        // Another superadmin took the accounts node over on a third node, which is still active.
        var third = Guid.NewGuid();
        await OnAsync(stand.Shared!, async sp =>
        {
            var (key, seed) = Ed25519Signer.GenerateKeyPair();
            Array.Clear(seed);
            await sp.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
            {
                NodeId = third, DisplayName = "Third server", Ed25519PublicKey = key, Status = "A", IsSuperadmin = true,
                Ownership = OwnershipKinds.Shared, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteAsync("UPDATE tbl_accounts_home SET node_id = @n", new { n = third.ToString("D") });
        });

        var refused = () => stand.Service(stand.Shared).TakeOverAsync(id, OffboardingStand.AdminPassword, adminHere.Id);
        (await refused.Should().ThrowAsync<OffboardingRefusedException>()).Which.Message.Should().Contain("Third server").And.Contain("Take the removal over there");
        (await stand.ViewAsync(id, stand.Shared)).DrivenHere.Should().BeFalse();
    }
}
