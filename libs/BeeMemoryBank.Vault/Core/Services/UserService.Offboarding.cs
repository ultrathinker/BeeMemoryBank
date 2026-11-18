using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Core.Services;

/// <summary>
/// The account side of the removal of a superadmin (release 2.12.0, plan WS-E): the lock-out of step S0, the end of step S5, and the enforcement of a
/// tombstone (plan R1). Each runs through the same account-transition frame as a demotion and a deletion (<c>RunAccountTransitionAsync</c>): the
/// person's doors (password door, recovery keys, auto-unlock agents), remote tokens and agents go in the transaction that changes the account, with the
/// boot-store order and the cache invalidation that frame already guarantees.
/// </summary>
public partial class UserService
{
    /// <summary>
    /// Step S0 of a removal, on the accounts node: in ONE transaction the account becomes "being removed" (<see cref="UserAccountStates.Offboarding"/>:
    /// no sign-in, no rights, no new door), its security stamp changes (every open browser session ends), its doors, recovery keys, auto-unlock agents,
    /// remote tokens and agents are revoked, and <paramref name="writeProcess"/> writes the process record (and whatever else the caller needs in the
    /// same transaction). Nothing of it happens in pieces.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such active user.</exception>
    /// <exception cref="InvalidOperationException">Not a superadmin, already being removed, or the last superadmin.</exception>
    public async Task LockOutForRemovalAsync(int userId, Func<IDbConnection, IDbTransaction, Task> writeProcess, CancellationToken ct = default, string? actor = null)
    {
        ct.ThrowIfCancellationRequested();
        var db = RequireFactory();
        var user = await userRepo.GetByIdAsync(userId)
            ?? throw new KeyNotFoundException($"User {userId} not found");
        if (user.Role != UserRoles.Superadmin)
            throw new InvalidOperationException("This person is not a superadmin.");
        if (user.IsBeingRemoved)
            throw new InvalidOperationException("This person is already being removed.");

        var remaining = (await userRepo.ListActiveAsync())
            .Count(u => u.Role == UserRoles.Superadmin && u.Id != userId && !u.IsBeingRemoved);
        if (remaining == 0)
            throw new InvalidOperationException("Cannot remove the last superadmin.");
        if (user.KeySlotId.HasValue)
            await EnsureAnotherSuperadminHoldsAKeySlotAsync(userId, "remove");

        await RunAccountTransitionAsync(db, user, ct, async (conn, tx) =>
        {
            // The state is re-checked in the WHERE clause: two removals (or a removal and a demotion) of one person race, only one of them writes.
            var changed = await conn.ExecuteAsync(
                @"UPDATE tbl_user SET account_state = @offboarding, key_slot_id = NULL, security_stamp = @stamp
                   WHERE id = @userId AND role = @superadmin AND is_active = 1 AND account_state = @active",
                new
                {
                    userId, offboarding = UserAccountStates.Offboarding, active = UserAccountStates.Active,
                    superadmin = UserRoles.Superadmin, stamp = NewSecurityStamp(),
                }, tx);
            if (changed != 1)
                throw new InvalidOperationException("This person is no longer an active superadmin.");
            await writeProcess(conn, tx);
        }, actor);
    }

    /// <summary>
    /// Checks the target of a demotion BEFORE a removal starts (plan E3: nothing changes if a check fails): the role exists and is not superadmin, the
    /// folders exist, and the choice says what the person may see (the rule: never the open role by default). Returns the canonical role name.
    /// </summary>
    public async Task<string> ValidateDemotionTargetAsync(string role, IReadOnlyList<DemotionFolderRule>? folderRules, bool allowOpenRole)
    {
        var db = RequireFactory();
        var newRole = await CanonicalRoleNameAsync(role);
        if (newRole == UserRoles.Superadmin)
            throw new ArgumentException("A demotion names a role other than superadmin.");
        await RequireExplicitAccessChoiceAsync(db, newRole, folderRules, allowOpenRole);
        if (folderRules is { Count: > 0 })
        {
            using var conn = db.CreateConnection();
            foreach (var folderId in folderRules.Select(r => r.FolderId).Distinct())
                if (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_folder WHERE id = @folderId AND status = 'A'", new { folderId }) == 0)
                    throw new ArgumentException("A folder of the access choice does not exist.");
        }
        return newRole;
    }

    /// <summary>
    /// Step S5 of a removal: the account a removal holds is deleted, or demoted to the role and folder rules stored at S0 (and is an ordinary, active
    /// account again). Idempotent: an account that is already deleted, or no longer a superadmin, is left as it is.
    /// </summary>
    /// <returns>True when this call changed the account.</returns>
    public async Task<bool> CloseRemovalAsync(int userId, bool delete, string? role, IReadOnlyList<DemotionFolderRule>? folderRules, bool allowOpenRole,
        CancellationToken ct = default)
    {
        var user = await userRepo.GetByIdAsync(userId);
        if (user is null || user.Role != UserRoles.Superadmin) return false;   // done before (a crash after the change, a second continue)
        if (delete)
        {
            await ApplyDeletionAsync(user, ct);
            return true;
        }
        await DemoteCoreAsync(user, role ?? throw new ArgumentException("A demotion names the role."), folderRules, allowOpenRole, null, null, fromRemoval: true, ct);
        return true;
    }

    /// <summary>
    /// Applies a tombstone to one local account (plan R1): first the account is locked out (no sign-in, no rights) so that nothing the person holds keeps
    /// working while the rest runs; then it is deleted, or - when <paramref name="demotion"/> names the role and folders this node stored for that
    /// removal - demoted. A deletion that the last-door guards refuse leaves the account locked out and says so in the return value.
    /// </summary>
    /// <returns>True when the account ended as the tombstone says; false when it is only locked out.</returns>
    public async Task<bool> EnforceTombstoneAsync(User user, (string Role, IReadOnlyList<DemotionFolderRule>? Rules, bool AllowOpenRole)? demotion,
        CancellationToken ct = default)
    {
        var db = RequireFactory();
        using (var conn = db.CreateConnection())
        {
            await conn.ExecuteAsync(
                "UPDATE tbl_user SET account_state = @offboarding, security_stamp = @stamp WHERE id = @id AND is_active = 1",
                new { id = user.Id, offboarding = UserAccountStates.Offboarding, stamp = NewSecurityStamp() });
        }
        CallerStateCache.Invalidate(user.Id);
        user.AccountState = UserAccountStates.Offboarding;

        try
        {
            // enforcingTombstone: the door goes even when no other superadmin holds a sealed door here (see RunAccountTransitionAsync).
            if (demotion is { } d && user.Role == UserRoles.Superadmin)
                await DemoteCoreAsync(user, d.Role, d.Rules, d.AllowOpenRole, null, null, fromRemoval: true, ct, enforcingTombstone: true);
            else
                await ApplyDeletionAsync(user, ct, enforcingTombstone: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            // The last superadmin or the last door of this node: the account stays locked out (no sign-in, no rights) and the caller reports it.
            return false;
        }
    }

    /// <summary>
    /// Before a promotion (plan E7): refused while a removal of this person is open; a person with a tombstone (removed before, demoted) gets a fresh person
    /// id first, so that tombstone, which stays for ever, is never applied to the new superadmin.
    /// </summary>
    private async Task PrepareForPromotionAsync(User user)
    {
        if (factory is null) return;   // tools and tests without the database factory have no removal tables either
        if (await HasOpenRemovalAsync(user.PersonId))
            throw new InvalidOperationException("This person is being removed: they cannot be made a superadmin until that process is closed.");
        using var conn = factory.CreateConnection();
        var tombstone = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM tbl_removed_person WHERE person_id = @p COLLATE NOCASE", new { p = user.PersonId.ToString("D") });
        if (tombstone == 0) return;
        var fresh = Guid.NewGuid();
        await conn.ExecuteAsync("UPDATE tbl_user SET person_id = @fresh WHERE id = @id", new { fresh = fresh.ToString("D"), id = user.Id });
        user.PersonId = fresh;
    }

    /// <summary>The removal processes open on this node for <paramref name="personId"/> (any origin).</summary>
    public async Task<bool> HasOpenRemovalAsync(Guid personId)
    {
        var db = RequireFactory();
        using var conn = db.CreateConnection();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM tbl_offboarding WHERE person_id = @p COLLATE NOCASE AND closed_at IS NULL",
            new { p = personId.ToString("D") }) > 0;
    }
}
