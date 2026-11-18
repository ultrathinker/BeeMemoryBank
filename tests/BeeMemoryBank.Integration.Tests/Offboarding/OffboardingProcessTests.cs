using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services.Offboarding;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests.Offboarding;

/// <summary>
/// The mechanics of the removal process (release 2.12.0, plan WS-E): the steps in order, the preconditions, resuming after a crash and a restart,
/// waiting for a node and revoking it, two removals sharing one rotation, the guards (no self-removal, never the last superadmin, no promotion during
/// a process), and a one-person bank that never sees any of it.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class OffboardingProcessTests
{
    private static async Task<User?> UserAsync(BmbWebApplicationFactory node, int id)
    {
        using var scope = node.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<User>(
            "SELECT id AS Id, username AS Username, role AS Role, is_active AS IsActive, account_state AS AccountState, key_slot_id AS KeySlotId FROM tbl_user WHERE id = @id",
            new { id });
    }

    private static async Task<long> CountAsync(BmbWebApplicationFactory node, string sql, object? args = null)
    {
        using var scope = node.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<long>(sql, args);
    }

    [Fact]
    public async Task A_removal_runs_every_step_in_order_and_ends_with_the_account_deleted()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        var id = await stand.StartRemovalAsync();

        var view = await stand.ViewAsync(id);
        view.Open.Should().BeTrue();
        view.Steps[0].State.Should().Be("done", "S0 locks the person out at once");
        view.Step.Should().Be(OffboardingSteps.Rekey, "S1 waits until this node has a new identity key");
        view.Status.Should().Be("being removed, step 2 of 6");
        var locked = (await UserAsync(stand.Accounts, stand.LeaverId))!;
        locked.AccountState.Should().Be(UserAccountStates.Offboarding);
        locked.KeySlotId.Should().BeNull("the person's door went in the S0 transaction");
        locked.IsActive.Should().BeTrue("the account stays until S5, shown as being removed");

        stand.RekeyNode(stand.AccountsId);
        await stand.Service().AdvanceAsync(id);
        view = await stand.ViewAsync(id);
        view.Steps[1].State.Should().Be("done");
        view.Steps[2].State.Should().Be("done", "no shared full node: nobody to wait for");
        view.Step.Should().Be(OffboardingSteps.Review, "S3 waits for the administrator's decisions");
        view.Review.Should().Contain(i => i.Id == $"superadmin:{stand.AdminId}" && !i.DefaultRemove);

        await stand.AcceptReviewAsync(id);
        view = await stand.ViewAsync(id);
        view.Open.Should().BeFalse("S4 ran with the password held since the start, every full node (this one) reported the key, and S5 closed");
        view.Outcome.Should().Be("deleted");
        view.Reminders.Should().HaveCount(4);
        view.Reminders.Should().Contain(r => r.Contains("A stolen device of somebody who stays keeps its access until you revoke it"))
            .And.Contain(r => r.Contains("Which tool answers which theft") && r.Contains("new identity key") && r.Contains("bmb rekey"));
        (await UserAsync(stand.Accounts, stand.LeaverId))!.IsActive.Should().BeFalse("deleted at S5");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_removed_person WHERE person_id = @p COLLATE NOCASE AND outcome = 'deleted'",
            new { p = stand.LeaverPersonId.ToString("D") })).Should().Be(1, "the tombstone is kept for ever");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_dek_rotation_state WHERE state = 'APPLIED'")).Should().Be(1, "S4 changed the master key");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_event WHERE event_type = 'dek_rotation_commit' AND payload LIKE @r",
            new { r = $"%offboarding:{id:D}%" })).Should().Be(1, "the rotation names the removal, so every node applies it by itself");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_event WHERE event_type = 'person_removed'")).Should().Be(1);
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_audit_log WHERE action = 'offboarding_closed'")).Should().Be(1);
    }

    [Fact]
    public async Task A_demotion_ends_with_an_ordinary_active_account_that_sees_only_the_folders_chosen_at_the_start()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        Guid folder;
        using (var scope = stand.Accounts.Services.CreateScope())
            folder = (await scope.ServiceProvider.GetRequiredService<FolderService>().CreateAsync("/Shared notes")).Id;

        var id = await stand.StartRemovalAsync("demote", UserRoles.User, [new DemotionFolderRule(folder, AclEffect.Allow, IsReadOnly: true)]);
        (await UserAsync(stand.Accounts, stand.LeaverId))!.Role.Should().Be(UserRoles.Superadmin, "the role changes only at S5; until then nobody is shown as 'user'");
        stand.RekeyNode(stand.AccountsId);
        await stand.AcceptReviewAsync(id);

        var after = (await UserAsync(stand.Accounts, stand.LeaverId))!;
        after.Role.Should().Be(UserRoles.User);
        after.AccountState.Should().Be(UserAccountStates.Active);
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_folder_acl_entry WHERE user_id = @u AND effect = 'allow' AND is_read_only = 1", new { u = stand.LeaverId }))
            .Should().Be(1);
        (await stand.ViewAsync(id)).Outcome.Should().Be("demoted");
    }

    [Fact]
    public async Task Preconditions_refuse_and_change_nothing()
    {
        await using var stand = await OffboardingStand.CreateAsync(personal: true);
        var service = stand.Service();

        var wrong = () => stand.StartRemovalAsync(password: "Not-The-Password-1");
        (await wrong.Should().ThrowAsync<OffboardingRefusedException>()).Which.Status.Should().Be(403);

        var self = () => stand.StartRemovalAsync(userId: stand.AdminId);
        (await self.Should().ThrowAsync<OffboardingRefusedException>()).WithMessage("You cannot remove yourself*");

        var openRole = () => stand.StartRemovalAsync("demote", UserRoles.User);
        (await openRole.Should().ThrowAsync<OffboardingRefusedException>()).WithMessage("Say which folders*", "the open role is never the default");

        using (var scope = stand.Accounts.Services.CreateScope())
        using (var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection())
            await conn.ExecuteAsync("UPDATE tbl_whitelist SET needs_review = 1 WHERE node_id = @n COLLATE NOCASE", new { n = stand.PersonalId.ToString("D") });
        var review = () => stand.StartRemovalAsync();
        (await review.Should().ThrowAsync<OffboardingRefusedException>()).WithMessage("First say whose these devices are*Lee's laptop*");
        await stand.SetOwnershipAsync(stand.PersonalId, OwnershipKinds.Personal, stand.LeaverPersonId);

        var databaseId = stand.Accounts.Services.GetRequiredService<IDbConnectionFactory>().DatabaseId;
        using (KeyOperationLock.TryEnter(databaseId, "an identity re-key"))
        {
            var busy = () => stand.StartRemovalAsync();
            (await busy.Should().ThrowAsync<OffboardingRefusedException>()).WithMessage("An identity re-key is running*");
        }

        (await UserAsync(stand.Accounts, stand.LeaverId))!.AccountState.Should().Be(UserAccountStates.Active, "no refusal changed anything");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_offboarding")).Should().Be(0);
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_whitelist WHERE status = 'A'")).Should().Be(1, "the personal device was not revoked");
    }

    [Fact]
    public async Task Never_the_last_superadmin_and_no_role_change_or_promotion_while_a_process_is_open()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        using (var scope = stand.Accounts.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserService>();
            // The account-side guard: the only active superadmin cannot be locked out.
            var last = () => users.LockOutForRemovalAsync(stand.AdminId, (_, _) => Task.CompletedTask);
            await stand.StartRemovalAsync();
            await last.Should().ThrowAsync<InvalidOperationException>().WithMessage("Cannot remove the last superadmin*");
        }

        using (var scope = stand.Accounts.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserService>();
            var demote = () => users.DemoteSuperadminAsync(stand.LeaverId, UserRoles.User, allowOpenRole: true);
            await demote.Should().ThrowAsync<InvalidOperationException>().WithMessage("This person is being removed*");
            var delete = () => users.DeleteUserAsync(stand.LeaverId);
            await delete.Should().ThrowAsync<InvalidOperationException>().WithMessage("This person is being removed*");
            (await users.AuthenticateAsync("leaver", OffboardingStand.LeaverPassword)).Should().BeNull("an account being removed cannot sign in");

            // Promotion (plan E7) of an account whose person has an open removal is refused.
            var ordinary = await users.CreateUserAsync("ordinary", "Ordinary", "Ordinary-Password-1", UserRoles.User, passwordHint: "hint");
            using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            await conn.ExecuteAsync("UPDATE tbl_offboarding SET person_id = @p", new { p = ordinary.PersonId.ToString("D") });
            var promote = () => users.UpdateUserAsync(ordinary.Id, "Ordinary", UserRoles.Superadmin);
            await promote.Should().ThrowAsync<InvalidOperationException>().WithMessage("This person is being removed*");
        }
    }

    [Fact]
    public async Task A_person_promoted_again_after_a_finished_demotion_gets_a_new_person_id_so_the_old_tombstone_never_applies()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        var id = await stand.StartRemovalAsync("demote", UserRoles.User, allowOpen: true);
        stand.RekeyNode(stand.AccountsId);
        await stand.AcceptReviewAsync(id);
        (await stand.ViewAsync(id)).Open.Should().BeFalse();

        using var scope = stand.Accounts.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserService>();
        await users.UpdateUserAsync(stand.LeaverId, "Lee Leaver", UserRoles.Superadmin, "Leaver-Password-2", passwordHint: "new hint");
        var again = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(stand.LeaverId))!;
        again.Role.Should().Be(UserRoles.Superadmin);
        again.PersonId.Should().NotBe(stand.LeaverPersonId);

        (await scope.ServiceProvider.GetRequiredService<IRemovedPersonEnforcer>().EnforceAsync()).Should().Be(0, "the tombstone names the old person id");
        (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(stand.LeaverId))!.Role.Should().Be(UserRoles.Superadmin);
    }

    [Fact]
    public async Task A_crash_right_after_the_lock_out_is_finished_by_the_next_resume()
    {
        await using var stand = await OffboardingStand.CreateAsync(personal: true);
        var service = stand.Service();
        service.AfterLockOutCommitForTests = () => throw new IOException("the process died here");
        var start = () => stand.StartRemovalAsync();
        await start.Should().ThrowAsync<IOException>();
        service.AfterLockOutCommitForTests = null;

        var id = Guid.Parse((await ProcessIdsAsync(stand.Accounts)).Single());
        (await UserAsync(stand.Accounts, stand.LeaverId))!.AccountState.Should().Be(UserAccountStates.Offboarding, "the transaction committed before the crash");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_whitelist WHERE status = 'A'")).Should().Be(1, "the device revoke comes after the transaction");
        (await stand.ViewAsync(id)).Steps[0].State.Should().NotBe("done");

        await service.ResumeAsync(id);
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_whitelist WHERE status = 'A'")).Should().Be(0, "the person's personal device is revoked");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_event WHERE event_type = 'person_removed'")).Should().Be(1);
        await service.ResumeAsync(id);
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_event WHERE event_type = 'person_removed'")).Should().Be(1, "a second resume writes no second tombstone");
        (await stand.ViewAsync(id)).Steps[0].State.Should().Be("done");
    }

    [Fact]
    public async Task A_crash_between_proposing_the_rotation_and_saving_it_is_resumed_by_adopting_that_rotation()
    {
        // the rotation is on disk (its commit event and a Committing row, reason offboarding:<id>) but the process never saved its id.
        await using var stand = await OffboardingStand.CreateAsync();
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        var service = stand.Service();
        service.AfterRotationProposedForTests = () => throw new IOException("the process died here");
        var review = () => stand.AcceptReviewAsync(id);
        await review.Should().ThrowAsync<IOException>();
        service.AfterRotationProposedForTests = null;
        var rows = await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_dek_rotation_state");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_dek_rotation_state WHERE state = 'COMMITTING'")).Should().Be(1, "the proposal is on disk");

        // The restart: no password in memory; continue gives it.
        var restarted = ActivatorUtilities.CreateInstance<OffboardingService>(stand.Accounts.Services, stand.Accounts.DataPath);
        (await restarted.GetAsync(id))!.Open.Should().BeTrue();
        await restarted.ContinueAsync(id, OffboardingStand.AdminPassword, stand.AdminId);

        (await restarted.GetAsync(id))!.Open.Should().BeFalse("the orphaned rotation of this process was adopted and applied");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_dek_rotation_state")).Should().Be(rows, "no second rotation was proposed");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_dek_rotation_state WHERE state = 'APPLIED'")).Should().Be(1);
    }

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("REJECTED")]
    public async Task A_rotation_the_process_held_that_ended_cancelled_or_rejected_is_let_go_and_a_new_one_is_proposed(string ended)
    {
        // the process keeps the id of a rotation that will never apply here (accept refuses a cancelled or rejected one for ever).
        await using var stand = await OffboardingStand.CreateAsync();
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        var service = stand.Service();
        service.AfterRotationProposedForTests = () => throw new IOException("the process died here");
        var review = () => stand.AcceptReviewAsync(id);
        await review.Should().ThrowAsync<IOException>();
        service.AfterRotationProposedForTests = null;
        string dead;
        using (var scope = stand.Accounts.Services.CreateScope())
        {
            using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            dead = await conn.ExecuteScalarAsync<string>("SELECT event_id FROM tbl_dek_rotation_state WHERE state = 'COMMITTING'") ?? "";
            await conn.ExecuteAsync("UPDATE tbl_dek_rotation_state SET state = @ended WHERE event_id = @dead", new { ended, dead });
            var store = new OffboardingStore(scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>());
            var record = (await store.GetAsync(id))!;
            record.Progress.S4CommitId = dead;   // the process holds it (adopted before it ended)
            await store.SaveAsync(record);
        }

        var restarted = ActivatorUtilities.CreateInstance<OffboardingService>(stand.Accounts.Services, stand.Accounts.DataPath);
        await restarted.ContinueAsync(id, OffboardingStand.AdminPassword, stand.AdminId);

        (await restarted.GetAsync(id))!.Open.Should().BeFalse("a new rotation was proposed and applied");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_dek_rotation_state WHERE state = 'APPLIED' AND event_id <> @dead", new { dead }))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_rotation_without_a_removal_reason_proposed_after_the_review_is_not_taken_for_the_removal()
    {
        // Only rotations named offboarding:<process> serve a removal; a manual one keeps the rule it had (auto-accept setting).
        await using var stand = await OffboardingStand.CreateAsync();
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        var restarted = ActivatorUtilities.CreateInstance<OffboardingService>(stand.Accounts.Services, stand.Accounts.DataPath);
        await restarted.ResolveReviewAsync(id, [], acceptDefaults: true, stand.AdminId);   // S3 done; S4 waits for the password
        Guid manual;
        using (var scope = stand.Accounts.Services.CreateScope())
        {
            var admin = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(stand.AdminId))!;
            manual = await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Api.Services.DekRotationService>()
                .ProposeRotationAsync(OffboardingStand.AdminPassword, admin.Id, null);
        }

        await restarted.AdvanceAsync(id);

        var record = await InScopeAsync(stand.Accounts, sp => new OffboardingStore(sp.GetRequiredService<IDbConnectionFactory>()).GetAsync(id));
        record!.Progress.S4CommitId.Should().BeNull("a manual rotation is not the removal's");
        manual.Should().NotBeEmpty();
    }

    private static async Task<T> InScopeAsync<T>(BmbWebApplicationFactory node, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = node.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    [Fact]
    public async Task A_shared_full_node_that_appears_during_the_process_reopens_S1_and_S2_for_itself()
    {
        // the wait sets of S1 and S2 follow the CURRENT shared full nodes on every pass, not the set of the moment a step started.
        await using var stand = await OffboardingStand.CreateAsync();
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        await stand.Service().AdvanceAsync(id);
        (await stand.ViewAsync(id)).Step.Should().Be(OffboardingSteps.Review, "no shared full node yet: S1 is this node, S2 has nobody to reach");
        stand.SharedDoors.Published.Should().BeEmpty();

        var (_, late) = await stand.AddSharedNodeAsync("Late server");
        await stand.Service().AdvanceAsync(id);

        var view = await stand.ViewAsync(id);
        view.Step.Should().Be(OffboardingSteps.Rekey, "a shared full node appeared: S1 opens again for it");
        view.Steps[1].Waits.Should().Contain(w => w.NodeId == late && w.State == "waiting");
        stand.Rekey.Started.Should().Contain(s => s.Process == id && s.Shared.Contains(late), "the re-key is asked of it");

        stand.RekeyNode(late);
        await stand.Service().AdvanceAsync(id);
        view = await stand.ViewAsync(id);
        view.Step.Should().Be(OffboardingSteps.SharedNodes, "S2 opens again for it too");
        view.Steps[2].Waits.Should().Contain(w => w.NodeId == late && w.State == "waiting");
        stand.SharedDoors.Published.Should().ContainSingle("the list goes out now that there is a shared node to reach");

        stand.SharedDoors.Confirmed[late] = true;
        await stand.Service().AdvanceAsync(id);
        (await stand.ViewAsync(id)).Step.Should().Be(OffboardingSteps.Review);
    }

    [Fact]
    public async Task A_replica_shows_the_step_the_driver_reports()
    {
        // a shared node's copy of the process carries the step in its row (the event's), not the driver's progress.
        await using var stand = await OffboardingStand.CreateAsync(shared: true);
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        stand.RekeyNode(stand.SharedId);
        stand.SharedDoors.Confirmed[stand.SharedId] = true;
        await stand.Service().AdvanceAsync(id);
        (await stand.ViewAsync(id)).Step.Should().Be(OffboardingSteps.Review);

        await OffboardingStand.SyncAsync(stand.Shared!, stand.Accounts);

        var replica = await stand.ViewAsync(id, stand.Shared);
        replica.Step.Should().Be(OffboardingSteps.Review);
        replica.Status.Should().Be("being removed, step 4 of 6");
        replica.Steps.Take(3).Should().OnlyContain(st => st.State == "done");
        replica.Steps[3].State.Should().Be("waiting");
    }

    [Fact]
    public async Task A_locked_node_is_asked_to_be_opened_once_at_every_step_and_so_is_this_node_after_its_own_rotation()
    {
        // "open this node once" wherever a locked node is the cause.
        await using var stand = await OffboardingStand.CreateAsync(shared: true);
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        // The shared node's last report says it is locked: no master-key fingerprint.
        await InScopeAsync(stand.Accounts, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteAsync(
                @"INSERT OR REPLACE INTO tbl_node_state (node_id, app_version, platform, runtime_kind, identity_key_fp, dek_fp, attention_flags_json, lamport_ts, updated_at)
                  VALUES (@n, '2.12.0', 'windows', 'server', '', NULL, '[]', 1, @t)",
                new { n = stand.SharedId.ToString("D"), t = DateTime.UtcNow.ToString("O") });
        });
        await stand.Service().AdvanceAsync(id);

        var wait = (await stand.ViewAsync(id)).Steps[1].Waits.Single(w => w.NodeId == stand.SharedId);
        wait.State.Should().Be("waiting");
        wait.OpenOnce.Should().BeTrue("S1 waits for a locked node too");
        wait.Why.Should().Contain("Open this node once");

        // This node, after applying the rotation itself, now locked: the sentence is no longer "enter your password".
        stand.RekeyNode(stand.SharedId);
        stand.SharedDoors.Confirmed[stand.SharedId] = true;
        await stand.Service().AdvanceAsync(id);
        await stand.AcceptReviewAsync(id);
        var view = await stand.ViewAsync(id);
        view.Step.Should().Be(OffboardingSteps.Rotate, "the shared node has not reported the new key");
        await InScopeAsync(stand.Accounts, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteAsync("UPDATE tbl_offboarding_node SET state = 'waiting', confirmed_at = NULL WHERE step = 4 AND node_id = @n COLLATE NOCASE",
                new { n = stand.AccountsId.ToString("D") });
        });
        var session = stand.Accounts.Services.GetRequiredService<SessionService>();
        session.Lock();
        try
        {
            var self = (await stand.ViewAsync(id)).Steps[4].Waits.Single(w => w.NodeId == stand.AccountsId);
            self.Why.Should().Contain("Open this node once").And.NotContain("enter your password");
            self.OpenOnce.Should().BeTrue();
        }
        finally
        {
            (await session.UnlockAsync(OffboardingStand.AdminPassword)).Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task A_crash_after_a_step_did_its_work_and_before_the_pass_was_saved_is_resumed_after_a_restart(int step)
    {
        // Review weak tests: crash and restart at each step. The pass that finished the step dies before it is saved; a restarted service (no
        // password in memory) resumes, and nothing is done twice that must not be: one tombstone, one applied rotation, the account deleted once.
        await using var stand = await OffboardingStand.CreateAsync(shared: true);
        var service = stand.Service();
        var crashed = false;
        service.BeforeSavingPassForTests = p =>
        {
            var reached = step switch { 1 => p.S1Done, 2 => p.S2Done, 3 => p.S3Done, 4 => p.S4Done, _ => p.S5Done };
            if (!crashed && reached)
            {
                crashed = true;
                throw new IOException("the process died here");
            }
            return Task.CompletedTask;
        };
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        stand.RekeyNode(stand.SharedId);
        stand.SharedDoors.Confirmed[stand.SharedId] = true;
        async Task SurviveAsync(Func<Task> pass) { try { await pass(); } catch (IOException) when (crashed) { } }
        await SurviveAsync(() => service.AdvanceAsync(id));
        if (!crashed) await SurviveAsync(() => stand.AcceptReviewAsync(id));
        for (var round = 0; round < 20 && !crashed; round++)
        {
            await OffboardingStand.SyncAsync(stand.Shared!, stand.Accounts);
            await OffboardingStand.AnnounceAsync(stand.Shared!);
            await OffboardingStand.SyncAsync(stand.Shared!, stand.Accounts);
            await SurviveAsync(() => service.AdvanceAsync(id));
            if (!crashed) await Task.Delay(250);
        }
        crashed.Should().BeTrue($"the pass that finished S{step} died");
        service.BeforeSavingPassForTests = null;

        var restarted = ActivatorUtilities.CreateInstance<OffboardingService>(stand.Accounts.Services, stand.Accounts.DataPath);
        await restarted.ResumeAsync(id);
        var view = (await restarted.GetAsync(id))!;
        for (var round = 0; round < 20 && view.Open; round++)
        {
            if (view.Step == OffboardingSteps.Review) await restarted.ResolveReviewAsync(id, [], acceptDefaults: true, stand.AdminId);
            if (view.NeedsPassword) await restarted.ContinueAsync(id, OffboardingStand.AdminPassword, stand.AdminId);
            await OffboardingStand.SyncAsync(stand.Shared!, stand.Accounts);
            await OffboardingStand.AnnounceAsync(stand.Shared!);
            await OffboardingStand.SyncAsync(stand.Shared!, stand.Accounts);
            await restarted.ResumeAsync(id);
            view = (await restarted.GetAsync(id))!;
            if (view.Open) await Task.Delay(250);
        }

        view.Open.Should().BeFalse("resumed to its end: " + string.Join(" | ", view.Steps.Select(st => $"{st.Number}:{st.State}:{st.Sentence}")));
        view.Outcome.Should().Be("deleted");
        (await UserAsync(stand.Accounts, stand.LeaverId))!.IsActive.Should().BeFalse();
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_event WHERE event_type = 'person_removed'")).Should().Be(1);
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_dek_rotation_state WHERE state = 'APPLIED'")).Should().Be(1, "one rotation, not two");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_audit_log WHERE action = 'offboarding_closed'")).Should().Be(1);
    }

    /// <summary>
    /// The shared node as after it applied the accounts node's list: every superadmin account there is a mirror of the list and no old shared
    /// password slot is left. A node that keeps superadmin doors of its own does not take a removal over.
    /// </summary>
    internal static Task<int> AsListMirrorAsync(OffboardingStand stand, int userId) => InSharedAsync(stand, async sp =>
    {
        using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_key_slot WHERE slot_type = 'password'");
        return await conn.ExecuteAsync("UPDATE tbl_user SET replicated = 1 WHERE id = @u OR (role = 'superadmin' AND is_active = 1)", new { u = userId });
    });

    private static async Task<List<string>> ProcessIdsAsync(BmbWebApplicationFactory node)
    {
        using var scope = node.Services.CreateScope();
        using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        return (await conn.QueryAsync<string>("SELECT process_id FROM tbl_offboarding")).ToList();
    }

    [Fact]
    public async Task After_a_restart_the_rotation_waits_for_the_password_and_continue_finishes_it()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);

        // A restart: a new service instance has no password in memory.
        var restarted = ActivatorUtilities.CreateInstance<OffboardingService>(stand.Accounts.Services, stand.Accounts.DataPath);
        await restarted.ResolveReviewAsync(id, [], acceptDefaults: true, stand.AdminId);
        var view = (await restarted.GetAsync(id))!;
        view.Step.Should().Be(OffboardingSteps.Rotate);
        view.NeedsPassword.Should().BeTrue("the password is kept in memory only");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_dek_rotation_state")).Should().Be(0);

        var wrong = () => restarted.ContinueAsync(id, "Wrong-Password-1", stand.AdminId);
        await wrong.Should().ThrowAsync<OffboardingRefusedException>();
        var leaverCannot = () => restarted.ContinueAsync(id, OffboardingStand.LeaverPassword, stand.LeaverId);
        await leaverCannot.Should().ThrowAsync<OffboardingRefusedException>();

        await restarted.ContinueAsync(id, OffboardingStand.AdminPassword, stand.AdminId);
        (await restarted.GetAsync(id))!.Open.Should().BeFalse();
    }

    [Fact]
    public async Task A_step_waits_for_a_node_that_does_not_report_and_revoking_that_node_ends_the_wait()
    {
        await using var stand = await OffboardingStand.CreateAsync(othersDevice: true);
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        await stand.AcceptReviewAsync(id);

        var view = await stand.ViewAsync(id);
        view.Step.Should().Be(OffboardingSteps.Rotate, "the administrator's phone has not reported the new master key");
        var wait = view.Steps[4].Waits.Single(w => w.NodeId == stand.OthersDeviceId);
        wait.State.Should().Be("waiting");
        wait.Why.Should().Contain("revoke it");
        wait.CanRevoke.Should().BeTrue();

        // The phone applies the removal rotation by itself (plan A5) and reports it: confirmed, the process closes. Revoking is the other way out,
        // proven by the next test; here the wait ends by the report, and the test says which path closed it.
        var closed = await stand.ViewAsync(id);
        for (var round = 0; round < 20 && closed.Open; round++)
        {
            await OffboardingStand.SyncAsync(stand.OthersDevice!, stand.Accounts);
            await OffboardingStand.AnnounceAsync(stand.OthersDevice!);
            await OffboardingStand.SyncAsync(stand.OthersDevice!, stand.Accounts);
            await stand.Service().AdvanceAsync(id);
            closed = await stand.ViewAsync(id);
            if (closed.Open) await Task.Delay(250);
        }
        closed.Open.Should().BeFalse();
        closed.Steps[4].Waits.Single(w => w.NodeId == stand.OthersDeviceId).State.Should().Be("confirmed", "the report closed the wait, not a revoke");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_whitelist WHERE node_id = @n COLLATE NOCASE AND status = 'A'",
            new { n = stand.OthersDeviceId.ToString("D") })).Should().Be(1, "nobody revoked it");
    }

    [Fact]
    public async Task A_full_node_that_keeps_a_bridge_from_the_old_master_key_reports_it_and_the_close_names_it()
    {
        // 2.12 cross review, finding 2: the bridge (v1 doors, chain material) of every full node travels in its node_state, and S5 reads it from there.
        await using var stand = await OffboardingStand.CreateAsync(othersDevice: true);
        await OffboardingStand.MakeDoorV1Async(stand.OthersDevice!, "admin", OffboardingStand.AdminPassword);   // not signed in there since the update
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        await stand.AcceptReviewAsync(id);

        var view = await stand.ViewAsync(id);
        for (var round = 0; round < 20 && view.Open; round++)
        {
            // The phone applies the removal rotation by itself (it keeps a chain: a v1 door is there) and reports it.
            await OffboardingStand.SyncAsync(stand.OthersDevice!, stand.Accounts);
            await OffboardingStand.AnnounceAsync(stand.OthersDevice!);
            await OffboardingStand.SyncAsync(stand.OthersDevice!, stand.Accounts);
            await stand.Service().AdvanceAsync(id);
            view = await stand.ViewAsync(id);
            if (view.Open) await Task.Delay(250);
        }

        view.Open.Should().BeFalse("the phone reported the new master key");
        using (var scope = stand.Accounts.Services.CreateScope())
        {
            var state = await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Sync.NodeStateStore>().GetAsync(stand.OthersDeviceId);
            state!.Extra.Should().NotBeNull();
            state.Extra![BeeMemoryBank.Sync.NodeStateExtraKeys.V1Doors].Should().Be("1", "the phone's report, as stored in tbl_node_state on the accounts node");
            state.Extra[BeeMemoryBank.Sync.NodeStateExtraKeys.DekBridge].Should().Be("1");
        }
        view.Bridges.Should().ContainSingle().Which.Should().Contain("Admin's phone").And.Contain("1 old door(s)");
        view.Steps[4].Waits.Single(w => w.NodeId == stand.OthersDeviceId).Why.Should().Contain("a bridge from the old one remains");
    }

    [Fact]
    public async Task A_shared_node_takes_a_removal_over_once_the_accounts_node_is_revoked_and_finishes_it()
    {
        await using var stand = await OffboardingStand.CreateAsync(shared: true);
        var id = await stand.StartRemovalAsync();
        await OffboardingStand.SyncAsync(stand.Shared!, stand.Accounts);   // the shared node learns the process and the tombstone

        var mirror = await stand.ViewAsync(id, stand.Shared);
        mirror.Open.Should().BeTrue();
        mirror.DrivenHere.Should().BeFalse();
        mirror.CanTakeOver.Should().BeFalse("the accounts node is still in the network");
        // A superadmin with an account on the shared node itself (before shared doors a joined node carries no accounts of the accounts node).
        const string KeeperPassword = "Keeper-Password-1";
        var adminHere = await InSharedAsync(stand, async sp =>
            (await sp.GetRequiredService<UserService>().CreateUserAsync("keeper", "Kim Keeper", KeeperPassword, UserRoles.Superadmin, passwordHint: "the usual")).Id);
        await AsListMirrorAsync(stand, adminHere);
        var refused = () => stand.Service(stand.Shared).TakeOverAsync(id, KeeperPassword, adminHere);
        (await refused.Should().ThrowAsync<OffboardingRefusedException>()).Which.Message.Should().Contain("Revoke it first");

        // The accounts node is lost and revoked; the shared node takes the removal over and runs it to its end.
        await InSharedAsync(stand, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteAsync("UPDATE tbl_whitelist SET status = 'R' WHERE node_id = @n COLLATE NOCASE", new { n = stand.AccountsId.ToString("D") });
        });
        (await stand.ViewAsync(id, stand.Shared)).CanTakeOver.Should().BeTrue();
        await stand.Service(stand.Shared).TakeOverAsync(id, KeeperPassword, adminHere);

        var taken = await stand.ViewAsync(id, stand.Shared);
        taken.DrivenHere.Should().BeTrue("this node drives it now");
        stand.RekeyNode(stand.SharedId);
        await stand.Service(stand.Shared).AdvanceAsync(id);
        await stand.Service(stand.Shared).ResolveReviewAsync(id, [], acceptDefaults: true, adminHere);
        await stand.KeepOpenSuperadminsAsync(id, stand.Shared, adminHere);   // decided one by one
        var closed = await stand.ViewAsync(id, stand.Shared);
        closed.Open.Should().BeFalse("the shared node finished the removal: " + string.Join(" | ", closed.Steps.Select(st => $"{st.Number}:{st.State}:{st.Sentence}")));
        (await InSharedAsync(stand, async sp => (await sp.GetRequiredService<IUserRepository>().ListActiveAsync()).Count(u => u.PersonId == stand.LeaverPersonId)))
            .Should().Be(0, "the person's account is gone on the node that finished it");
        (await InSharedAsync(stand, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_audit_log WHERE action = 'offboarding_taken_over'");
        })).Should().Be(1);
    }

    [Fact]
    public async Task The_node_that_drove_a_process_before_a_take_over_refuses_another_nodes_state_for_it()
    {
        // The accounts node, restored after a shared node took its removal over, follows the take-over's states. A later rule
        // withdrew that: this node drives the process and never counts itself as revoked, so another node's state for it looks exactly like a rogue
        // superadmin peer's close of a removal it does not drive, and is refused. (A node that drove a process before a take-over is revoked on every
        // other node and cannot sync; the take-over reaches every other node: RemovalSecurity2Tests.)
        await using var stand = await OffboardingStand.CreateAsync(shared: true);
        var id = await stand.StartRemovalAsync();
        await OffboardingStand.SyncAsync(stand.Shared!, stand.Accounts);
        const string KeeperPassword = "Keeper-Password-1";
        var keeper = await InSharedAsync(stand, async sp =>
            (await sp.GetRequiredService<UserService>().CreateUserAsync("keeper", "Kim Keeper", KeeperPassword, UserRoles.Superadmin, passwordHint: "the usual")).Id);
        await AsListMirrorAsync(stand, keeper);
        await InSharedAsync(stand, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteAsync("UPDATE tbl_whitelist SET status = 'R' WHERE node_id = @n COLLATE NOCASE", new { n = stand.AccountsId.ToString("D") });
        });
        await stand.Service(stand.Shared).TakeOverAsync(id, KeeperPassword, keeper);

        // The accounts node sees the shared node's states (as after its restore and re-join), oldest first.
        var states = await InSharedAsync(stand, async sp => (await sp.GetRequiredService<IEventLogRepository>().GetRecentAsync(200, 0, BeeMemoryBank.Sync.EventTypes.OffboardingState))
            .Where(e => e.NodeId == stand.SharedId).OrderBy(e => e.LamportTs).ToList());
        states.Should().NotBeEmpty();
        using (var scope = stand.Accounts.Services.CreateScope())
        {
            var apply = () => scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Sync.EventApplier>().ApplyAsync(states[0]);
            await apply.Should().ThrowAsync<UnauthorizedAccessException>();
        }

        var kept = await stand.ViewAsync(id);
        kept.DrivenHere.Should().BeTrue("the row of the driver of record is unchanged");
        kept.Open.Should().BeTrue();
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_offboarding WHERE process_id = @p AND driver_node_id = @d COLLATE NOCASE AND origin = 'local'",
            new { p = id.ToString("D"), d = stand.AccountsId.ToString("D") })).Should().Be(1);
    }

    internal static async Task<T> InSharedAsync<T>(OffboardingStand stand, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = stand.Shared!.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    [Fact]
    public async Task A_refused_removal_of_another_superadmin_from_the_review_leaves_the_item_to_decide_again()
    {
        // a mistyped password (or a passing refusal) must not leave the item decided and never carried out.
        await using var stand = await OffboardingStand.CreateAsync();
        int eve;
        using (var scope = stand.Accounts.Services.CreateScope())
            eve = (await scope.ServiceProvider.GetRequiredService<UserService>()
                .CreateUserAsync("eve", "Eve", "Eve-Password-1", UserRoles.Superadmin, passwordHint: "her hint")).Id;
        var id = await stand.StartRemovalAsync();

        var refused = () => stand.Service().ResolveReviewAsync(id,
            [new OffboardingDecision($"superadmin:{eve}", "remove", null, new OffboardingSuperadminTarget("delete", null, null, false))],
            acceptDefaults: false, stand.AdminId, "Not-The-Password-1");
        await refused.Should().ThrowAsync<OffboardingRefusedException>();

        var item = (await stand.ViewAsync(id)).Review.Single(i => i.Id == $"superadmin:{eve}");
        item.Decision.Should().BeNull("the removal of Eve did not start: the item is open to be decided again");
        item.Applied.Should().BeFalse();
        (await UserAsync(stand.Accounts, eve))!.AccountState.Should().Be(UserAccountStates.Active);
    }

    private sealed class FailingBridgeStatus : IDekBridgeStatus
    {
        public Task<int> GetV1DoorCountAsync() => throw new IOException("the bridge cannot be read now");
        public Task<bool> IsBridgePresentAsync() => throw new IOException("the bridge cannot be read now");
    }

    [Fact]
    public async Task A_node_that_cannot_tell_its_bridge_reports_it_unknown_and_the_close_says_so()
    {
        // a failed bridge contribution is "unknown", never read as "no bridge".
        await using var stand = await OffboardingStand.CreateAsync(othersDevice: true,
            othersDeviceServices: services => services.AddScoped<IDekBridgeStatus, FailingBridgeStatus>());
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        await stand.AcceptReviewAsync(id);
        var view = await stand.ViewAsync(id);
        for (var round = 0; round < 20 && view.Open; round++)
        {
            await OffboardingStand.SyncAsync(stand.OthersDevice!, stand.Accounts);
            await OffboardingStand.AnnounceAsync(stand.OthersDevice!);
            await OffboardingStand.SyncAsync(stand.OthersDevice!, stand.Accounts);
            await stand.Service().AdvanceAsync(id);
            view = await stand.ViewAsync(id);
            if (view.Open) await Task.Delay(250);
        }

        view.Open.Should().BeFalse();
        using (var scope = stand.Accounts.Services.CreateScope())
        {
            var state = await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Sync.NodeStateStore>().GetAsync(stand.OthersDeviceId);
            state!.Extra.Should().NotBeNull();
            state.Extra![BeeMemoryBank.Sync.NodeStateExtraKeys.DekBridge].Should().Be("unknown");
        }
        view.Bridges.Should().ContainSingle().Which.Should().Contain("Admin's phone").And.Contain("not known");
    }

    [Fact]
    public async Task Revoking_the_node_that_is_waited_for_closes_the_process_and_it_is_never_waited_for_again()
    {
        await using var stand = await OffboardingStand.CreateAsync(othersDevice: true);
        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        await stand.AcceptReviewAsync(id);
        (await stand.ViewAsync(id)).Step.Should().Be(OffboardingSteps.Rotate);

        await stand.Service().RevokeNodeAsync(id, stand.OthersDeviceId, stand.AdminId);
        var view = await stand.ViewAsync(id);
        view.Open.Should().BeFalse();
        view.Steps[4].Waits.Single(w => w.NodeId == stand.OthersDeviceId).State.Should().Be("revoked");
    }

    [Fact]
    public async Task Two_removals_share_one_rotation()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        int second;
        using (var scope = stand.Accounts.Services.CreateScope())
            second = (await scope.ServiceProvider.GetRequiredService<UserService>()
                .CreateUserAsync("second", "Second Leaver", OffboardingStand.OtherPassword, UserRoles.Superadmin, passwordHint: "hint")).Id;

        var first = await stand.StartRemovalAsync();
        var other = await stand.StartRemovalAsync(userId: second);
        stand.RekeyNode(stand.AccountsId);

        // Both reviews are decided before the rotation (after a restart: no password held), then ONE rotation, entered once, ends both.
        var restarted = ActivatorUtilities.CreateInstance<OffboardingService>(stand.Accounts.Services, stand.Accounts.DataPath);
        await restarted.ResolveReviewAsync(first, [], acceptDefaults: true, stand.AdminId);
        await restarted.ResolveReviewAsync(other, [], acceptDefaults: true, stand.AdminId);
        (await restarted.GetAsync(other))!.NeedsPassword.Should().BeTrue();

        await restarted.ContinueAsync(first, OffboardingStand.AdminPassword, stand.AdminId);
        await restarted.AdvanceAsync(other);

        (await restarted.GetAsync(first))!.Open.Should().BeFalse();
        (await restarted.GetAsync(other))!.Open.Should().BeFalse("the rotation proposed after its review serves it too");
        (await CountAsync(stand.Accounts, "SELECT COUNT(DISTINCT proposed_event_id) FROM tbl_dek_rotation_state")).Should().Be(1, "one rotation served both removals");
    }

    [Fact]
    public async Task A_rotation_proposed_before_a_review_was_done_does_not_serve_that_removal()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        int second;
        using (var scope = stand.Accounts.Services.CreateScope())
            second = (await scope.ServiceProvider.GetRequiredService<UserService>()
                .CreateUserAsync("second", "Second Leaver", OffboardingStand.OtherPassword, UserRoles.Superadmin, passwordHint: "hint")).Id;
        var first = await stand.StartRemovalAsync();
        var other = await stand.StartRemovalAsync(userId: second);
        stand.RekeyNode(stand.AccountsId);

        await stand.AcceptReviewAsync(first);     // rotates now, while the other review is still open
        await stand.AcceptReviewAsync(other);     // so this one rotates again, after its own review

        (await stand.ViewAsync(other)).Open.Should().BeFalse();
        (await CountAsync(stand.Accounts, "SELECT COUNT(DISTINCT proposed_event_id) FROM tbl_dek_rotation_state")).Should().Be(2,
            "a rotation before a review would have re-sealed doors that review removes");
    }

    [Fact]
    public async Task The_api_starts_a_removal_instead_of_demoting_or_deleting_a_superadmin_and_needs_the_administrators_password()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        using var admin = OffboardingStand.ClientAs(stand.Accounts, stand.AdminId);

        var noPassword = await admin.PutAsJsonAsync($"/api/users/{stand.LeaverId}", new { displayName = "Lee", role = "user", allowOpenRole = true });
        noPassword.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await UserAsync(stand.Accounts, stand.LeaverId))!.Role.Should().Be(UserRoles.Superadmin, "nothing was changed");

        using var deleteNoPassword = new HttpRequestMessage(HttpMethod.Delete, $"/api/users/{stand.LeaverId}");
        (await admin.SendAsync(deleteNoPassword)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/users/{stand.LeaverId}")
        {
            Content = JsonContent.Create(new { adminPassword = OffboardingStand.AdminPassword }),
        };
        var started = await admin.SendAsync(delete);
        started.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await UserAsync(stand.Accounts, stand.LeaverId))!.AccountState.Should().Be(UserAccountStates.Offboarding);
        (await UserAsync(stand.Accounts, stand.LeaverId))!.IsActive.Should().BeTrue("deleted only at the end");

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/users");
        list.EnumerateArray().Single(u => u.GetProperty("id").GetInt32() == stand.LeaverId)
            .GetProperty("removalStatus").GetString().Should().StartWith("being removed, step");

        var processes = await admin.GetFromJsonAsync<JsonElement>("/api/offboarding");
        processes.GetArrayLength().Should().Be(1);

        // The person signs in no more, and is nobody to the API.
        using var leaver = OffboardingStand.ClientAs(stand.Accounts, stand.LeaverId);
        (await leaver.GetAsync("/api/offboarding")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_cli_requests_are_what_the_api_takes_and_its_answer_carries_every_field_the_cli_prints()
    {
        // bmb user remove-superadmin / bmb offboarding status|continue (OffboardingCommand) send exactly these bodies, as a machine-local caller with the
        // internal key that names the acting superadmin; the CLI tests pin the bodies, this pins the API's side of the contract.
        await using var stand = await OffboardingStand.CreateAsync();
        using var machine = stand.Accounts.CreateClient();

        var users = await machine.GetFromJsonAsync<JsonElement>("/api/users");
        var leaverId = users.EnumerateArray().Single(u => u.GetProperty("username").GetString() == "leaver").GetProperty("id").GetInt32();
        var nobody = await machine.PostAsJsonAsync("/api/offboarding", new { userId = leaverId, target = "delete", password = OffboardingStand.AdminPassword });
        nobody.StatusCode.Should().Be(HttpStatusCode.BadRequest, "a machine-local caller names the superadmin who acts");
        var started = await machine.PostAsJsonAsync("/api/offboarding", new
        {
            userId = leaverId, target = "delete", role = (string?)null, folderRules = (object?)null, allowOpenRole = false,
            password = OffboardingStand.AdminPassword, actingUser = "admin",
        });
        started.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var view = await started.Content.ReadFromJsonAsync<JsonElement>();
        var id = view.GetProperty("processId").GetString();

        view = await machine.GetFromJsonAsync<JsonElement>($"/api/offboarding/{id}");
        view.GetProperty("person").GetString().Should().Contain("leaver");
        view.GetProperty("status").GetString().Should().StartWith("being removed, step");
        view.GetProperty("open").GetBoolean().Should().BeTrue();
        view.GetProperty("needsPassword").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        var step = view.GetProperty("steps")[1];
        step.GetProperty("number").GetInt32().Should().Be(1);
        step.GetProperty("title").GetString().Should().NotBeNullOrEmpty();
        step.GetProperty("state").GetString().Should().NotBeNullOrEmpty();
        step.GetProperty("sentence").ValueKind.Should().Be(JsonValueKind.String);
        step.GetProperty("waits").ValueKind.Should().Be(JsonValueKind.Array);
        view.GetProperty("review").ValueKind.Should().Be(JsonValueKind.Array);
        view.GetProperty("bridges").ValueKind.Should().Be(JsonValueKind.Array);
        view.GetProperty("reminders").ValueKind.Should().Be(JsonValueKind.Array);

        stand.RekeyNode(stand.AccountsId);
        (await machine.PostAsJsonAsync($"/api/offboarding/{id}/continue", new { })).StatusCode.Should().Be(HttpStatusCode.OK);
        var review = await machine.PostAsJsonAsync($"/api/offboarding/{id}/review", new { acceptDefaults = true, actingUser = "admin" });
        review.StatusCode.Should().Be(HttpStatusCode.OK);
        var closed = await review.Content.ReadFromJsonAsync<JsonElement>();
        closed.GetProperty("open").GetBoolean().Should().BeFalse("the password held since the start changed the master key, and S5 closed");
        closed.GetProperty("status").GetString().Should().Be("deleted");
        var wait = closed.GetProperty("steps")[4].GetProperty("waits");
        foreach (var w in wait.EnumerateArray())
            (w.TryGetProperty("nodeName", out var nodeName) && w.TryGetProperty("why", out var why)).Should().BeTrue("the CLI prints both");
    }

    [Fact]
    public async Task A_one_person_bank_never_sees_any_of_it()
    {
        var node = new BmbWebApplicationFactory();
        try
        {
            await node.InitializeNodeAsync("Alone", OffboardingStand.AdminPassword);
            (await node.Services.GetRequiredService<SessionService>().UnlockAsync(OffboardingStand.AdminPassword)).Should().BeTrue();
            int adminId;
            using (var scope = node.Services.CreateScope())
                adminId = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!.Id;
            using var admin = OffboardingStand.ClientAs(node, adminId);

            var list = await admin.GetFromJsonAsync<JsonElement>("/api/users");
            list.EnumerateArray().Single().GetProperty("removalStatus").ValueKind.Should().Be(JsonValueKind.Null);
            (await admin.GetFromJsonAsync<JsonElement>("/api/offboarding")).GetArrayLength().Should().Be(0);

            var self = await admin.PostAsJsonAsync("/api/offboarding", new { userId = adminId, target = "delete", password = OffboardingStand.AdminPassword });
            self.StatusCode.Should().Be(HttpStatusCode.Conflict, "nobody removes themself, and never the last superadmin");
        }
        finally
        {
            node.Dispose();
        }
    }
}
