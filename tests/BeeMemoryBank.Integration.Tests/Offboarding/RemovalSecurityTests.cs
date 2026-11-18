using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services.Offboarding;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Vault.SharedDoors;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The removal against forged-door and related attacks: a forged doors list signed
/// with a stolen accounts-node key, the review's defaults, mixed networks, the accounts node and the take-over.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class RemovalSecurityTests
{
    internal static async Task<T> OnAsync<T>(BmbWebApplicationFactory node, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = node.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    /// <summary>
    /// F1(a), the receiver half: a list signed by the accounts node's CURRENT key replaces one signed by a key it has retired since, whatever their
    /// versions. A forger chose a version no counter can overtake; once the accounts node re-keyed, its next list still wins on the shared node.
    /// </summary>
    [Fact]
    public async Task A_list_signed_after_the_accounts_node_rekeyed_replaces_a_forged_list_whatever_its_version()
    {
        await using var stand = await RemovalWiringTests.SharedDoorsStandAsync();
        var stolen = await RemovalWiringTests.SeedAsync(stand.Accounts);
        var forged = await ForgedDoorsList.BuildAsync(stand.Accounts, stolen, stand.Shared!, long.MaxValue / 2, "backdoor", "Backdoor-Password-1");
        Array.Clear(stolen);
        await ForgedDoorsList.DeliverAsync(stand.Shared!, forged, relayedBy: stand.AccountsId);
        (await RemovalWiringTests.LoginAsync(stand.Shared!, "backdoor", "Backdoor-Password-1")).Should().Be(HttpStatusCode.OK, "the forgery works up to here");

        // The accounts node re-keys (as S1 does); the shared node follows.
        await OnAsync(stand.Accounts, async sp =>
            (await sp.GetRequiredService<NodeIdentityRekeyService>().RekeyAsync("test", "admin")).Done.Should().BeTrue());
        await RemovalWiringTests.ExchangeAsync(stand.Shared!, stand.Accounts);

        // Its next list - with a small version, it has not seen the forged one - replaces the forged list.
        var version = await OnAsync(stand.Accounts, sp => sp.GetRequiredService<SuperadminDoorsPublisher>().PublishForRemovalAsync([]));
        version.Should().BeLessThan(long.MaxValue / 2);
        await RemovalWiringTests.ExchangeAsync(stand.Shared!, stand.Accounts);
        await OnAsync(stand.Shared!, sp => sp.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync());

        (await OnAsync(stand.Shared!, async sp => (await sp.GetRequiredService<ISharedDoorsStateRepository>().GetAsync()).AppliedVersion))
            .Should().Be(version, "the list signed by the current key replaced the forged one");
        (await RemovalWiringTests.LoginAsync(stand.Shared!, "backdoor", "Backdoor-Password-1")).Should().Be(HttpStatusCode.Unauthorized);
        (await RemovalWiringTests.LoginAsync(stand.Shared!, "admin", OffboardingStand.AdminPassword)).Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// F1(c): an event that names this node and carries its signature, but that this node never wrote, arrives from outside. It raises the
    /// "somebody signs as this node" flag, and the identity re-key never lists it as this node's own (so no peer accepts it as the node's after it).
    /// </summary>
    [Fact]
    public async Task An_event_signed_as_this_node_that_it_did_not_write_raises_a_flag_and_is_never_listed_as_its_own()
    {
        await using var stand = await RemovalWiringTests.SharedDoorsStandAsync();
        var stolen = await RemovalWiringTests.SeedAsync(stand.Accounts);
        var forged = await ForgedDoorsList.BuildAsync(stand.Accounts, stolen, stand.Shared!, 1_000_000, "backdoor", "Backdoor-Password-1");
        Array.Clear(stolen);
        await ForgedDoorsList.DeliverAsync(stand.Shared!, forged, relayedBy: stand.AccountsId);
        (await OnAsync(stand.Accounts, sp => SignedAsThisNode.GetAsync(sp.GetRequiredService<IDbConnectionFactory>()))).Should().BeNull();

        // The shared node passes it on to the accounts node, whose name it carries.
        await RemovalWiringTests.ExchangeAsync(stand.Shared!, stand.Accounts);
        var mark = await OnAsync(stand.Accounts, sp => SignedAsThisNode.GetAsync(sp.GetRequiredService<IDbConnectionFactory>()));
        mark.Should().NotBeNull("an event signed with this node's key that it never wrote reached it");
        var flags = await OnAsync(stand.Accounts, async sp =>
        {
            var draft = new NodeStateDraft();
            foreach (var c in sp.GetServices<INodeStateContributor>()) await c.ContributeAsync(draft);
            return draft.AttentionFlags;
        });
        flags.Should().Contain(SignedAsThisNode.Flag);
        (await OnAsync(stand.Accounts, sp => sp.GetRequiredService<IEventLogRepository>().ExistsAsync(forged.EventId))).Should().BeFalse();

        // Even stored (an older build relayed it into the log), it is not listed as the node's own by the re-key.
        await OnAsync(stand.Accounts, async sp =>
        {
            await sp.GetRequiredService<IEventLogRepository>().AppendIfNotExistsAsync(forged,
                new EventRowLocal(OriginPeer: stand.SharedId, OriginSeq: 1, ApplyState: EventApplyStates.Relayed));
            return true;
        });
        var rotation = await OnAsync(stand.Accounts, async sp =>
        {
            var result = await sp.GetRequiredService<NodeIdentityRekeyService>().RekeyAsync("test", "admin");
            result.Done.Should().BeTrue();
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            var payload = await conn.ExecuteScalarAsync<string>("SELECT payload FROM tbl_event WHERE event_type = @t ORDER BY lamport_ts DESC LIMIT 1", new { t = EventTypes.NodeKeyRotated });
            return JsonSerializer.Deserialize<NodeKeyRotatedPayload>(payload!)!;
        });
        EventIdSet.Unpack(rotation.OwnEventIds).Should().NotContain(forged.EventId);
        rotation.CheckpointLamportTs.Should().BeLessThan(forged.LamportTs);
    }

    /// <summary>Runs a removal of the real-step stand with a shared node until the master key changed everywhere (or the rounds run out).</summary>
    internal static async Task<OffboardingView> RunRealAsync(OffboardingStand stand, Guid id, int rounds = 24)
    {
        var view = await stand.ViewAsync(id);
        for (var round = 0; round < rounds && view.Steps[4].State != "done"; round++)
        {
            if (round > 0) await Task.Delay(250);
            await OnAsync(stand.Shared!, async sp => { await sp.GetRequiredService<IDekRotationApplier>().RetryPendingAutoAcceptsAsync(); return true; });
            // The review is decided as soon as it has open items (the page shows it at every step), not only once S1 and S2 are done.
            if (view.Review.Any(i => i.Decision is null))
            {
                await stand.AcceptReviewAsync(id);
                await stand.KeepOpenSuperadminsAsync(id);
            }
            if (view.NeedsPassword) await stand.Service().ContinueAsync(id, OffboardingStand.AdminPassword, stand.AdminId);
            // One exchange a round: the sign-in of a sync is rate-limited per node (30 a minute).
            await stand.Service().AdvanceAsync(id);
            await stand.Service(stand.Shared).ServeRekeyRequestsAsync(id);
            await OnAsync(stand.Shared!, async sp => { await sp.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync(); return true; });
            await OffboardingStand.AnnounceAsync(stand.Shared!);
            await ExchangeRetryingAsync(stand);
            await stand.Service().AdvanceAsync(id);
            view = await stand.ViewAsync(id);
        }
        return view;
    }

    /// <summary>One exchange; a node that applies the rotation by itself closes its vault for a moment (503, retry later), and is asked again.</summary>
    internal static async Task ExchangeRetryingAsync(OffboardingStand stand)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await RemovalWiringTests.ExchangeAsync(stand.Shared!, stand.Accounts);
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < 8)
            {
                await Task.Delay(500);
            }
        }
    }

    /// <summary>
    /// F2(c) acceptance - the join window between S0 and S2: right after the lock-out, before the shared node has heard of it, the person joins a new
    /// device through the shared node with their old password and has it claim to be shared (so no row names them). The removal still lists it,
    /// pre-selected because it joined after the lock-out; decided as pre-selected it is revoked before the master key changes, and the rotation
    /// seals the new key to nobody but the nodes that stay.
    /// </summary>
    [Fact]
    public async Task Acceptance_a_device_joined_through_the_persons_door_between_S0_and_S2_never_gets_the_new_master_key()
    {
        await using var stand = await RemovalWiringTests.SharedDoorsStandAsync();
        var id = await stand.StartRemovalAsync();
        var laptop = Guid.NewGuid();
        using (var client = stand.Shared!.CreateClient())
        {
            var (publicKey, seed) = Ed25519Signer.GenerateKeyPair();
            Array.Clear(seed);
            var joined = await client.PostAsJsonAsync("/api/join", new
            {
                masterPassword = OffboardingStand.LeaverPassword, nodeId = laptop, displayName = "Lee's laptop", ed25519PublicKeyB64 = Convert.ToBase64String(publicKey),
                apiAddress = (string?)null, protocolVersion = BeeMemoryBank.Sync.Metadata.EventClasses.SealedProtocol, ownership = "shared",
            });
            joined.StatusCode.Should().Be(HttpStatusCode.OK, "the window: the shared node has not applied the lock-out yet");
        }

        var view = await RunRealAsync(stand, id);
        string Dump(OffboardingView v) => string.Join(" | ", v.Steps.Select(st => $"{st.Number}:{st.State}:{st.Sentence}"));
        view.Steps[4].State.Should().Be("done", Dump(view));
        var item = view.Review.Single(i => i.Id == $"device:{laptop:D}");
        item.Why.Should().Be("Joined after this removal began.");
        (item.Decision, item.Applied).Should().Be(("remove", true));
        (await OnAsync(stand.Accounts, async sp => (await sp.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(laptop, includeDeleted: true))!.Status))
            .Should().Be("R");
        var commit = await OnAsync(stand.Accounts, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteScalarAsync<string>("SELECT payload FROM tbl_event WHERE event_type = 'dek_rotation_commit' ORDER BY lamport_ts DESC LIMIT 1");
        });
        JsonSerializer.Deserialize<DekRotationCommitPayload>(commit!)!.DekEnvelopes!.Peers.Keys
            .Should().NotContain(k => string.Equals(k, laptop.ToString("D"), StringComparison.OrdinalIgnoreCase), "the new master key is sealed to nodes that stay");
    }

    /// <summary>
    /// F1 end to end (acceptance): somebody with a stolen backup of the accounts node plants a door of their own on a shared node with a forged list of
    /// superadmins that carries a huge version. The removal then runs on the real steps: S1 re-keys the accounts node, S2's list - signed with the new
    /// key - replaces the forgery, the planted door stops working, S2 confirms only the exact list it published, and S5 does not close until the
    /// warnings the forgery left (this node: signed as itself; the shared node: an old key's change nobody listed) are acknowledged.
    /// </summary>
    [Fact]
    public async Task Acceptance_a_forged_list_with_a_huge_version_is_gone_after_the_removal_and_the_close_waits_for_its_warnings()
    {
        await using var stand = await RemovalWiringTests.SharedDoorsStandAsync();
        var stolen = await RemovalWiringTests.SeedAsync(stand.Accounts);
        var forged = await ForgedDoorsList.BuildAsync(stand.Accounts, stolen, stand.Shared!, long.MaxValue / 2, "backdoor", "Backdoor-Password-1");
        Array.Clear(stolen);
        await ForgedDoorsList.DeliverAsync(stand.Shared!, forged, relayedBy: stand.AccountsId);
        (await RemovalWiringTests.LoginAsync(stand.Shared!, "backdoor", "Backdoor-Password-1")).Should().Be(HttpStatusCode.OK, "the forgery works up to here");

        var id = await stand.StartRemovalAsync();
        string Dump(OffboardingView v) => string.Join(" | ", v.Steps.Select(st =>
            $"{st.Number}:{st.State}:{st.Sentence} [{string.Join("; ", st.Waits.Select(w => $"{w.NodeName}={w.State}:{w.Why}"))}]"));
        var view = await RunRealAsync(stand, id);
        view.Steps[2].State.Should().Be("done", Dump(view));
        view.Steps[4].State.Should().Be("done", Dump(view));
        (await RemovalWiringTests.LoginAsync(stand.Shared!, "backdoor", "Backdoor-Password-1")).Should().Be(HttpStatusCode.Unauthorized,
            "the list signed with the accounts node's new key replaced the forged one");
        (await RemovalWiringTests.LoginAsync(stand.Shared!, "leaver", OffboardingStand.LeaverPassword)).Should().Be(HttpStatusCode.Unauthorized);
        (await RemovalWiringTests.LoginAsync(stand.Shared!, "admin", OffboardingStand.AdminPassword)).Should().Be(HttpStatusCode.OK);

        view.Open.Should().BeTrue("the forgery's warnings are not acknowledged yet: " + Dump(view));
        view.Steps[5].Sentence.Should().Contain("signed with its own identity key", "the forged list came back to the accounts node");

        // Acknowledged on both nodes (and the accounts node's designation looked at on the shared node): the removal closes.
        await OnAsync(stand.Accounts, sp => SignedAsThisNode.ClearAsync(sp.GetRequiredService<IDbConnectionFactory>()));
        await OnAsync(stand.Shared!, sp => RetiredKeyAttention.ClearAsync(sp.GetRequiredService<IDbConnectionFactory>(), stand.AccountsId));
        await OnAsync(stand.Shared!, sp => sp.GetRequiredService<AccountsHomeService>().AcknowledgeHomeChangeAsync("admin"));   // F5: looked at there too
        await OffboardingStand.AnnounceAsync(stand.Shared!);
        await ExchangeRetryingAsync(stand);
        await stand.Service().AdvanceAsync(id);
        view = await stand.ViewAsync(id);
        view.Open.Should().BeFalse(Dump(view));
    }

    /// <summary>
    /// F2(a)(b)(d): the review follows the network while the removal runs. A device that becomes the person's after the review was built - what a
    /// join through their door on a node that had not applied the lock-out yet leaves: the replicated row names them as owner - is pre-selected by
    /// that row alone, and a device that appears later is listed too. Decisions already taken stay.
    /// </summary>
    [Fact]
    public async Task The_review_is_rebuilt_while_open_and_pre_selects_every_device_of_the_person()
    {
        await using var stand = await OffboardingStand.CreateAsync(shared: true);
        var id = await stand.StartRemovalAsync();
        await stand.Service().AdvanceAsync(id);
        var review = (await stand.ViewAsync(id)).Review;
        var sharedItem = $"device:{stand.SharedId:D}";
        review.Single(i => i.Id == sharedItem).DefaultRemove.Should().BeFalse("a shared server of the bank");
        var decidedBefore = $"superadmin:{stand.AdminId}";
        await stand.Service().ResolveReviewAsync(id, [new OffboardingDecision(decidedBefore, "keep")], acceptDefaults: false, stand.AdminId);

        // The shared server's row now names the person as owner (no audit of this node says how), and a new device appears.
        await stand.SetOwnershipAsync(stand.SharedId, OwnershipKinds.Personal, stand.LeaverPersonId);
        var (late, lateId) = await stand.AddSharedNodeAsync("Late laptop");
        await using var _ = late;
        await stand.Service().AdvanceAsync(id);

        review = (await stand.ViewAsync(id)).Review;
        var shared = review.Single(i => i.Id == sharedItem);
        shared.DefaultRemove.Should().BeTrue("the replicated row names the person as its owner");
        shared.Why.Should().Be("A device of this person.");
        review.Should().Contain(i => i.Id == $"device:{lateId:D}", "a device that appeared after the review was built is listed");
        review.Single(i => i.Id == decidedBefore).Decision.Should().Be("keep", "a decision taken before the review was rebuilt stays");

        // Decided as pre-selected: the person's device is revoked before the rotation.
        await stand.AcceptReviewAsync(id);
        (await OnAsync(stand.Accounts, async sp => (await sp.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(stand.SharedId, includeDeleted: true))!.Status))
            .Should().Be("R");
    }

    /// <summary>
    /// F2(c): "decide every open item as pre-selected" never decides a superadmin - one promoted before the audit window is not pre-selected, and a
    /// default "keep" would hide it. The deciding administrator's own item is kept (nobody removes themselves); every other superadmin waits for an
    /// explicit decision, and S3 is not done until it has one.
    /// </summary>
    [Fact]
    public async Task Decide_as_pre_selected_never_decides_another_superadmin()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        var other = await OnAsync(stand.Accounts, sp => sp.GetRequiredService<UserService>()
            .CreateUserAsync("puppet", "Old Puppet", "Puppet-Password-1", UserRoles.Superadmin));
        var id = await stand.StartRemovalAsync();
        await stand.AcceptReviewAsync(id);

        var view = await stand.ViewAsync(id);
        view.Review.Single(i => i.Id == $"superadmin:{other.Id}").Decision.Should().BeNull("a superadmin needs an explicit decision");
        view.Review.Single(i => i.Id == $"superadmin:{stand.AdminId}").Decision.Should().Be("keep", "the deciding administrator's own item");
        view.Steps[3].State.Should().NotBe("done");

        await stand.Service().ResolveReviewAsync(id, [new OffboardingDecision($"superadmin:{other.Id}", "keep")], acceptDefaults: false, stand.AdminId);
        (await stand.ViewAsync(id)).Steps[3].State.Should().Be("done");
    }

    /// <summary>
    /// F2(a): S5 sweeps the person's devices once more. A device kept in the review that became the person's afterwards (its replicated row names
    /// them) is revoked at the close, whatever the review decided about it then.
    /// </summary>
    [Fact]
    public async Task S5_revokes_a_device_that_became_the_persons_after_the_review()
    {
        await using var stand = await OffboardingStand.CreateAsync(shared: true);
        var hold = SignedAsThisNode.MarkerPrefix + Guid.NewGuid().ToString("D");
        await SetMarkerAsync(stand.Accounts, hold, on: true);   // keeps the close back until the device changed hands
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        stand.RekeyNode(stand.SharedId);
        stand.SharedDoors.Confirmed[stand.SharedId] = true;
        var view = await DriveAsync(stand, id);
        view.Steps[4].State.Should().Be("done");
        view.Review.Single(i => i.Id == $"device:{stand.SharedId:D}").Decision.Should().Be("keep");

        await stand.SetOwnershipAsync(stand.SharedId, OwnershipKinds.Personal, stand.LeaverPersonId);
        await SetMarkerAsync(stand.Accounts, hold, on: false);
        await stand.Service().AdvanceAsync(id);

        (await stand.ViewAsync(id)).Open.Should().BeFalse();
        (await OnAsync(stand.Accounts, async sp => (await sp.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(stand.SharedId, includeDeleted: true))!.Status))
            .Should().Be("R", "a device of the person does not outlive the removal");
    }

    /// <summary>
    /// F4: a device enrolled through an invitation the person made (a blind copy or a server; invitations default to shared) is pre-selected for
    /// removal with that reason - also after the lock-out marked the person's redeemed invitation refused.
    /// </summary>
    [Fact]
    public async Task A_device_enrolled_through_the_persons_invitation_is_pre_selected_after_the_lock_out_refused_the_invitation()
    {
        await using var stand = await OffboardingStand.CreateAsync(shared: true);
        var invite = Guid.NewGuid().ToString("N");
        await OnAsync(stand.Accounts, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            var now = DateTime.UtcNow;
            return await conn.ExecuteAsync(
                @"INSERT INTO tbl_blind_invite (invite_id, secret_hash, label, source_name, address, trust, created_by, created_at, expires_at, state,
                      redeemed_at, redeemer_node_id)
                  VALUES (@i, 'hash', 'Office box', 'Accounts', 'https://a', 'pin', @by, @t, @e, 'redeemed', @t, @n)",
                new { i = invite, by = stand.LeaverId.ToString(), t = now.ToString("O"), e = now.AddDays(1).ToString("O"), n = stand.SharedId.ToString("D") });
        });

        var id = await stand.StartRemovalAsync();
        await stand.Service().AdvanceAsync(id);

        (await OnAsync(stand.Accounts, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteScalarAsync<string>("SELECT state FROM tbl_blind_invite WHERE invite_id = @i", new { i = invite });
        })).Should().Be("refused", "the lock-out refused the person's redeemed invitation");
        var item = (await stand.ViewAsync(id)).Review.Single(i => i.Id == $"device:{stand.SharedId:D}");
        item.DefaultRemove.Should().BeTrue();
        item.Why.Should().Be("Joined through an invitation this person made.");
    }

    internal static Task SetHomeRowAsync(BmbWebApplicationFactory node, Guid home) => OnAsync(node, async sp =>
    {
        using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return await conn.ExecuteAsync(
            @"INSERT INTO tbl_accounts_home (id, node_id, lamport_ts, updated_at) VALUES (1, @n, 1, @t)
              ON CONFLICT(id) DO UPDATE SET node_id = excluded.node_id",
            new { n = home.ToString("D"), t = DateTime.UtcNow.ToString("O") });
    });

    /// <summary>
    /// F5: S5 does not close while the accounts node is a node this review removed (the shared nodes would take their superadmins from nobody, or
    /// from the person's node), nor while this node says the accounts node changed and nobody looked. Making this node the accounts node, and
    /// acknowledging the change, lets it close.
    /// </summary>
    [Fact]
    public async Task S5_does_not_close_while_the_accounts_node_was_removed_or_its_change_is_unacknowledged()
    {
        await using var stand = await OffboardingStand.CreateAsync(shared: true);
        await SetHomeRowAsync(stand.Accounts, stand.SharedId);
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        await stand.Service().AdvanceAsync(id);
        await stand.Service().ResolveReviewAsync(id, [new OffboardingDecision($"device:{stand.SharedId:D}", "remove")], acceptDefaults: false, stand.AdminId);

        // The shared node is revoked: this node runs on alone.
        var view = await stand.ViewAsync(id);
        for (var round = 0; round < 12 && view.Steps[4].State != "done"; round++)
        {
            if (view.Review.Any(i => i.Decision is null))
            {
                await stand.AcceptReviewAsync(id);
                await stand.KeepOpenSuperadminsAsync(id);
            }
            if (view.NeedsPassword) await stand.Service().ContinueAsync(id, OffboardingStand.AdminPassword, stand.AdminId);
            await stand.Service().AdvanceAsync(id);
            view = await stand.ViewAsync(id);
            if (view.Steps[4].State != "done") await Task.Delay(250);
        }
        view.Steps[4].State.Should().Be("done", string.Join(" | ", view.Steps.Select(st => $"{st.Number}:{st.State}:{st.Sentence}")));
        view.Open.Should().BeTrue("the accounts node was removed in this review");
        view.Steps[5].Sentence.Should().Contain("the accounts node").And.Contain("was removed in this review");

        // This node becomes the accounts node; another node's change arrives, unacknowledged.
        await OnAsync(stand.Accounts, async sp => (await sp.GetRequiredService<AccountsHomeService>().SetAsync(stand.AccountsId, actor: stand.AdminId.ToString())).NodeId);
        await OnAsync(stand.Accounts, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            await conn.ExecuteAsync("INSERT OR IGNORE INTO tbl_shared_doors_state (id) VALUES (1)");
            return await conn.ExecuteAsync("UPDATE tbl_shared_doors_state SET accounts_home_changed = 1");
        });
        // The new accounts node is a new review item (the review follows the network): decided as pre-selected, it is kept.
        await stand.AcceptReviewAsync(id);
        view = await stand.ViewAsync(id);
        view.Review.Single(i => i.Id == $"{OffboardingService.AccountsHomeKind}:{stand.AccountsId:D}").Decision.Should().Be("keep");
        view.Open.Should().BeTrue();
        view.Steps[5].Sentence.Should().Contain("says the accounts node changed").And.NotContain("was removed in this review");

        (await OnAsync(stand.Accounts, sp => sp.GetRequiredService<AccountsHomeService>().AcknowledgeHomeChangeAsync(stand.AdminId.ToString()))).Should().BeTrue();
        await stand.Service().AdvanceAsync(id);
        (await stand.ViewAsync(id)).Open.Should().BeFalse();
    }

    /// <summary>
    /// F5: when the accounts node is another node that the person being removed made the accounts node, S2 never tells the administrator to hand the
    /// accounts node over there: it says to revoke that node and make this one the accounts node.
    /// </summary>
    [Fact]
    public async Task S2_never_sends_the_administrator_to_hand_over_on_an_accounts_node_the_person_set_up()
    {
        await using var stand = await RemovalWiringTests.SharedDoorsStandAsync();
        await SetHomeRowAsync(stand.Accounts, stand.SharedId);
        await OnAsync(stand.Accounts, async sp =>
        {
            await sp.GetRequiredService<IAuditLogRepository>().LogAsync("accounts_home", stand.SharedId.ToString("D"), "accounts_home_set", "web",
                $"Accounts node set to 'Shared server' ({stand.SharedId:D}) by user {stand.LeaverId}");
            return true;
        });
        await stand.StartRemovalAsync();

        var why = await OnAsync(stand.Accounts, sp => sp.GetRequiredService<ISharedDoorsStep>().WhyNotPublishingAsync());
        why.Should().Contain("Do not hand anything over there").And.NotContain("Hand the accounts node over to this node there");
    }

    /// <summary>
    /// Acceptance - a blind copy across the removal's rotation: a copy paired before the removal follows this node's new identity key, takes the
    /// removal's master-key change, and goes on receiving what this node writes after the close.
    /// </summary>
    [Fact]
    public async Task Acceptance_a_blind_copy_keeps_syncing_across_the_removals_rotation()
    {
        using var blind = new BlindNodeFactory(drainWaitSeconds: 10);
        _ = blind.Services;
        await using var stand = await OffboardingStand.CreateAsync(realSteps: true, blind: blind);
        var blindId = await stand.PairBlindAsync(blind);
        await stand.SyncBlindAsync(blindId);

        var id = await stand.StartRemovalAsync();
        await stand.AcceptReviewAsync(id);
        for (var round = 0; round < 8 && (await stand.ViewAsync(id)).Open; round++)
        {
            await stand.Service().AdvanceAsync(id);
            await stand.SyncBlindAsync(blindId);
        }
        (await stand.ViewAsync(id)).Open.Should().BeFalse();

        // After the close this node writes something - signed with its new identity key, sealed under the new master key.
        await OnAsync(stand.Accounts, async sp =>
        {
            var now = DateTime.UtcNow;
            await sp.GetRequiredService<IEventLogger>().LogFolderCreateAsync(new Folder
            {
                Id = Guid.NewGuid(), Path = "/After the removal", Name = "After the removal", CreatedAt = now, UpdatedAt = now,
            });
            return true;
        });
        Task<long> OnAccountsAsync(string sql, object? args = null) => OnAsync(stand.Accounts, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteScalarAsync<long>(sql, args);
        });
        var rotated = await OnAccountsAsync("SELECT MAX(sequence_num) FROM tbl_event WHERE event_type = 'node_key_rotated' AND node_id = @n COLLATE NOCASE",
            new { n = stand.AccountsId.ToString("D") });
        var committed = await OnAccountsAsync("SELECT MAX(sequence_num) FROM tbl_event WHERE event_type = 'dek_rotation_commit' AND json_extract(payload, '$.reason') LIKE 'offboarding:%'");
        var last = await OnAccountsAsync("SELECT MAX(sequence_num) FROM tbl_event");
        Task<long> PushedAsync() => OnAccountsAsync("SELECT COALESCE(MAX(last_pushed_seq), 0) FROM tbl_sync_push_position WHERE remote_node_id = @b COLLATE NOCASE",
            new { b = blindId.ToString("D") });
        for (var round = 0; round < 3 && await PushedAsync() < last; round++)
            await stand.SyncBlindAsync(blindId);

        // The copy took them all: this node's key change, the removal's master-key change, and what came after.
        rotated.Should().BeGreaterThan(0);
        committed.Should().BeGreaterThan(rotated);
        (await PushedAsync()).Should().BeGreaterThanOrEqualTo(last, "the copy accepted every event up to the one written after the close");
        (await OnAsync(stand.Accounts, async sp => (await sp.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(blindId))!.Status)).Should().Be("A");
    }

    /// <summary>
    /// F6: a take-over is refused from a shared node that keeps superadmin doors of its own (it never applied a list: an account of the person not
    /// linked to them may still open it), and by an account the person created - as far as that node's audit log tells.
    /// </summary>
    [Fact]
    public async Task A_take_over_is_refused_from_a_node_with_doors_of_its_own_and_by_an_account_the_person_made()
    {
        await using var stand = await OffboardingStand.CreateAsync(shared: true);
        var id = await stand.StartRemovalAsync();
        await OffboardingStand.SyncAsync(stand.Shared!, stand.Accounts);
        const string Password = "Keeper-Password-1";
        var keeper = await OnAsync(stand.Shared!, async sp =>
            (await sp.GetRequiredService<UserService>().CreateUserAsync("keeper", "Kim Keeper", Password, UserRoles.Superadmin)).Id);
        await OnAsync(stand.Shared!, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteAsync("UPDATE tbl_whitelist SET status = 'R' WHERE node_id = @n COLLATE NOCASE", new { n = stand.AccountsId.ToString("D") });
        });
        var takeOver = () => stand.Service(stand.Shared).TakeOverAsync(id, Password, keeper);
        (await takeOver.Should().ThrowAsync<OffboardingRefusedException>()).Which.Message.Should().Contain("keeps superadmin accounts of its own");

        // The account is a mirror of a list now (no door of its own here) - but an account of the person created it.
        var leeHere = await OnAsync(stand.Shared!, async sp =>
            (await sp.GetRequiredService<UserService>().CreateUserAsync("lee", "Lee here", "Lee-Password-1", UserRoles.User)).Id);
        await OnAsync(stand.Shared!, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            await conn.ExecuteAsync("UPDATE tbl_user SET person_id = @p WHERE id = @l", new { p = stand.LeaverPersonId.ToString("D"), l = leeHere });
            await sp.GetRequiredService<IAuditLogRepository>().LogAsync("user", keeper.ToString(), "user_created", "web", $"User 'keeper' created by user {leeHere}");
            return true;
        });
        await OffboardingProcessTests.AsListMirrorAsync(stand, keeper);
        (await takeOver.Should().ThrowAsync<OffboardingRefusedException>()).Which.Message.Should().Contain("by the person being removed");
    }

    /// <summary>
    /// F3 (mixed network): a device runs a version that cannot follow a new identity key. The ordinary re-key still waits for it; the removal does
    /// not - this node gets its new key at once, so a stolen backup stops opening the updated nodes. S1 then lists the device as one that cannot
    /// follow and waits until it is updated or revoked; S0 says the person is locked out on this node and what is still open, and S1 says what a
    /// stolen backup still does meanwhile.
    /// </summary>
    [Fact]
    public async Task A_removal_rekeys_this_node_despite_a_device_that_cannot_follow_and_waits_for_that_device()
    {
        await using var stand = await OffboardingStand.CreateAsync(realSteps: true, othersDevice: true);
        await OnAsync(stand.Accounts, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteAsync("UPDATE tbl_whitelist SET last_capabilities = 'node-state-v1', last_capabilities_seen_at = @t WHERE node_id = @n COLLATE NOCASE",
                new { t = DateTime.UtcNow.ToString("O"), n = stand.OthersDeviceId.ToString("D") });
        });
        (await OnAsync(stand.Accounts, sp => sp.GetRequiredService<NodeIdentityRekeyService>().RekeyAsync("test", "admin"))).Done
            .Should().BeFalse("the ordinary re-key keeps waiting for every device");

        var before = await RemovalWiringTests.FingerprintAsync(stand.Accounts);
        var id = await stand.StartRemovalAsync();
        await stand.Service().AdvanceAsync(id);
        (await RemovalWiringTests.FingerprintAsync(stand.Accounts)).Should().NotBe(before, "the removal re-keys this node at once");

        var view = await stand.ViewAsync(id);
        view.Steps[0].Sentence.Should().Contain("Locked out on this node").And.Contain("new identity keys (step 2)").And.NotContain("Signed out everywhere");
        view.Steps[1].Sentence.Should().Contain("a stolen backup of this node still opens the network");
        var lagging = view.Steps[1].Waits.Single(w => w.NodeId == stand.OthersDeviceId);
        (lagging.State, lagging.CanRevoke).Should().Be(("waiting", true));
        lagging.Why.Should().Contain("cannot follow the new identity key").And.Contain("Update it").And.Contain("revoke it");
        view.Steps[1].State.Should().NotBe("done");

        await stand.Service().RevokeNodeAsync(id, stand.OthersDeviceId, stand.AdminId);
        await stand.Service().AdvanceAsync(id);
        (await stand.ViewAsync(id)).Steps[1].State.Should().Be("done");
    }

    /// <summary>F2(e): the review names the accounts node and who made it that.</summary>
    [Fact]
    public async Task The_review_names_the_accounts_node_and_who_designated_it()
    {
        await using var stand = await RemovalWiringTests.SharedDoorsStandAsync();
        var id = await stand.StartRemovalAsync();
        await stand.Service().AdvanceAsync(id);
        var home = (await stand.ViewAsync(id)).Review.Single(i => i.Kind == OffboardingService.AccountsHomeKind);
        home.Label.Should().Be("Accounts node: this node");
        home.Why.Should().Contain("Made the accounts node");
        home.DefaultRemove.Should().BeFalse("this node, the one that runs the removal");
        Func<Task> remove = () => stand.Service().ResolveReviewAsync(id, [new OffboardingDecision(home.Id, "remove")], acceptDefaults: false, stand.AdminId);
        await remove.Should().ThrowAsync<OffboardingRefusedException>().WithMessage("*not removed in the review*");
    }

    internal static Task SetMarkerAsync(BmbWebApplicationFactory node, string key, bool on) => OnAsync(node, async sp =>
    {
        using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return on
            ? await conn.ExecuteAsync("INSERT OR REPLACE INTO tbl_migration_marker (key, value, set_at) VALUES (@k, @v, @now)",
                new { k = key, v = key.StartsWith(SignedAsThisNode.MarkerPrefix) ? SignedAsThisNode.Open : "1", now = DateTime.UtcNow.ToString("O") })
            : await conn.ExecuteAsync("DELETE FROM tbl_migration_marker WHERE key = @k", new { k = key });
    });

    /// <summary>Drives a removal of the fake-step stand as far as it goes: the review as pre-selected, the password for the rotation, reports.</summary>
    private static async Task<OffboardingView> DriveAsync(OffboardingStand stand, Guid id, int rounds = 12)
    {
        var view = await stand.ViewAsync(id);
        for (var round = 0; round < rounds && view.Open && view.Steps[4].State != "done"; round++)
        {
            if (view.Step == OffboardingSteps.Review) await stand.AcceptReviewAsync(id);
            if (view.NeedsPassword) await stand.Service().ContinueAsync(id, OffboardingStand.AdminPassword, stand.AdminId);
            await OffboardingStand.SyncAsync(stand.Shared!, stand.Accounts);
            await OffboardingStand.AnnounceAsync(stand.Shared!);
            await OffboardingStand.SyncAsync(stand.Shared!, stand.Accounts);
            await stand.Service().AdvanceAsync(id);
            view = await stand.ViewAsync(id);
            if (view.Open) await Task.Delay(250);
        }
        return view;
    }

    /// <summary>
    /// F1(d): S5 does not close while a node reports something the removal exists to end - here the shared node took events signed by a retired
    /// key that nobody acknowledged, and this node received events signed as itself. The page says what to do; once both are acknowledged, it closes.
    /// </summary>
    [Fact]
    public async Task S5_does_not_close_while_a_node_reports_a_retired_key_or_a_signed_as_itself_warning()
    {
        await using var stand = await OffboardingStand.CreateAsync(shared: true);
        var retired = EventApplier.RetiredKeyAttentionMarkerPrefix + Guid.NewGuid().ToString("D");
        await SetMarkerAsync(stand.Shared!, retired, on: true);
        var signedAs = SignedAsThisNode.MarkerPrefix + Guid.NewGuid().ToString("D");
        await SetMarkerAsync(stand.Accounts, signedAs, on: true);
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        stand.RekeyNode(stand.SharedId);
        stand.SharedDoors.Confirmed[stand.SharedId] = true;

        var view = await DriveAsync(stand, id);
        view.Steps[4].State.Should().Be("done", string.Join(" | ", view.Steps.Select(st => $"{st.Number}:{st.State}:{st.Sentence}")));
        view.Open.Should().BeTrue("a removal does not close over such a warning");
        view.Steps[5].State.Should().Be("waiting");
        view.Steps[5].Sentence.Should().Contain("old identity key").And.Contain("signed with its own identity key").And.Contain("Node identity page");

        // Both acknowledged (the shared node reports it at its next node_state): the removal closes.
        await SetMarkerAsync(stand.Shared!, retired, on: false);
        await SetMarkerAsync(stand.Accounts, signedAs, on: false);
        await OffboardingStand.AnnounceAsync(stand.Shared!);
        await OffboardingStand.SyncAsync(stand.Shared!, stand.Accounts);
        await stand.Service().AdvanceAsync(id);
        (await stand.ViewAsync(id)).Open.Should().BeFalse();
    }

    internal static Task<int> ReportOnAsync(BmbWebApplicationFactory accounts, Guid node, string extraJson) => OnAsync(accounts, async sp =>
    {
        using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return await conn.ExecuteAsync(
            @"INSERT OR REPLACE INTO tbl_node_state (node_id, app_version, platform, runtime_kind, identity_key_fp, dek_fp, attention_flags_json, extra_json,
                  lamport_ts, event_id, updated_at)
              VALUES (@n, '2.12.0', 'test', 'server', '', 'dek', '[]', @x, 999999999, NULL, @now)",
            new { n = node.ToString("D"), x = extraJson, now = DateTime.UtcNow.ToString("O") });
    });

    /// <summary>
    /// F1(b): S2 confirms a shared node only when it reports the EXACT list S2 published (its event), no superadmin door of its own and no mirror of
    /// anybody being removed. A report of a higher version - what a forged list with a huge version produces - confirms nothing.
    /// </summary>
    [Fact]
    public async Task S2_confirms_only_the_exact_list_it_published_and_no_mirror_of_the_person()
    {
        await using var stand = await RemovalWiringTests.SharedDoorsStandAsync();
        var id = await stand.StartRemovalAsync();
        await RemovalWiringTests.RunUntilAsync(stand, id, step: 2);
        (await stand.ViewAsync(id)).Steps[2].State.Should().Be("done", "the real list, applied and reported, confirms");

        var version = await OnAsync(stand.Accounts, async sp =>
            (await new OffboardingStore(sp.GetRequiredService<IDbConnectionFactory>()).GetAsync(id))!.Progress.S2ListVersion);
        var exact = await OnAsync(stand.Accounts, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteScalarAsync<string>(
                "SELECT event_id FROM tbl_event WHERE event_type = 'superadmin_doors_set' AND CAST(json_extract(payload, '$.version') AS INTEGER) = @v",
                new { v = version });
        });
        Task<OffboardingNodeReport> ReportAsync() => OnAsync(stand.Accounts, sp => sp.GetRequiredService<ISharedDoorsStep>().ReportAsync(stand.SharedId, version));

        // A higher version, no door of its own - what a forged list leaves - is not the published list.
        await ReportOnAsync(stand.Accounts, stand.SharedId,
            $"{{\"applied_doors_version\":\"9000000000\",\"local_superadmin_door_count\":\"0\",\"applied_doors_event\":\"{Guid.NewGuid()}\",\"removed_mirrors\":\"0\"}}");
        var forgedReport = await ReportAsync();
        forgedReport.Confirmed.Should().BeFalse("a version at least as high is not the list S2 published");
        forgedReport.Waiting.Should().Contain("that this node did not write");

        // The exact list, but the person's mirror is still there.
        await ReportOnAsync(stand.Accounts, stand.SharedId,
            $"{{\"applied_doors_version\":\"{version}\",\"local_superadmin_door_count\":\"0\",\"applied_doors_event\":\"{exact}\",\"removed_mirrors\":\"1\"}}");
        (await ReportAsync()).Confirmed.Should().BeFalse("the account of the person being removed is still there");

        // The exact list, nothing left: confirmed.
        await ReportOnAsync(stand.Accounts, stand.SharedId,
            $"{{\"applied_doors_version\":\"{version}\",\"local_superadmin_door_count\":\"0\",\"applied_doors_event\":\"{exact}\",\"removed_mirrors\":\"0\"}}");
        (await ReportAsync()).Confirmed.Should().BeTrue();
    }
}
