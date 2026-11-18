using System.Net;
using System.Text.Json;
using BeeMemoryBank.Api.Services.Offboarding;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Vault.SharedDoors;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests.Offboarding;

/// <summary>
/// The removal against forged-list and rogue-state attacks: a newer genuine list must not
/// wedge S2 (G1), a rogue peer's process state, a removed person's mirror, the accounts node's self-designation window, a device
/// that comes back (G2), and a re-key request that arrives before its process.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class RemovalSecurity2Tests
{
    private static Task<T> OnAsync<T>(BmbWebApplicationFactory node, Func<IServiceProvider, Task<T>> work) => RemovalSecurityTests.OnAsync(node, work);

    private const string KeeperPassword = "Keeper-Password-1";

    /// <summary>A staying superadmin with a sealed door of their own on the accounts node (so they are on the doors list).</summary>
    private static Task<User> KeeperAsync(OffboardingStand stand) => OnAsync(stand.Accounts, sp =>
        sp.GetRequiredService<UserService>().CreateUserAsync("keeper", "Kim Keeper", KeeperPassword, UserRoles.Superadmin, passwordHint: "the usual"));

    private static Task<long> S2VersionAsync(OffboardingStand stand, Guid id) => OnAsync(stand.Accounts, async sp =>
        (await new OffboardingStore(sp.GetRequiredService<IDbConnectionFactory>()).GetAsync(id))!.Progress.S2ListVersion);

    private static Task<long> NewestOwnListAsync(OffboardingStand stand) => OnAsync(stand.Accounts, async sp =>
    {
        using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COALESCE(MAX(CAST(json_extract(payload, '$.version') AS INTEGER)), 0) FROM tbl_event WHERE event_type = 'superadmin_doors_set' AND origin_peer IS NULL");
    });

    private static string Dump(OffboardingView v) => string.Join(" | ", v.Steps.Select(st =>
        $"{st.Number}:{st.State}:{st.Sentence} [{string.Join("; ", st.Waits.Select(w => $"{w.NodeName}={w.State}:{w.Why}"))}]"));

    /// <summary>Runs every given removal of the real-step stand with its shared node until each changed the master key everywhere.</summary>
    private static async Task RunAllAsync(OffboardingStand stand, IReadOnlyList<Guid> ids, int rounds = 24)
    {
        for (var round = 0; round < rounds; round++)
        {
            var views = new List<OffboardingView>();
            foreach (var id in ids) views.Add(await stand.ViewAsync(id));
            if (views.All(v => v.Steps[4].State == "done")) return;
            if (round > 0) await Task.Delay(250);
            await OnAsync(stand.Shared!, async sp => { await sp.GetRequiredService<IDekRotationApplier>().RetryPendingAutoAcceptsAsync(); return true; });
            foreach (var view in views)
            {
                if (!view.Open) continue;
                if (view.Review.Any(i => i.Decision is null))
                {
                    await stand.AcceptReviewAsync(view.ProcessId);
                    await stand.KeepOpenSuperadminsAsync(view.ProcessId);
                }
                if ((await stand.ViewAsync(view.ProcessId)).NeedsPassword)
                    await stand.Service().ContinueAsync(view.ProcessId, OffboardingStand.AdminPassword, stand.AdminId);
                await stand.Service().AdvanceAsync(view.ProcessId);
                await stand.Service(stand.Shared).ServeRekeyRequestsAsync(view.ProcessId);
            }
            await OnAsync(stand.Shared!, async sp => { await sp.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync(); return true; });
            await OffboardingStand.AnnounceAsync(stand.Shared!);
            await RemovalSecurityTests.ExchangeRetryingAsync(stand);
            foreach (var id in ids) await stand.Service().AdvanceAsync(id);
        }
    }

    /// <summary>The close: the shared node's "accounts node changed" looked at there, then every removal closes.</summary>
    private static async Task CloseAllAsync(OffboardingStand stand, IReadOnlyList<Guid> ids)
    {
        await OnAsync(stand.Shared!, sp => sp.GetRequiredService<AccountsHomeService>().AcknowledgeHomeChangeAsync("admin"));
        await OffboardingStand.AnnounceAsync(stand.Shared!);
        await RemovalSecurityTests.ExchangeRetryingAsync(stand);
        foreach (var id in ids) await stand.Service().AdvanceAsync(id);
        foreach (var id in ids)
        {
            var view = await stand.ViewAsync(id);
            view.Open.Should().BeFalse(Dump(view));
        }
    }

    /// <summary>
    /// Found by this round's tests: a genuine identity re-key of the accounts node raised "changes signed by an old identity key" on the shared
    /// node - a recovery box event the accounts node had purged from its own log before it re-keyed (so not on the rotation's list) was still held
    /// there and counted. S5 then refused to close after every removal with a shared node. Guard of the plain re-key (the removal tests below
    /// cover the case that failed).
    /// </summary>
    [Fact]
    public async Task A_genuine_identity_rekey_raises_no_retired_key_warning_on_the_receiver()
    {
        await using var stand = await RemovalWiringTests.SharedDoorsStandAsync();
        (await OnAsync(stand.Accounts, sp => sp.GetRequiredService<NodeIdentityRekeyService>().RekeyAsync("test", "admin"))).Done.Should().BeTrue();
        await RemovalWiringTests.ExchangeAsync(stand.Shared!, stand.Accounts);

        (await OnAsync(stand.Shared!, sp => RetiredKeyAttention.ListAsync(sp.GetRequiredService<IDbConnectionFactory>()))).Should().BeEmpty(
            "the receiver followed the new key and holds no event of the old key that the rotation did not list");
        (await RemovalWiringTests.FingerprintAsync(stand.Accounts)).Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// G1 (blocker): after S2 published its list, a staying superadmin changes their password - the accounts node publishes
    /// a newer list, and the shared node applies that one. It is a list the accounts node wrote, newer than S2's: S2 confirms it and the removal
    /// closes. (Before: S2 waited for the exact list it had published, which the shared node can never go back to - the removal and its master-key
    /// change were wedged for ever.)
    /// </summary>
    [Fact]
    public async Task A_newer_genuine_list_after_S2_published_does_not_wedge_the_removal()
    {
        await using var stand = await RemovalWiringTests.SharedDoorsStandAsync();
        var keeper = await KeeperAsync(stand);
        await RemovalWiringTests.ExchangeAsync(stand.Shared!, stand.Accounts);
        var id = await stand.StartRemovalAsync();
        await stand.Service().AdvanceAsync(id);
        var published = await S2VersionAsync(stand, id);
        published.Should().BeGreaterThan(0, "S2 published its list: " + Dump(await stand.ViewAsync(id)));

        await OnAsync(stand.Accounts, async sp =>
        {
            await sp.GetRequiredService<UserService>().ChangePasswordAsync(keeper.Id, KeeperPassword, "Keeper-Password-2");
            await sp.GetRequiredService<SuperadminDoorsPublisher>().PublishIfDueAsync();
            return true;
        });
        (await NewestOwnListAsync(stand)).Should().BeGreaterThan(published, "the password change published a newer list");

        await RunAllAsync(stand, [id]);
        var view = await stand.ViewAsync(id);
        view.Steps[2].State.Should().Be("done", Dump(view));
        view.Steps[4].State.Should().Be("done", Dump(view));
        await CloseAllAsync(stand, [id]);
        (await RemovalWiringTests.LoginAsync(stand.Shared!, "keeper", "Keeper-Password-2")).Should().Be(HttpStatusCode.OK,
            "the shared node holds the newer list");
    }

    /// <summary>
    /// G1 with a second removal in parallel (plan E5: removals share S1, S4 and S5): the second removal's S2 publishes a newer list without both
    /// people; the first removal confirms it as well, and both close.
    /// </summary>
    [Fact]
    public async Task A_second_removal_in_parallel_does_not_wedge_the_first()
    {
        await using var stand = await RemovalWiringTests.SharedDoorsStandAsync();
        var keeper = await KeeperAsync(stand);
        await RemovalWiringTests.ExchangeAsync(stand.Shared!, stand.Accounts);
        var first = await stand.StartRemovalAsync();
        await stand.Service().AdvanceAsync(first);
        var published = await S2VersionAsync(stand, first);
        published.Should().BeGreaterThan(0);

        var second = await stand.StartRemovalAsync(userId: keeper.Id);
        await stand.Service().AdvanceAsync(second);
        (await S2VersionAsync(stand, second)).Should().BeGreaterThan(published, "the second removal published a newer list");

        await RunAllAsync(stand, [first, second]);
        foreach (var id in new[] { first, second })
        {
            var view = await stand.ViewAsync(id);
            view.Steps[2].State.Should().Be("done", Dump(view));
            view.Steps[4].State.Should().Be("done", Dump(view));
        }
        await CloseAllAsync(stand, [first, second]);
        (await RemovalWiringTests.LoginAsync(stand.Shared!, "keeper", KeeperPassword)).Should().Be(HttpStatusCode.Unauthorized);
        (await RemovalWiringTests.LoginAsync(stand.Shared!, "admin", OffboardingStand.AdminPassword)).Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// N3 (security review, MEDIUM-HIGH): a list of superadmins that names a person this node keeps a tombstone of - what a rogue or removed accounts
    /// node may publish - leaves no mirror of that person here: the doors applier leaves them out, and the tombstones are enforced again after the
    /// list is applied. (Before: the mirror was created, active, and its password signed in until the next open or tombstone.)
    /// </summary>
    [Fact]
    public async Task A_list_naming_a_removed_person_leaves_no_mirror_of_them()
    {
        await using var stand = await RemovalWiringTests.SharedDoorsStandAsync();
        var keeper = await KeeperAsync(stand);
        // The shared node keeps the person's tombstone; the accounts node (as a rogue one would) still names them.
        await OnAsync(stand.Shared!, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteAsync(
                "INSERT INTO tbl_removed_person (person_id, outcome, removed_at, event_id) VALUES (@p, 'deleted', @at, @e)",
                new { p = keeper.PersonId.ToString("D"), at = DateTime.UtcNow.ToString("O"), e = Guid.NewGuid().ToString("D") });
        });
        await OnAsync(stand.Accounts, async sp => { await sp.GetRequiredService<SuperadminDoorsPublisher>().PublishIfDueAsync(); return true; });

        await RemovalWiringTests.ExchangeAsync(stand.Shared!, stand.Accounts);
        await OnAsync(stand.Shared!, async sp => { await sp.GetRequiredService<ISharedDoorsApplier>().ApplyPendingAsync(); return true; });

        (await CountOnAsync(stand.Shared!, "SELECT COUNT(*) FROM tbl_user WHERE person_id = @p COLLATE NOCASE AND is_active = 1",
            new { p = keeper.PersonId.ToString("D") })).Should().Be(0, "no mirror of a removed person");
        (await RemovalWiringTests.LoginAsync(stand.Shared!, "keeper", KeeperPassword)).Should().Be(HttpStatusCode.Unauthorized);
        (await RemovalWiringTests.LoginAsync(stand.Shared!, "admin", OffboardingStand.AdminPassword)).Should().Be(HttpStatusCode.OK,
            "the rest of the list applies");
    }

    /// <summary>The fake-step stand with a shared node, a removal started and S5 held back by a warning; the review removes the shared node.</summary>
    private static async Task<(OffboardingStand Stand, Guid Id, string Hold)> DeviceRemovedByTheReviewAsync()
    {
        var stand = await OffboardingStand.CreateAsync(shared: true);
        var hold = SignedAsThisNode.MarkerPrefix + Guid.NewGuid().ToString("D");
        await RemovalSecurityTests.SetMarkerAsync(stand.Accounts, hold, on: true);
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        stand.RekeyNode(stand.SharedId);
        stand.SharedDoors.Confirmed[stand.SharedId] = true;
        await stand.Service().ResolveReviewAsync(id, [new OffboardingDecision($"device:{stand.SharedId:D}", "remove")], acceptDefaults: true, stand.AdminId);
        await stand.KeepOpenSuperadminsAsync(id);
        (await StatusOnAccountsAsync(stand, stand.SharedId)).Should().Be("R", "the review removed the device");
        return (stand, id, hold);
    }

    private static Task<string> StatusOnAccountsAsync(OffboardingStand stand, Guid node) => OnAsync(stand.Accounts, async sp =>
        (await sp.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(node, includeDeleted: true))!.Status);

    /// <summary>The device is active again (re-joined with its own key through a node that had not applied the revocation yet, as shared).</summary>
    private static Task<bool> ReactivateAsync(OffboardingStand stand, Guid node) => OnAsync(stand.Accounts, async sp =>
    {
        var wl = sp.GetRequiredService<IWhitelistRepository>();
        var row = (await wl.GetByNodeIdAsync(node, includeDeleted: true))!;
        row.Status = WhitelistStatuses.Active;
        row.UpdatedAt = DateTime.UtcNow;
        await wl.UpdateAsync(row);
        return true;
    });

    /// <summary>
    /// G2: a device the review removed comes back active. Its item is undecided again (pre-selected "remove", said why), and
    /// the review re-opens. (Before: the decided-and-applied item stayed as it was; nothing listed the device again.)
    /// </summary>
    [Fact]
    public async Task A_device_the_review_removed_that_comes_back_active_is_listed_again_and_the_review_reopens()
    {
        var (stand, id, _) = await DeviceRemovedByTheReviewAsync();
        await using var _ = stand;
        (await stand.ViewAsync(id)).Steps[3].State.Should().Be("done");

        await ReactivateAsync(stand, stand.SharedId);
        await stand.Service().AdvanceAsync(id);

        var view = await stand.ViewAsync(id);
        var item = view.Review.Single(i => i.Id == $"device:{stand.SharedId:D}");
        item.Decision.Should().BeNull("it is to be decided again: " + Dump(view));
        item.DefaultRemove.Should().BeTrue();
        item.Why.Should().Contain("active again");
        view.Steps[3].State.Should().NotBe("done", "the review is open again");

        await stand.Service().ResolveReviewAsync(id, [new OffboardingDecision($"device:{stand.SharedId:D}", "remove")], acceptDefaults: false, stand.AdminId);
        (await StatusOnAccountsAsync(stand, stand.SharedId)).Should().Be("R");
    }

    /// <summary>
    /// G2, the second net: the device comes back while the review does not see it (the close runs first). S5 revokes it with the person's devices.
    /// </summary>
    [Fact]
    public async Task The_close_revokes_a_device_the_review_removed_that_is_active_again()
    {
        var (stand, id, hold) = await DeviceRemovedByTheReviewAsync();
        await using var _ = stand;
        for (var round = 0; round < 12 && (await stand.ViewAsync(id)).Steps[4].State != "done"; round++)
        {
            if ((await stand.ViewAsync(id)).NeedsPassword) await stand.Service().ContinueAsync(id, OffboardingStand.AdminPassword, stand.AdminId);
            await stand.Service().AdvanceAsync(id);
            await Task.Delay(100);
        }
        (await stand.ViewAsync(id)).Steps[4].State.Should().Be("done", Dump(await stand.ViewAsync(id)));

        await ReactivateAsync(stand, stand.SharedId);
        await OnAsync(stand.Accounts, async sp =>
        {
            // The review has not looked since (its basis is the network as it is now).
            var store = new OffboardingStore(sp.GetRequiredService<IDbConnectionFactory>());
            var record = (await store.GetAsync(id))!;
            record.Progress.ReviewBasis = await stand.Service().ReviewBasisAsync();
            await store.SaveAsync(record);
            return true;
        });
        await RemovalSecurityTests.SetMarkerAsync(stand.Accounts, hold, on: false);
        await stand.Service().AdvanceAsync(id);

        (await stand.ViewAsync(id)).Open.Should().BeFalse(Dump(await stand.ViewAsync(id)));
        (await StatusOnAccountsAsync(stand, stand.SharedId)).Should().Be("R", "a device the review removed does not come back past the close");
    }

    /// <summary>
    /// G3: a peer that reports "signed as this node" and "accounts node changed" holds S5 - its own report can say so at will.
    /// Both sentences name the remedy that always works on a hostile node: revoke it.
    /// </summary>
    [Fact]
    public async Task The_close_sentences_for_a_peers_own_warnings_say_it_can_be_revoked()
    {
        await using var stand = await OffboardingStand.CreateAsync(shared: true);
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        stand.RekeyNode(stand.SharedId);
        stand.SharedDoors.Confirmed[stand.SharedId] = true;
        var hold = SignedAsThisNode.MarkerPrefix + Guid.NewGuid().ToString("D");
        await RemovalSecurityTests.SetMarkerAsync(stand.Accounts, hold, on: true);   // keeps the close back until the shared node's report is in
        await stand.AcceptReviewAsync(id);
        await stand.KeepOpenSuperadminsAsync(id);
        for (var round = 0; round < 16 && (await stand.ViewAsync(id)).Steps[4].State != "done"; round++)
        {
            if ((await stand.ViewAsync(id)).NeedsPassword) await stand.Service().ContinueAsync(id, OffboardingStand.AdminPassword, stand.AdminId);
            await OffboardingStand.SyncAsync(stand.Shared!, stand.Accounts);
            await OffboardingStand.AnnounceAsync(stand.Shared!);
            await OffboardingStand.SyncAsync(stand.Shared!, stand.Accounts);
            await stand.Service().AdvanceAsync(id);
            await Task.Delay(100);
        }
        (await stand.ViewAsync(id)).Steps[4].State.Should().Be("done", Dump(await stand.ViewAsync(id)));
        // The shared node's report raises both warnings (kept: a Lamport time no later report of the stand reaches).
        await OnAsync(stand.Accounts, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteAsync("UPDATE tbl_node_state SET attention_flags_json = @f, lamport_ts = 999999999 WHERE node_id = @n COLLATE NOCASE",
                new { n = stand.SharedId.ToString("D"), f = "[\"signed_as_this_node\",\"accounts_home_changed\"]" });
        });
        await RemovalSecurityTests.SetMarkerAsync(stand.Accounts, hold, on: false);
        await stand.Service().AdvanceAsync(id);

        var view = await stand.ViewAsync(id);
        view.Open.Should().BeTrue(Dump(view));
        var sentence = view.Steps[5].Sentence;
        sentence.Should().Contain("signed with its own identity key").And.Contain("accounts node changed");
        System.Text.RegularExpressions.Regex.Matches(sentence, "or revoke that node").Count.Should().Be(2, sentence);
    }

    private static Task<long> CountOnAsync(BmbWebApplicationFactory node, string sql, object? args = null) => OnAsync(node, async sp =>
    {
        using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<long>(sql, args);
    });

    private static Task<int> RevokeOnAsync(BmbWebApplicationFactory node, Guid nodeId) => OnAsync(node, async sp =>
    {
        using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return await conn.ExecuteAsync("UPDATE tbl_whitelist SET status = 'R' WHERE node_id = @n COLLATE NOCASE", new { n = nodeId.ToString("D") });
    });

    /// <summary>A superadmin peer signs, as itself, the state of a process: its clock raised first when <paramref name="lamport"/> is given.</summary>
    private static Task<long> StateFromAsync(BmbWebApplicationFactory node, Guid processId, Guid personId, Guid accountsNode, Guid driver, int step,
        DateTime? closedAt, string? outcome, long? lamport = null) => OnAsync(node, async sp =>
    {
        if (lamport is { } at) sp.GetRequiredService<ILamportClock>().RaiseTo(at);
        return await sp.GetRequiredService<IEventLogger>().LogOffboardingStateAsync(processId, personId, accountsNode, driver, "delete", step,
            DateTime.UtcNow.AddMinutes(-1), closedAt, outcome);
    });

    /// <summary>
    /// N2 (security review, HIGH): a rogue superadmin peer signs, as itself and with a huge Lamport time, the close of a genuine open removal it does
    /// not drive. Refused on the driver: the process stays open and driven there, the person's account is not touched, and the driver keeps
    /// driving. (Before: the row was overtaken, became a replica, closed - the driver stopped and S1 to S4 never ran.)
    /// </summary>
    [Fact]
    public async Task A_rogue_peers_close_of_a_removal_it_does_not_drive_is_refused_and_the_driver_keeps_driving()
    {
        await using var stand = await OffboardingStand.CreateAsync(shared: true);
        var id = await stand.StartRemovalAsync();
        await OffboardingStand.SyncAsync(stand.Shared!, stand.Accounts);
        var rogue = await StateFromAsync(stand.Shared!, id, stand.LeaverPersonId, stand.AccountsId, stand.SharedId, 6, DateTime.UtcNow, "deleted",
            lamport: 5_000_000);
        rogue.Should().BeGreaterThan(5_000_000);

        await OffboardingStand.SyncAsync(stand.Accounts, stand.Shared!);

        (await CountOnAsync(stand.Accounts,
            "SELECT COUNT(*) FROM tbl_offboarding WHERE process_id = @p AND driver_node_id = @a COLLATE NOCASE AND origin = 'local' AND closed_at IS NULL AND version < @r",
            new { p = id.ToString("D"), a = stand.AccountsId.ToString("D"), r = rogue })).Should().Be(1, "the rogue state is refused: the row is the driver's own");
        var view = await stand.ViewAsync(id);
        view.Open.Should().BeTrue();
        view.DrivenHere.Should().BeTrue();
        await stand.Service().AdvanceAsync(id);
        var after = await stand.ViewAsync(id);
        after.DrivenHere.Should().BeTrue("the driver keeps driving");
        after.Open.Should().BeTrue();
        (await OnAsync(stand.Accounts, async sp => (await sp.GetRequiredService<IUserRepository>().GetByIdAsync(stand.LeaverId))!.AccountState))
            .Should().Be(UserAccountStates.Offboarding, "the person's account is held by the open removal, not closed by the rogue's state");
    }

    /// <summary>
    /// N2, the other way: the genuine take-over still reaches the other nodes. The accounts node is lost and revoked on both shared nodes; one takes
    /// the removal over, and the other follows it - the driver of record there (the accounts node) is revoked there.
    /// </summary>
    [Fact]
    public async Task A_genuine_take_over_after_the_accounts_node_is_revoked_reaches_another_node()
    {
        await using var stand = await OffboardingStand.CreateAsync(shared: true);
        var (second, _) = await stand.AddSharedNodeAsync("Second shared");
        var id = await stand.StartRemovalAsync();
        await OffboardingStand.SyncAsync(stand.Shared!, stand.Accounts);
        await OffboardingStand.SyncAsync(second, stand.Accounts);
        var keeper = await OnAsync(stand.Shared!, async sp =>
            (await sp.GetRequiredService<UserService>().CreateUserAsync("keeper", "Kim Keeper", KeeperPassword, UserRoles.Superadmin, passwordHint: "the usual")).Id);
        await OffboardingProcessTests.AsListMirrorAsync(stand, keeper);
        await RevokeOnAsync(stand.Shared!, stand.AccountsId);
        await RevokeOnAsync(second, stand.AccountsId);

        await stand.Service(stand.Shared).TakeOverAsync(id, KeeperPassword, keeper);
        await OffboardingStand.SyncAsync(second, stand.Shared!);

        (await CountOnAsync(second, "SELECT COUNT(*) FROM tbl_offboarding WHERE process_id = @p AND driver_node_id = @d COLLATE NOCASE AND origin = 'replicated'",
            new { p = id.ToString("D"), d = stand.SharedId.ToString("D") })).Should().Be(1, "the other node follows the node that took the removal over");
        (await stand.ViewAsync(id, second)).Open.Should().BeTrue();
    }

    /// <summary>
    /// N2: a process this node does not know may be started only by the accounts node of record here. A superadmin peer that is not it cannot invent a
    /// removal of somebody (and lock their account here): its state is held, not applied.
    /// </summary>
    [Fact]
    public async Task A_removal_invented_by_a_peer_that_is_not_the_accounts_node_is_not_applied()
    {
        await using var stand = await RemovalWiringTests.SharedDoorsStandAsync();
        var invented = Guid.NewGuid();
        await StateFromAsync(stand.Shared!, invented, stand.LeaverPersonId, stand.SharedId, stand.SharedId, 0, null, null);

        await RemovalWiringTests.ExchangeAsync(stand.Shared!, stand.Accounts);

        (await CountOnAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_offboarding WHERE process_id = @p", new { p = invented.ToString("D") })).Should().Be(0);
        (await OnAsync(stand.Accounts, async sp => (await sp.GetRequiredService<IUserRepository>().GetByIdAsync(stand.LeaverId))!.AccountState))
            .Should().NotBe("offboarding", "nothing about the person changes here");
    }
}
