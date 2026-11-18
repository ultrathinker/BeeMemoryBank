using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Api.Services.Offboarding;
using Dapper;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests.Offboarding;

/// <summary>
/// The leaver's toolkit (release 2.12.0, plan 5): everything a removed superadmin could still try, each of which must FAIL for them. Items 1-7 right
/// after S0, the rest at the step that closes them. The numbers follow the plan's list.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class LeaverToolkitTests
{
    private static async Task<T> InScopeAsync<T>(BmbWebApplicationFactory node, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = node.Services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    private static async Task<long> CountAsync(BmbWebApplicationFactory node, string sql, object? args = null) =>
        await InScopeAsync(node, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteScalarAsync<long>(sql, args);
        });

    // (1) the old password on the accounts node, (2) an open browser session, (3) an agent key, (4) a remote token: right after S0.
    [Fact]
    public async Task Right_after_the_lock_out_the_password_the_open_session_the_agent_key_and_the_remote_token_all_fail()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        using var leaverClient = OffboardingStand.ClientAs(stand.Accounts, stand.LeaverId);

        // What the person holds before: a session (its stamp), an agent key, a remote token.
        var stampBefore = (await leaverClient.GetFromJsonAsync<JsonElement>("/api/users/me/stamp")).GetProperty("stamp").GetString();
        var agent = await leaverClient.PostAsJsonAsync("/api/agents", new { name = "Lee's helper", description = "assistant" });
        agent.EnsureSuccessStatusCode();
        var agentKey = (await agent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString()!;
        (await InScopeAsync(stand.Accounts, sp => sp.GetRequiredService<BeeMemoryBank.Core.Interfaces.IAgentRepository>()
            .GetByKeyHashAsync(BeeMemoryBank.Crypto.AgentKeyHelper.ComputeKeyHash(agentKey)))).Should().NotBeNull("the agent works before the removal");
        await InScopeAsync(stand.Accounts, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteAsync(
                @"INSERT INTO tbl_remote_api_token (id, user_id, token_hash, label, created_at, last_used_at, expires_at)
                  VALUES (@id, @u, 'hash-of-a-token', 'phone', @now, @now, @later)",
                new { id = Guid.NewGuid().ToString("D"), u = stand.LeaverId, now = DateTime.UtcNow.ToString("O"), later = DateTime.UtcNow.AddDays(90).ToString("O") });
        });

        await stand.StartRemovalAsync();

        // (1) the old password.
        using (var anonymous = stand.Accounts.CreateClient())
        {
            var login = await anonymous.PostAsJsonAsync("/api/session/login", new { username = "leaver", password = OffboardingStand.LeaverPassword });
            login.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        // (2) the open session: the stamp changed (the Web signs the cookie out at its next check), and the API gives the forwarded id no rights at
        // all. While the stamp check itself fails (an API hiccup) the Web refuses superadmin pages: WebStampStrictnessTests proves that half.
        var stampAfter = await leaverClient.GetAsync("/api/users/me/stamp");
        stampAfter.StatusCode.Should().Be(HttpStatusCode.OK, "the account still exists until S5: the check answers, with a new stamp");
        (await stampAfter.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stamp").GetString().Should().NotBe(stampBefore);
        (await leaverClient.GetAsync("/api/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // (3) the agent key: no agent answers to it any more (the middleware finds nobody: no identity, no wrapped key to open a locked node).
        (await InScopeAsync(stand.Accounts, sp => sp.GetRequiredService<BeeMemoryBank.Core.Interfaces.IAgentRepository>()
            .GetByKeyHashAsync(BeeMemoryBank.Crypto.AgentKeyHelper.ComputeKeyHash(agentKey)))).Should().BeNull();
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_agent WHERE owner_user_id = @u AND (status = 'A' OR encrypted_dek IS NOT NULL)",
            new { u = stand.LeaverId })).Should().Be(0);
        using (var agentClient = stand.Accounts.Server.CreateClient())
        {
            agentClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agentKey);
            (await agentClient.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" })).IsSuccessStatusCode.Should().BeFalse();
        }
        // (4) the remote token.
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_remote_api_token WHERE user_id = @u", new { u = stand.LeaverId })).Should().Be(0);
    }

    // (5) their own recovery key, right after S0: it opens the node no more, and it resets no password.
    [Fact]
    public async Task Right_after_the_lock_out_their_own_recovery_key_opens_nothing_and_resets_nothing()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        string recoveryKey;
        using (var leaverClient = OffboardingStand.ClientAs(stand.Accounts, stand.LeaverId))
        {
            var issued = await leaverClient.PostAsync("/api/keys/add-recovery", null);
            issued.EnsureSuccessStatusCode();
            recoveryKey = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryKey").GetString()!;
        }
        var session = stand.Accounts.Services.GetRequiredService<SessionService>();
        session.Lock();
        (await session.UnlockAsync(recoveryKey)).Should().BeTrue("the key works before the removal");

        await stand.StartRemovalAsync();

        session.Lock();
        (await session.UnlockAsync(recoveryKey)).Should().BeFalse("the person's recovery keys went in the lock-out transaction");
        (await session.UnlockAsync(OffboardingStand.AdminPassword)).Should().BeTrue();
        using var anonymous = stand.Accounts.CreateClient();
        var reset = await anonymous.PostAsJsonAsync("/api/session/recover-access", new
        {
            username = "leaver", recoveryKey, newPassword = "Leaver-New-Password-2", clientIp = "203.0.113.9", passwordHint = "a new hint",
        });
        reset.IsSuccessStatusCode.Should().BeFalse("no password of theirs can be set with it");
        (await anonymous.PostAsJsonAsync("/api/session/login", new { username = "leaver", password = "Leaver-New-Password-2" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // (12a) a snapshot from before the removal is refused once the rotation (S4) is done - on a sealed accounts node, where restores are allowed at all.
    [Fact]
    public async Task A_snapshot_from_before_the_removal_is_refused_after_the_rotation_and_changes_nothing()
    {
        await using var stand = await OffboardingStand.CreateAsync(sealedAccounts: true);
        using var machine = stand.Accounts.CreateClient();
        var created = await machine.PostAsync("/api/snapshots", null);
        created.EnsureSuccessStatusCode();
        var fileName = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fileName").GetString()!;

        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        await stand.AcceptReviewAsync(id);
        (await stand.ViewAsync(id)).Open.Should().BeFalse("closed: the master key changed and the account was deleted");

        var restore = await machine.PostAsJsonAsync("/api/snapshots/restore", new { fileName, masterPassword = OffboardingStand.AdminPassword });

        restore.IsSuccessStatusCode.Should().BeFalse("the snapshot is sealed under a master key the network no longer has");
        (await restore.Content.ReadAsStringAsync()).Should().NotContain("   at ", "no exception text reaches the answer");
        // The refused restore closes the node (as every restore does at its end); it opens again with the same password.
        var session = stand.Accounts.Services.GetRequiredService<SessionService>();
        if (!session.IsUnlocked) (await session.UnlockAsync(OffboardingStand.AdminPassword)).Should().BeTrue();
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_user WHERE id = @id AND is_active = 1", new { id = stand.LeaverId }))
            .Should().Be(0, "the leaver did not come back");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_removed_person")).Should().Be(1);
    }

    /// <summary>
    /// (12b) A snapshot from before the removal, restored with keep-identity between S0 and S4 - while the old master key still opens it - is refused
    /// (that mode was removed: it would bring back the node's identity key and the person's account and doors as they were). The refusal comes before the
    /// password is looked at; the removal, the lock-out and the tombstone are untouched.
    /// </summary>
    [Theory]
    [InlineData("keep-identity")]
    [InlineData(null)]   // an older client that says standaloneMode: false
    public async Task A_pre_removal_snapshot_restored_with_keep_identity_between_S0_and_S4_is_refused(string? mode)
    {
        await using var stand = await OffboardingStand.CreateAsync(sealedAccounts: true);
        using var machine = stand.Accounts.CreateClient();
        var created = await machine.PostAsync("/api/snapshots", null);
        created.EnsureSuccessStatusCode();
        var fileName = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fileName").GetString()!;
        Guid identityBefore;
        using (var scope = stand.Accounts.Services.CreateScope())
            identityBefore = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;

        var id = await stand.StartRemovalAsync();
        var view = await stand.ViewAsync(id);
        view.Open.Should().BeTrue();
        view.Steps[4].State.Should().NotBe("done", "the master key has not changed yet: the old snapshot still opens with it");

        var restore = mode is null
            ? await machine.PostAsJsonAsync("/api/snapshots/restore", new { fileName, masterPassword = OffboardingStand.AdminPassword, createBackupFirst = false, standaloneMode = false })
            : await machine.PostAsJsonAsync("/api/snapshots/restore", new { fileName, masterPassword = OffboardingStand.AdminPassword, createBackupFirst = false, mode });

        restore.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await restore.Content.ReadAsStringAsync()).Should().Contain("no longer offered").And.NotContain("   at ");
        stand.Accounts.Services.GetRequiredService<SessionService>().IsUnlocked.Should().BeTrue("refused before the password is looked at");
        using (var scope = stand.Accounts.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId.Should().Be(identityBefore);
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_user WHERE id = @id AND account_state = 'offboarding'", new { id = stand.LeaverId }))
            .Should().Be(1, "the person is still locked out");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_removed_person")).Should().Be(1);
        (await stand.ViewAsync(id)).Open.Should().BeTrue("the removal goes on");
    }

    // a node whose only person door is the removed person's (an old backup of the accounts node, restored and re-joined).
    private const string IslandPassword = "Island-Password-1";

    private static async Task<(BmbWebApplicationFactory Node, Guid Person)> IslandOfThePersonAsync()
    {
        var island = new BmbWebApplicationFactory();
        await island.InitializeNodeAsync("Island", IslandPassword);
        (await island.Services.GetRequiredService<SessionService>().UnlockAsync(IslandPassword)).Should().BeTrue();
        var person = Guid.NewGuid();
        await InScopeAsync(island, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteAsync("UPDATE tbl_user SET person_id = @p WHERE username = 'admin'", new { p = person.ToString("D") });
        });
        return (island, person);
    }

    private static Task<int> TombstoneAsync(BmbWebApplicationFactory node, Guid person) =>
        InScopeAsync(node, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.ExecuteAsync("INSERT INTO tbl_removed_person (person_id, outcome, removed_at) VALUES (@p, 'deleted', @t)",
                new { p = person.ToString("D"), t = DateTime.UtcNow.ToString("O") });
        });

    [Fact]
    public async Task A_removed_persons_door_that_must_stay_as_the_last_door_of_a_node_opens_no_join()
    {
        var (island, person) = await IslandOfThePersonAsync();
        using var _ = island;
        await TombstoneAsync(island, person);
        await BeeMemoryBank.Api.Startup.ApiStartWork.EnforceTombstonesAsync(island.Services);

        // Nothing else opens this node: the door stays (a node with no door at all is lost for everybody), the account is locked out.
        (await CountAsync(island, "SELECT COUNT(*) FROM tbl_key_slot")).Should().Be(1);
        (await CountAsync(island, "SELECT COUNT(*) FROM tbl_user WHERE username = 'admin' AND account_state = 'offboarding'")).Should().Be(1);

        // Joining with that password would hand a new device the master key and the mesh: refused, as sign-in and the recovery reset are.
        using var thief = new BmbWebApplicationFactory();
        using var islandClient = island.CreateClient();
        var join = () => thief.JoinNodeAsync(islandClient, "Thief", IslandPassword);
        await join.Should().ThrowAsync<HttpRequestException>();
        (await CountAsync(island, "SELECT COUNT(*) FROM tbl_whitelist")).Should().Be(0);
    }

    [Fact]
    public async Task A_removed_persons_last_person_door_goes_when_a_machine_door_keeps_the_node_openable()
    {
        var (island, person) = await IslandOfThePersonAsync();
        using var _ = island;
        var secret = SecureRandom.GetBytes(32);
        int machineSlot;
        using (var scope = island.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var dek = sp.GetRequiredService<SessionService>().GetMasterDek();
            try
            {
                machineSlot = await BeeMemoryBank.Vault.Sealed.SlotDoors.CreateSlotAsync(sp.GetRequiredService<IKeySlotRepository>(),
                    sp.GetRequiredService<IDbConnectionFactory>(), sp.GetService<BeeMemoryBank.Vault.Sealed.IUnlockMaterialMirror>(),
                    BeeMemoryBank.Crypto.Doors.NewSecretSlot(KeySlotTypes.OsAutoUnlock, secret, dek), dek);
            }
            finally { Array.Clear(dek); }
        }
        await TombstoneAsync(island, person);

        await BeeMemoryBank.Api.Startup.ApiStartWork.EnforceTombstonesAsync(island.Services);

        (await CountAsync(island, "SELECT COUNT(*) FROM tbl_user WHERE username LIKE 'admin%' AND is_active = 1")).Should().Be(0, "the tombstone deleted the account");
        (await CountAsync(island, "SELECT COUNT(*) FROM tbl_key_slot WHERE slot_type = 'user'")).Should().Be(0, "and the person's door with it");
        var kept = (await InScopeAsync(island, sp => sp.GetRequiredService<IKeySlotRepository>().GetAllAsync())).Single(s => s.SlotId == machineSlot);
        var current = island.Services.GetRequiredService<SessionService>().GetMasterDek();
        BeeMemoryBank.Crypto.Doors.OpenSlot(kept, secret).Should().Equal(current, "the machine's door still opens the node");
        Array.Clear(current);
    }

    // the review finds a promotion by the role that was APPLIED, and only when the role changed.
    [Fact]
    public async Task The_review_finds_a_promotion_by_the_applied_role_and_not_a_role_that_did_not_change()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        int dora, eve;
        using (var scope = stand.Accounts.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserService>();
            dora = (await users.CreateUserAsync("dora", "Dora", "Dora-Password-1", UserRoles.User, passwordHint: "her hint")).Id;
            eve = (await users.CreateUserAsync("eve", "Eve", "Eve-Password-1", UserRoles.Superadmin, passwordHint: "her hint")).Id;
        }
        using (var leaver = OffboardingStand.ClientAs(stand.Accounts, stand.LeaverId))
        {
            // A promotion written the way a script may send it (the service canonicalizes the case; a padded name is refused with 400 before
            // anything changes), and a "change" that changes nothing.
            (await leaver.PutAsJsonAsync($"/api/users/{dora}", new { displayName = "Dora", role = " Superadmin " })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await leaver.PutAsJsonAsync($"/api/users/{dora}", new { displayName = "Dora", role = "SuperAdmin" })).EnsureSuccessStatusCode();
            (await leaver.PutAsJsonAsync($"/api/users/{eve}", new { displayName = "Eve", role = "superadmin" })).EnsureSuccessStatusCode();
        }
        (await InScopeAsync(stand.Accounts, sp => sp.GetRequiredService<IUserRepository>().GetByIdAsync(dora)))!.Role.Should().Be(UserRoles.Superadmin);

        var id = await stand.StartRemovalAsync();
        var review = (await stand.ViewAsync(id)).Review;

        review.Single(i => i.Id == $"superadmin:{dora}").DefaultRemove.Should().BeTrue("the person made her a superadmin");
        review.Single(i => i.Id == $"superadmin:{eve}").DefaultRemove.Should().BeFalse("she was a superadmin before; the person changed nothing");
    }

    // an account the person created, made a superadmin after the review listed it, is not "removed" by a decision that deletes nothing.
    [Fact]
    public async Task An_account_that_became_a_superadmin_after_the_review_listed_it_is_listed_again_as_a_superadmin()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        int fred;
        using (var leaver = OffboardingStand.ClientAs(stand.Accounts, stand.LeaverId))
        {
            var created = await leaver.PostAsJsonAsync("/api/users", new
            {
                username = "fred", password = "Fred-Password-1", displayName = "Fred", role = "user", passwordHint = "the colour of the sea",
            });
            created.EnsureSuccessStatusCode();
            fred = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        }
        var id = await stand.StartRemovalAsync();
        (await stand.ViewAsync(id)).Review.Should().Contain(i => i.Id == $"user:{fred}" && i.DefaultRemove, "an account the person created");

        // Promoted in the meantime (by somebody who stays), then the old "remove" decision of the ordinary account arrives. The review follows the
        // accounts: it was built again, and the ordinary account's item, undecided, is not on it any more.
        using (var admin = OffboardingStand.ClientAs(stand.Accounts, stand.AdminId))
            (await admin.PutAsJsonAsync($"/api/users/{fred}", new { displayName = "Fred", role = "superadmin" })).EnsureSuccessStatusCode();
        var late = () => stand.Service().ResolveReviewAsync(id, [new OffboardingDecision($"user:{fred}", "remove", null, null)], acceptDefaults: false, stand.AdminId);
        await late.Should().ThrowAsync<OffboardingRefusedException>().WithMessage("That item is not on this review.");

        var view = await stand.ViewAsync(id);
        (await InScopeAsync(stand.Accounts, sp => sp.GetRequiredService<IUserRepository>().GetByIdAsync(fred)))!.IsActive.Should().BeTrue();
        view.Review.Should().NotContain(i => i.Id == $"user:{fred}" && i.Applied, "nothing was removed");
        var again = view.Review.Single(i => i.Id == $"superadmin:{fred}");
        again.Decision.Should().BeNull("a superadmin is decided as one: removing them starts their own removal");
        again.DefaultRemove.Should().BeTrue();
        view.Steps[3].State.Should().NotBe("done");
    }

    // (4) a live remote token: it answers before S0, and after S0 neither it nor a new one issued with the old password answers anything.
    [Fact]
    public async Task Right_after_the_lock_out_a_remote_token_answers_nothing_and_the_old_password_gets_no_new_one()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        using var anonymous = stand.Accounts.Server.CreateClient();   // a remote node: no internal key, no forwarded person
        async Task<HttpResponseMessage> IssueAsync() => await anonymous.PostAsJsonAsync("/api/auth/remote-token",
            new { username = "leaver", password = OffboardingStand.LeaverPassword, label = "friend's bank" });
        async Task<HttpStatusCode> AskAsync(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/folders/accessible");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (await anonymous.SendAsync(request)).StatusCode;
        }

        var issued = await IssueAsync();
        issued.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        (await AskAsync(token)).Should().Be(HttpStatusCode.OK, "the token works before the removal");

        await stand.StartRemovalAsync();

        (await AskAsync(token)).Should().NotBe(HttpStatusCode.OK, "the token went with the lock-out");
        var again = await IssueAsync();
        again.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a person being removed gets no new token with the old password");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_remote_api_token WHERE user_id = @u", new { u = stand.LeaverId })).Should().Be(0);
    }

    // (6) an unknown-owner key and an un-migrated legacy slot, after S3.
    [Fact]
    public async Task An_ownerless_key_and_the_old_shared_master_password_stop_working_after_the_review()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        const string oldMasterPassword = "Old-Master-Password-1";
        // A legacy 'password' slot (from before user accounts) and a recovery slot nobody owns.
        await InScopeAsync(stand.Accounts, async sp =>
        {
            var slots = sp.GetRequiredService<IKeySlotRepository>();
            var dek = sp.GetRequiredService<SessionService>().GetMasterDek();
            try
            {
                var salt = KeyDerivation.GenerateSalt();
                var kek = KeyDerivation.DeriveKek(oldMasterPassword, salt);
                var (enc, iv) = MasterKeyManager.WrapMasterDek(dek, kek);
                await slots.CreateAsync(new MasterKeyStore
                {
                    SlotType = "password", EncryptedMasterDek = enc, IV = iv, Salt = salt, ArgonMemory = CryptoConstants.DefaultArgonMemory,
                    ArgonIterations = CryptoConstants.DefaultArgonIterations, ArgonParallelism = CryptoConstants.DefaultArgonParallelism, CreatedAt = DateTime.UtcNow,
                });
                var rsalt = KeyDerivation.GenerateSalt();
                var rkek = KeyDerivation.DeriveKek("Recovery-Key-Nobody-Owns-1", rsalt);
                var (renc, riv) = MasterKeyManager.WrapMasterDek(dek, rkek);
                return await slots.CreateAsync(new MasterKeyStore
                {
                    SlotType = "recovery", EncryptedMasterDek = renc, IV = riv, Salt = rsalt, ArgonMemory = CryptoConstants.DefaultArgonMemory,
                    ArgonIterations = CryptoConstants.DefaultArgonIterations, ArgonParallelism = CryptoConstants.DefaultArgonParallelism, CreatedAt = DateTime.UtcNow,
                });
            }
            finally { Array.Clear(dek); }
        });

        var id = await stand.StartRemovalAsync();
        stand.RekeyNode(stand.AccountsId);
        await stand.Service().AdvanceAsync(id);
        var review = (await stand.ViewAsync(id)).Review;
        review.Where(i => i.Kind == "slot").Should().HaveCount(2).And.OnlyContain(i => i.DefaultRemove, "the unknown-owner set is pre-selected for removal");

        // Keeping one needs a typed reason.
        var keepWithoutReason = () => stand.Service().ResolveReviewAsync(id, [new(review.First(i => i.Kind == "slot").Id, "keep")], false, stand.AdminId);
        await keepWithoutReason.Should().ThrowAsync<BeeMemoryBank.Api.Services.Offboarding.OffboardingRefusedException>().WithMessage("Say why*");

        await stand.AcceptReviewAsync(id);
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_key_slot WHERE slot_type IN ('password', 'recovery')")).Should().Be(0);
        (await stand.Accounts.Services.GetRequiredService<SessionService>().TryOpenWithRecoveryKeyAsync("Recovery-Key-Nobody-Owns-1")).Should().BeNull();
    }

    // (7) the personal full device: pull, push, and a sync token issued before the revoke.
    [Fact]
    public async Task The_persons_own_full_device_is_cut_off_at_the_lock_out()
    {
        await using var stand = await OffboardingStand.CreateAsync(personal: true);
        var (tokenBefore, _) = await OffboardingStand.AuthenticateAsync(stand.Personal!, stand.Accounts);
        tokenBefore.Should().NotBeNull("the device is a peer before the removal");

        await stand.StartRemovalAsync();

        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_whitelist WHERE node_id = @n COLLATE NOCASE AND status = 'R'",
            new { n = stand.PersonalId.ToString("D") })).Should().Be(1, "every device personal of the person is revoked at S0");

        using var http = stand.Accounts.CreateClient();
        using (var pull = new HttpRequestMessage(HttpMethod.Get, "/api/sync/events?afterSequence=0"))
        {
            pull.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenBefore);
            (await http.SendAsync(pull)).IsSuccessStatusCode.Should().BeFalse("a token issued before the revoke dies with it");
        }
        var (tokenAfter, status) = await OffboardingStand.AuthenticateAsync(stand.Personal!, stand.Accounts);
        tokenAfter.Should().BeNull("the device cannot sign in to sync any more");
        status.Should().NotBe(HttpStatusCode.OK);

        // The device's own sync round receives nothing new: new data written after the removal never reaches it.
        Guid article;
        using (var scope = stand.Accounts.Services.CreateScope())
            article = (await scope.ServiceProvider.GetRequiredService<ArticleService>().CreateAsync("After the removal", "/News", [], "new data")).Id;
        try { await OffboardingStand.SyncAsync(stand.Personal!, stand.Accounts); } catch { /* refused: that is the point */ }
        (await CountAsync(stand.Personal!, "SELECT COUNT(*) FROM tbl_article WHERE id = @a", new { a = article })).Should().Be(0, "no new data reaches it");

        // Its own ownership it cannot change at all (the device's logger refuses it at the source; a hand-made event would meet the refusals below).
        Guid own;
        using (var scope = stand.Personal!.Services.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<IEventLogger>();
            var ownership = () => logger.LogWhitelistUpdateAsync(stand.PersonalId, null, null, ownership: OwnershipKinds.Shared);
            await ownership.Should().ThrowAsync<InvalidOperationException>();
            // A control event of its own (a new address) pushed with the token from before, and by its own sync round.
            await logger.LogWhitelistUpdateAsync(stand.PersonalId, "https://lee.example:5300", null);
            var evt = (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetRecentAsync(20, 0, "whitelist_update"))
                .First(e => e.NodeId == stand.PersonalId);
            own = evt.EventId;
            using var push = new HttpRequestMessage(HttpMethod.Post, "/api/sync/events") { Content = JsonContent.Create(new[] { evt }) };
            push.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenBefore);
            (await http.SendAsync(push)).IsSuccessStatusCode.Should().BeFalse("a push with the token from before is refused");
        }
        try { await OffboardingStand.SyncAsync(stand.Personal!, stand.Accounts); } catch { /* refused */ }
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_event WHERE event_id = @e COLLATE NOCASE", new { e = own.ToString() }))
            .Should().Be(0, "the control event never reached the accounts node");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_whitelist WHERE node_id = @n COLLATE NOCASE AND status = 'R' AND ownership = 'personal'",
            new { n = stand.PersonalId.ToString("D") })).Should().Be(1, "it stays revoked, and the person's");
    }

    // (10) the old master key and a copy of the database taken after S4.
    [Fact]
    public async Task The_old_master_key_opens_nothing_in_a_database_copy_taken_after_the_rotation()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        var oldDek = stand.Accounts.Services.GetRequiredService<SessionService>().GetMasterDek();
        try
        {
            var id = await stand.StartRemovalAsync();
            stand.RekeyNode(stand.AccountsId);
            await stand.AcceptReviewAsync(id);
            (await stand.ViewAsync(id)).Open.Should().BeFalse();

            var copy = Path.Combine(stand.Accounts.DataPath, "copy-after-removal.db");
            await InScopeAsync(stand.Accounts, async sp =>
            {
                using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
                return await conn.ExecuteAsync($"VACUUM INTO '{copy.Replace("'", "''")}'");
            });
            using var copied = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy};Pooling=False");
            copied.Open();
            var sentinel = await copied.ExecuteScalarAsync<byte[]>("SELECT sentinel_value FROM tbl_node_identity LIMIT 1");
            sentinel.Should().NotBeNull();
            MasterKeyManager.VerifySentinel(sentinel!, oldDek).Should().BeFalse("the master key changed at S4");
            (await copied.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_key_slot WHERE door_version = 1")).Should().Be(0,
                "no old-format door, so no bridge from the old key in the copy");
            foreach (var (type, box) in await copied.QueryAsync<(string, byte[])>("SELECT slot_type, encrypted_master_dek FROM tbl_key_slot"))
                box.Length.Should().BeGreaterThan(48, $"the {type} door is a sealed box, which a master key does not open");
        }
        finally
        {
            Array.Clear(oldDek);
        }
    }

    // (11) a planted superadmin, device, key, agent and invitation must be resolved in S3.
    [Fact]
    public async Task What_the_person_planted_is_listed_preselected_and_must_be_resolved_before_the_rotation()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        using var leaverClient = OffboardingStand.ClientAs(stand.Accounts, stand.LeaverId);

        // Planted while a superadmin: an account made a superadmin, a device joined with the person's password, an agent for somebody else.
        var planted = await leaverClient.PostAsJsonAsync("/api/users", new
        {
            username = "accomplice", displayName = "Accomplice", password = "Accomplice-Pass-1", role = "superadmin", passwordHint = "a hint",
        });
        planted.EnsureSuccessStatusCode();
        var plantedId = (await planted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var device = new BmbWebApplicationFactory();
        try
        {
            using (var accounts = stand.Accounts.CreateClient())
                await device.JoinNodeAsync(accounts, "Lee's spare", OffboardingStand.LeaverPassword);
            Guid deviceId;
            using (var scope = device.Services.CreateScope())
                deviceId = (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
            await stand.SetOwnershipAsync(deviceId, OwnershipKinds.Shared, null);
            var agentId = await InScopeAsync(stand.Accounts, sp => sp.GetRequiredService<BeeMemoryBank.Core.Interfaces.IAgentRepository>().CreateAsync(new Agent
            {
                Name = "Backdoor", KeyPrefix = "bee_xxxx", KeyHash = "hash-" + Guid.NewGuid().ToString("N"), OwnerUserId = stand.AdminId, Status = "A",
                CreatedByUserId = stand.LeaverId, CreatedAt = DateTime.UtcNow,
            }));

            var id = await stand.StartRemovalAsync();
            stand.RekeyNode(stand.AccountsId);
            stand.RekeyNode(deviceId);   // a shared full node: S1 waits for its new key too,
            stand.SharedDoors.Confirmed[deviceId] = true;   // and S2 for its list
            await stand.Service().AdvanceAsync(id);
            var review = (await stand.ViewAsync(id)).Review;
            review.Single(i => i.Id == $"superadmin:{plantedId}").DefaultRemove.Should().BeTrue("made a superadmin by this person");
            review.Single(i => i.Id == $"device:{deviceId:D}").DefaultRemove.Should().BeTrue("joined with this person's password");
            review.Single(i => i.Id == $"agent:{agentId}").DefaultRemove.Should().BeTrue("made by this person");

            // The pre-selected choices do not decide a superadmin (that is a removal of its own): the step waits, no rotation yet.
            await stand.AcceptReviewAsync(id);
            var view = await stand.ViewAsync(id);
            view.Step.Should().Be(BeeMemoryBank.Api.Services.Offboarding.OffboardingSteps.Review);
            (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_dek_rotation_state")).Should().Be(0);
            (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_whitelist WHERE node_id = @n COLLATE NOCASE AND status = 'R'",
                new { n = deviceId.ToString("D") })).Should().Be(1, "the planted device is revoked");
            (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_agent WHERE id = @a AND status = 'A'", new { a = agentId })).Should().Be(0);

            // Removing the planted superadmin starts their removal; this one's review is then done.
            await stand.Service().ResolveReviewAsync(id,
                [new($"superadmin:{plantedId}", "remove", Target: new("delete", null, null, false))], false, stand.AdminId, OffboardingStand.AdminPassword);
            (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_offboarding")).Should().Be(2);
            (await InScopeAsync(stand.Accounts, sp => sp.GetRequiredService<IUserRepository>().GetByIdAsync(plantedId)))!.IsBeingRemoved.Should().BeTrue();
            (await stand.ViewAsync(id)).Steps[3].State.Should().Be("done");
        }
        finally
        {
            device.Dispose();
        }
    }

    // (11) continued: a recovery key the person made for somebody else, and invitations - theirs are withdrawn at S0, another one's is listed.
    [Fact]
    public async Task A_recovery_key_the_person_made_for_another_is_removed_and_their_invitations_end_at_the_lock_out()
    {
        await using var stand = await OffboardingStand.CreateAsync();
        int keyForAdmin;
        using (var scope = stand.Accounts.Services.CreateScope())
        {
            // Since 2.12 a key is its issuer's own; one made for another person is from before that rule (or a tool): its creator is on record.
            keyForAdmin = (await scope.ServiceProvider.GetRequiredService<KeyManagementService>().IssueRecoveryKeyAsync(stand.AdminId, "made by Lee")).SlotId;
            using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            await conn.ExecuteAsync("UPDATE tbl_key_slot SET created_by_user_id = @lee WHERE slot_id = @s", new { lee = stand.LeaverId, s = keyForAdmin });
            foreach (var (inviteId, by) in new[] { ("invite-of-lee", stand.LeaverId), ("invite-of-admin", stand.AdminId) })
                await conn.ExecuteAsync(
                    @"INSERT INTO tbl_blind_invite (invite_id, secret_hash, label, source_name, address, trust, created_by, created_at, expires_at, state)
                      VALUES (@id, 'hash', @label, 'Accounts', 'https://accounts.test:5300', 'pin', @by, @now, @later, 'issued')",
                    new { id = inviteId, label = "Phone of " + inviteId, by = by.ToString(System.Globalization.CultureInfo.InvariantCulture),
                          now = DateTime.UtcNow.ToString("O"), later = DateTime.UtcNow.AddDays(1).ToString("O") });
        }

        var id = await stand.StartRemovalAsync();

        // S0 ended the person's own invitation in its transaction; the other one waits for the review.
        (await InScopeAsync(stand.Accounts, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.QueryFirstAsync<(string State, string? By)>("SELECT state, decided_by FROM tbl_blind_invite WHERE invite_id = 'invite-of-lee'");
        })).Should().Be(("expired", "removal"));
        var review = (await stand.ViewAsync(id)).Review;
        review.Should().NotContain(i => i.Id == "invite:invite-of-lee", "it ended at S0");
        review.Single(i => i.Id == "invite:invite-of-admin").DefaultRemove.Should().BeFalse("somebody else's invitation");
        review.Single(i => i.Id == $"slot:{keyForAdmin}").DefaultRemove.Should().BeTrue("issued by this person");

        stand.RekeyNode(stand.AccountsId);
        await stand.AcceptReviewAsync(id);
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_key_slot WHERE slot_id = @s", new { s = keyForAdmin })).Should().Be(0, "removed in S3");
        (await CountAsync(stand.Accounts, "SELECT COUNT(*) FROM tbl_blind_invite WHERE invite_id = 'invite-of-admin' AND state = 'issued'"))
            .Should().Be(1, "kept as pre-selected");
    }

    // (12) an island that re-joins loses the person at its first sync; a tombstone a restore or a join brought is applied before anybody signs in.
    [Fact]
    public async Task A_copy_that_still_holds_the_person_loses_them_at_its_first_sync_and_at_its_next_start()
    {
        await using var stand = await OffboardingStand.CreateAsync(shared: true);
        // The island: a node whose accounts are a copy of the accounts node's (a restored backup), the person's row with the person's id.
        int islandRow;
        using (var scope = stand.Shared!.Services.CreateScope())
        {
            var island = await scope.ServiceProvider.GetRequiredService<UserService>()
                .CreateUserAsync("leaver", "Lee Leaver", OffboardingStand.LeaverPassword, UserRoles.Superadmin, passwordHint: "the usual one");
            using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            await conn.ExecuteAsync("UPDATE tbl_user SET person_id = @p WHERE id = @id", new { p = stand.LeaverPersonId.ToString("D"), id = island.Id });
            islandRow = island.Id;
        }

        await stand.StartRemovalAsync();
        await OffboardingStand.SyncAsync(stand.Shared!, stand.Accounts);

        var row = await InScopeAsync(stand.Shared!, async sp =>
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            return await conn.QueryFirstAsync<(long IsActive, string State)>("SELECT is_active, account_state FROM tbl_user WHERE id = @id", new { id = islandRow });
        });
        row.IsActive.Should().Be(0, "the tombstone deleted the copy of the person's account");
        // ... and with it everything the old password opened there: no sign-in, no door (the node no longer opens with it).
        using (var anonymous = stand.Shared!.CreateClient())
            (await anonymous.PostAsJsonAsync("/api/session/login", new { username = "leaver", password = OffboardingStand.LeaverPassword }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var islandSession = stand.Shared!.Services.GetRequiredService<SessionService>();
        islandSession.Lock();
        (await islandSession.UnlockAsync(OffboardingStand.LeaverPassword)).Should().BeFalse("the person's door on that node went with the account");
        (await islandSession.UnlockAsync(OffboardingStand.AdminPassword)).Should().BeTrue();
        (await CountAsync(stand.Shared!, "SELECT COUNT(*) FROM tbl_removed_person WHERE person_id = @p COLLATE NOCASE", new { p = stand.LeaverPersonId.ToString("D") }))
            .Should().Be(1);
        (await CountAsync(stand.Shared!, "SELECT COUNT(*) FROM tbl_offboarding WHERE origin = 'replicated'")).Should().Be(1, "the shared node sees the process");

        // A tombstone that arrived with a restore or a join (no event): the next start closes the account before anybody signs in.
        int second;
        using (var scope = stand.Shared!.Services.CreateScope())
        {
            var again = await scope.ServiceProvider.GetRequiredService<UserService>()
                .CreateUserAsync("ghost", "Ghost", "Ghost-Password-1", UserRoles.Superadmin, passwordHint: "hint");
            var ghost = Guid.NewGuid();
            using var conn = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            await conn.ExecuteAsync("UPDATE tbl_user SET person_id = @p WHERE id = @id", new { p = ghost.ToString("D"), id = again.Id });
            await conn.ExecuteAsync("INSERT INTO tbl_removed_person (person_id, outcome, removed_at) VALUES (@p, 'deleted', @t)",
                new { p = ghost.ToString("D"), t = DateTime.UtcNow.ToString("O") });
            second = again.Id;
        }
        await BeeMemoryBank.Api.Startup.ApiStartWork.EnforceTombstonesAsync(stand.Shared!.Services);
        (await CountAsync(stand.Shared!, "SELECT is_active FROM tbl_user WHERE id = @id", new { id = second })).Should().Be(0);
    }
}
