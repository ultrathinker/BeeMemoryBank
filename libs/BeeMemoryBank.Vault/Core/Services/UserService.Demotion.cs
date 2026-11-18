using System.Data;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Vault.Sealed;
using BeeMemoryBank.Vault.SharedDoors;
using Dapper;

namespace BeeMemoryBank.Core.Services;

/// <summary>One per-user folder rule written with a demotion (the same shape as a row of <c>tbl_folder_acl_entry</c>).</summary>
public sealed record DemotionFolderRule(Guid FolderId, AclEffect Effect, bool IsReadOnly = false);

/// <summary>
/// The two operations that take a person's access away in one transaction: demoting a superadmin (<see cref="UserService.DemoteSuperadminAsync"/>) and deleting any
/// user (<see cref="UserService.DeleteUserAsync"/>). Both end in the same state for the things that belong to the person alone: their remote API tokens are gone and
/// their agents are revoked (status, and the wrapped master key every one of them carried), so no key, token or agent of a person who lost their rights survives the
/// change by a crash, a second request or a later "cleanup" that never comes.
/// </summary>
public partial class UserService
{
    private IDbConnectionFactory RequireFactory() =>
        factory ?? throw new InvalidOperationException("Changing a person's access needs the database connection: it is done in one transaction or not at all.");

    /// <summary>32 lowercase hex characters, the shape of the stamp the repository writes (<c>lower(hex(randomblob(16)))</c>).</summary>
    private static string NewSecurityStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>
    /// Demotes a superadmin to <paramref name="role"/> and says, in the same transaction, what that person may see: role change, the person's door
    /// (their key slot) deleted, per-user folder rules written, remote tokens deleted, agents revoked, security stamp bumped. Nothing of this happens in pieces:
    /// there is no moment at which the person is an ordinary user who can see every folder.
    ///
    /// <para><b>An explicit access choice is required.</b> Without one the demotion is refused. A choice is any of: a role whose base policy is closed or that
    /// carries allow rules (what it sees is its own); <paramref name="folderRules"/> (per-user rules, for the built-in <c>user</c> role only, the same limit
    /// <c>/api/restrictions/user</c> has); or <paramref name="allowOpenRole"/> = true, the caller saying out loud that the person may see everything an open role sees.
    /// The built-in open <c>user</c> role is never the default.</para>
    ///
    /// <para>The per-user rows the person already has are replaced by <paramref name="folderRules"/> when that list is given (rows left from before a promotion are
    /// inert for a superadmin and would otherwise start to apply unseen), and are left alone when it is not.</para>
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such active user.</exception>
    /// <exception cref="ArgumentException">Bad input: the new role is superadmin or unknown, folder rules with a role other than <c>user</c>, a folder that does not exist.</exception>
    /// <exception cref="InvalidOperationException">The person is not a superadmin, is the last one, or no access choice was made. Whether the CALLER may demote anyone is not asked here: the service does not see the caller, the route checks it (<c>RequireSuperadmin</c>).</exception>
    public async Task DemoteSuperadminAsync(
        int userId, string role, IReadOnlyList<DemotionFolderRule>? folderRules = null, bool allowOpenRole = false,
        string? displayName = null, bool? chatAccess = null, CancellationToken ct = default, string? actor = null)
    {
        ct.ThrowIfCancellationRequested();
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException($"User {userId} not found");
        if (user.Replicated)
            throw await SharedDoorsGuards.ReplicatedUserRefusalAsync(accountsHomeRepo, nodeIdentityRepo, whitelistRepo);
        // While a removal process holds the person, only that process changes the account (release 2.12.0, plan E5).
        if (user.IsBeingRemoved)
            throw new InvalidOperationException("This person is being removed: the removal process decides what happens to the account.");
        await DemoteCoreAsync(user, role, folderRules, allowOpenRole, displayName, chatAccess, fromRemoval: false, ct, actor);
    }

    /// <summary>
    /// The demotion itself. <paramref name="fromRemoval"/>: the last step of a removal process (S5), which demotes an account it locked out at S0 and
    /// makes it an ordinary, active account again; otherwise the account must be active.
    /// </summary>
    private async Task DemoteCoreAsync(
        User user, string role, IReadOnlyList<DemotionFolderRule>? folderRules, bool allowOpenRole, string? displayName, bool? chatAccess,
        bool fromRemoval, CancellationToken ct, string? actor = null, bool enforcingTombstone = false)
    {
        ct.ThrowIfCancellationRequested();
        var db = RequireFactory();
        var userId = user.Id;
        if (user.Role != UserRoles.Superadmin)
            throw new InvalidOperationException("This person is not a superadmin.");

        var newRole = await CanonicalRoleNameAsync(role);
        if (newRole == UserRoles.Superadmin)
            throw new ArgumentException("A demotion names a role other than superadmin.");

        var remaining = (await userRepo.ListActiveAsync()).Count(u => u.Role == UserRoles.Superadmin && u.Id != userId);
        if (remaining == 0)
            throw new InvalidOperationException("Cannot demote the last superadmin");

        // "Cannot demote the last superadmin" is not enough on its own: the remaining superadmins may all be promoted-but-not-yet-logged-in and so hold no slot, and
        // dropping the only slot left would lock the vault permanently.
        if (user.KeySlotId.HasValue)
            await EnsureAnotherSuperadminHoldsAKeySlotAsync(userId, "demote");

        await RequireExplicitAccessChoiceAsync(db, newRole, folderRules, allowOpenRole);

        var now = DateTime.UtcNow;
        var rules = folderRules?.Select(r => new
        {
            UserId = userId,
            // A Guid, not its text: the stack writes (and compares) the id in its own format, and the foreign key to tbl_folder matches exactly.
            r.FolderId,
            Effect = r.Effect.ToString().ToLowerInvariant(),
            // is_read_only is only meaningful for allow-entries.
            IsReadOnly = r.Effect == AclEffect.Allow && r.IsReadOnly ? 1 : 0,
            CreatedAt = now,
        }).ToList();

        await RunAccountTransitionAsync(db, user, ct, async (conn, tx) =>
        {
            if (rules != null)
            {
                foreach (var folderId in rules.Select(r => r.FolderId).Distinct())
                {
                    var exists = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM tbl_folder WHERE id = @folderId AND status = 'A'", new { folderId }, tx);
                    if (exists == 0) throw new ArgumentException("A folder of the access choice does not exist.");
                }
                await conn.ExecuteAsync("DELETE FROM tbl_folder_acl_entry WHERE user_id = @userId", new { userId }, tx);
                foreach (var rule in rules)
                    await conn.ExecuteAsync(
                        @"INSERT INTO tbl_folder_acl_entry (user_id, folder_id, effect, is_read_only, created_at)
                          VALUES (@UserId, @FolderId, @Effect, @IsReadOnly, @CreatedAt)", rule, tx);
            }

            // The role is re-checked in the WHERE clause: two demotions of one person race, only one of them writes. So is the account state: a removal
            // that locked the person out in the meantime wins over a demotion that loaded them before.
            var changed = await conn.ExecuteAsync(
                @"UPDATE tbl_user
                     SET role = @newRole, key_slot_id = NULL, security_stamp = @stamp,
                         display_name = @displayName, chat_access = @chatAccess, account_state = @active
                   WHERE id = @userId AND role = @superadmin AND is_active = 1 AND account_state = @expectedState",
                new
                {
                    newRole, userId, superadmin = UserRoles.Superadmin, stamp = NewSecurityStamp(),
                    active = UserAccountStates.Active,
                    expectedState = fromRemoval ? UserAccountStates.Offboarding : UserAccountStates.Active,
                    displayName = displayName?.Trim() is { Length: > 0 } name ? name : user.DisplayName,
                    chatAccess = (chatAccess ?? user.ChatAccess) ? 1 : 0,
                }, tx);
            if (changed != 1)
                throw new InvalidOperationException("This person is no longer a superadmin.");
        }, actor, enforcingTombstone);

        if (doorsNotifier != null)
            await doorsNotifier.NotifyChangedAsync(immediate: true, ct);
    }

    /// <summary>The refusal of a demotion that does not say what the person may see afterwards.</summary>
    private static async Task RequireExplicitAccessChoiceAsync(
        IDbConnectionFactory db, string newRole, IReadOnlyList<DemotionFolderRule>? folderRules, bool allowOpenRole)
    {
        if (folderRules is { Count: > 0 })
        {
            if (newRole != UserRoles.User)
                throw new ArgumentException(
                    $"Folder rules of their own are only for the built-in '{UserRoles.User}' role. Pick that role, or give the person a role that already says which folders it sees.");
            return;
        }

        using var conn = db.CreateConnection();
        var policy = await conn.ExecuteScalarAsync<string?>("SELECT base_policy FROM tbl_role WHERE name = @newRole", new { newRole });
        if (policy == RoleBasePolicy.Closed) return;

        var allowRules = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM tbl_role_folder_acl_entry WHERE role_name = @newRole AND effect = 'allow'", new { newRole });
        if (allowRules > 0) return;

        if (allowOpenRole) return;

        throw new InvalidOperationException(
            "Say which folders this person may see after they lose superadmin rights: pick a role that is not open, give folders, " +
            "or confirm that the open role is meant. Nothing was changed.");
    }

    /// <summary>
    /// Deletes a user (the account is kept as an inactive row under a released name), in one transaction with everything that went with it: key slot, remote tokens,
    /// agents (revoked, wrapped keys wiped), security stamp. Throws <see cref="ConflictException"/> when no free released name was found in three tries.
    /// </summary>
    private async Task ApplyDeletionAsync(User user, CancellationToken ct = default, string? actor = null, bool enforcingTombstone = false)
    {
        var db = RequireFactory();
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var suffix = new string(Enumerable.Range(0, 3)
                .Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
            var releasedUsername = $"{user.Username}_del_{suffix}";
            try
            {
                await RunAccountTransitionAsync(db, user, ct, async (conn, tx) =>
                {
                    try
                    {
                        await conn.ExecuteAsync(
                            @"UPDATE tbl_user SET is_active = 0, username = @releasedUsername, key_slot_id = NULL, security_stamp = @stamp
                               WHERE id = @id",
                            new { id = user.Id, releasedUsername, stamp = NewSecurityStamp() }, tx);
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
                    {
                        // The caller retries this with a fresh suffix, so it needs to recognise it.
                        throw new UsernameConflictException($"Username '{releasedUsername}' is already taken.", ex);
                    }
                }, actor, enforcingTombstone);
                return;
            }
            catch (UsernameConflictException)
            {
                // The random suffix collided with an already-released username; try another one. The last attempt must NOT let the exception escape: it would reach
                // the client as a 409 naming a `_del_xxx` username the caller never asked for.
                if (attempt == 2) break;
            }
        }
        throw new ConflictException("Failed to release username after 3 attempts. Please try again.");
    }

    /// <summary>
    /// The shared frame of both operations. In ONE transaction: the user's key slot is deleted (its boot-store copies retired first, rule 2 of the write protocol),
    /// the wrapped keys of the user's agents are retired the same way, <paramref name="writeUser"/> writes the user row (and whatever else belongs to that operation),
    /// the remote API tokens are deleted and every agent the user owns is revoked. A failure rolls everything back and puts the retired copies back. After the
    /// commit the caches that could still answer for the old state are dropped and the boot store is rewritten.
    ///
    /// <para>A person's password door goes only while another active superadmin holds a sealed (v2) password door on this node (2.12 cross review,
    /// finding 1; plan E3): the device box this node publishes after the change copies such a door, and without one the box that copied the
    /// leaver's door would stay the active one. <paramref name="enforcingTombstone"/> skips that check (and only that one): a tombstone takes the
    /// door away regardless, because a door that stays opens this node's vault with the leaver's password, which is worse than a box that does.</para>
    /// </summary>
    private async Task RunAccountTransitionAsync(
        IDbConnectionFactory db, User user, CancellationToken ct, Func<IDbConnection, IDbTransaction, Task> writeUser, string? actor = null,
        bool enforcingTombstone = false)
    {
        var userId = user.Id;
        var deletedDoors = 0;
        using (var conn = db.CreateConnection())
        using (var tx = conn.BeginTransaction())
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                // The frame's own last-person-door guard: after this person's doors are gone, a person must still have a way in.
                // "Not the last superadmin" plus EnsureAnotherSuperadminHoldsAKeySlotAsync is not enough — the latter is skipped when the person
                // holds no user slot, and the remaining superadmins may all be slot-less while the demoted one owns the node's last recovery key.
                // The guard runs against exactly the set PersonDoors removes, BEFORE any write, so a demotion or deletion can never lock everyone out.
                var removing = await PersonDoors.SlotIdsOfAsync(conn, tx, userId);
                if (!enforcingTombstone)
                {
                    await SlotDoors.EnsureAPersonDoorRemainsAsync(keySlotRepo, userRepo, removing);
                    await EnsureAnotherSuperadminHoldsASealedDoorAsync(conn, tx, userId);
                }
                else
                {
                    // A tombstone: a removed person's door that stays is a way back in - it opened /api/join, and a removal rotation
                    // applied on this node would re-seal it to the NEW key. So it goes even when it is the last door a PERSON can open, as long as any
                    // door still opens the node (a machine's auto-unlock door, another person's agent): the node opens, and whoever stays signs in
                    // and gets a door of their own. Only the node's very last door is kept (locked out), because a node with no door at all is lost
                    // for everybody, the people who stay included; its holder can open no sign-in, no reset and no join with it.
                    await EnsureAnyDoorRemainsAsync(conn, tx, userId, removing);
                }

                // The person's doors — their user password door, their recovery keys (a recovery key is personal, 2.12) and their auto-unlock agents' doors — are
                // retired and revoked in the boot-store order, in this same transaction (PersonDoors). The slot the row names NOW is read inside the transaction
                // (it holds the write lock), so a password change that replaced the slot between the load and here cannot leave the new door behind. Rule 2: the
                // boot-store copies are gone BEFORE the change commits, so a revoked key cannot wake a closed node, not even for the seconds until the rewrite.
                var revokedRecovery = await PersonDoors.RevokeAllOfAsync(conn, tx, userId, keySlotRepo, mirror, ct);

                // Each recovery key that goes with the person is audited like a single-key revoke — its owner and the administrator who did it —
                // INSIDE this transaction, so the audit and the revocation commit together and a failed audit cannot report a done revoke as failed
                //. The audit repository opens its own connection, which cannot nest in this write transaction, so the row is written here.
                foreach (var rk in revokedRecovery)
                    await SlotDoors.AuditRecoveryKeyRevokedAsync(conn, tx, rk.SlotId, rk.OwnerUserId, rk.CreatedAt, actor);

                await writeUser(conn, tx);

                // PersonDoors.RevokeAllOfAsync above already deleted the person's doors — exactly the set 'removing' (its SlotIdsOfAsync is the
                // single source). Count them so the device box is republished after the commit when a door went (plan WS-A4). The bridge from
                // an old master key, if this was the node's last v1 door, ended in the transaction: KeySlotRepository.DeleteAsync -> DekBridge.EndIfUnused.
                deletedDoors = removing.Count;

                await conn.ExecuteAsync("DELETE FROM tbl_remote_api_token WHERE user_id = @userId", new { userId }, tx);

                tx.Commit();
            }
            catch
            {
                try { tx.Rollback(); } catch { /* SQLite may have rolled back already; do not mask the real failure */ }
                await SlotDoors.RewriteAsync(mirror);   // the doors that were retired are copied back
                throw;
            }
        }

        // Synchronously, before the call returns: the very next request of this person sees the new state, not a cached one.
        CallerStateCache.Invalidate(userId);
        folderAccess.InvalidateCache(userId);
        await SlotDoors.RewriteAsync(mirror);

        // A door was deleted (2.12, plan WS-A4): this node's device box may have copied it, so it is published again from the door it should copy
        // now. (The bridge from an old master key, if this was the node's last v1 door, ended in the transaction above: KeySlotRepository.DeleteAsync.)
        if (deletedDoors > 0) await RepublishDeviceBoxAsync();
    }

    /// <summary>
    /// Refuses only when, without <paramref name="removing"/> and the agents of <paramref name="userId"/>, no door of any kind would open this node: no key
    /// slot, and no active agent of somebody else that carries the wrapped key.
    /// </summary>
    private static async Task EnsureAnyDoorRemainsAsync(IDbConnection conn, IDbTransaction tx, int userId, IReadOnlyCollection<int> removing)
    {
        var slots = (await conn.QueryAsync<int>("SELECT slot_id FROM tbl_key_slot", transaction: tx)).Count(id => !removing.Contains(id));
        var agents = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM tbl_agent WHERE status = 'A' AND encrypted_dek IS NOT NULL AND owner_user_id <> @userId", new { userId }, tx);
        if (slots == 0 && agents == 0)
            throw new InvalidOperationException("This is the last door of this node: it is kept, and the account stays locked out.");
    }

    /// <summary>
    /// Refuses to take <paramref name="userId"/>'s password door away while no OTHER active superadmin (not being removed) holds a sealed (v2) password door
    /// on this node. Counting any door is not enough (2.12 cross review, finding 1): after a door goes, this node's device box is published again from a
    /// sealed door only (<c>IRecoveryBoxPublisher.RepublishForDoorsAsync</c>), so with nothing but v1 stragglers left the box that copied the leaver's
    /// door stays active and keeps opening with their password. A person who holds no password door passes.
    /// </summary>
    internal static async Task EnsureAnotherSuperadminHoldsASealedDoorAsync(IDbConnection conn, IDbTransaction? tx, int userId)
    {
        var holdsAPasswordDoor = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM tbl_user u JOIN tbl_key_slot s ON s.slot_id = u.key_slot_id
               WHERE u.id = @userId AND s.slot_type = @user",
            new { userId, user = KeySlotTypes.User }, tx) > 0;
        if (!holdsAPasswordDoor) return;

        var sealedElsewhere = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM tbl_user u JOIN tbl_key_slot s ON s.slot_id = u.key_slot_id
               WHERE u.id <> @userId AND u.role = @superadmin AND u.is_active = 1 AND u.account_state = @active
                 AND s.slot_type = @user AND s.door_version = 2",
            new { userId, superadmin = UserRoles.Superadmin, active = UserAccountStates.Active, user = KeySlotTypes.User }, tx);
        if (sealedElsewhere == 0)
            throw new InvalidOperationException(
                "Not yet: no other superadmin holds a sealed password door on this node, so this node's device box would keep opening with this " +
                "person's password. Have another superadmin sign in once (that seals their door), then try again. Nothing was changed.");
    }
}
