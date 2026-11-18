using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Boot;
using Dapper;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// One recovery key a person's removal revoked: its slot, its owner and when it was issued, so the caller can write
/// the same <c>recovery_key_revoked</c> audit entry a single-key revoke writes. Never key material.
/// </summary>
public sealed record RevokedRecoveryKey(int SlotId, int? OwnerUserId, DateTime CreatedAt);

/// <summary>
/// Revokes every door that belongs to one person (release 2.12, plan WS-B2 / E4 step S0): their <c>user</c> password door, their recovery keys
/// and their auto-unlock agents' doors. A recovery key is personal, so it goes with the person; an agent's wrapped master key is wiped and the
/// agent revoked, so none of the person's keys outlives their rights. The boot-store copies are retired BEFORE the change commits (rule 2 of the
/// write protocol), so a revoked key cannot wake a closed node even for the seconds until the rewrite.
/// </summary>
public static class PersonDoors
{
    /// <summary>
    /// The slot ids of every door that belongs to the person — their <c>user</c> password door(s) and their recovery keys — read inside the
    /// caller's transaction. This is exactly the set <see cref="RevokeAllOfAsync(IDbConnection, IDbTransaction, int, IKeySlotRepository, IUnlockMaterialMirror, CancellationToken)"/>
    /// removes, so a caller can run the last-person-door guard (<see cref="SlotDoors.EnsureAPersonDoorRemainsAsync"/>) against it BEFORE any write
    ///. The user door is the slot the row names NOW plus any user slot the database says they own — they differ when a
    /// password change replaced the slot between a load and here.
    /// </summary>
    public static async Task<List<int>> SlotIdsOfAsync(IDbConnection conn, IDbTransaction tx, int userId)
    {
        var doors = new List<int>();
        var current = await conn.ExecuteScalarAsync<int?>("SELECT key_slot_id FROM tbl_user WHERE id = @userId", new { userId }, tx);
        if (current is { } now) doors.Add(now);
        var ownedUserSlots = await conn.QueryAsync<int>(
            "SELECT slot_id FROM tbl_key_slot WHERE owner_user_id = @userId AND slot_type = 'user'", new { userId }, tx);
        foreach (var s in ownedUserSlots) if (!doors.Contains(s)) doors.Add(s);
        var recoveryKeys = await conn.QueryAsync<int>(
            "SELECT slot_id FROM tbl_key_slot WHERE owner_user_id = @userId AND slot_type = 'recovery'", new { userId }, tx);
        foreach (var s in recoveryKeys) if (!doors.Contains(s)) doors.Add(s);
        return doors;
    }

    /// <summary>
    /// Revokes the person's doors INSIDE the caller's transaction (the removal's S0 composes this with the account-state write). Retires the
    /// boot-store copies, deletes the person's user door(s) and recovery keys, revokes their agents and clears the dangling <c>key_slot_id</c>.
    /// The caller commits and then rewrites the boot store (<see cref="SlotDoors.RewriteAsync"/>); on failure the caller rolls back and rewrites.
    /// </summary>
    /// <returns>The recovery keys that were revoked, so the caller can audit each one (owner and issue date) inside the same transaction.</returns>
    public static async Task<IReadOnlyList<RevokedRecoveryKey>> RevokeAllOfAsync(IDbConnection conn, IDbTransaction tx, int userId, IKeySlotRepository slots,
        IUnlockMaterialMirror? mirror, CancellationToken ct = default)
    {
        var doors = await SlotIdsOfAsync(conn, tx, userId);
        // The recovery keys' owner and issue date, for the audit entry the caller writes per deleted key. Read before the delete.
        var recoveryRows = (await conn.QueryAsync<(int SlotId, int? OwnerUserId, DateTime CreatedAt)>(
            "SELECT slot_id, owner_user_id, created_at FROM tbl_key_slot WHERE owner_user_id = @userId AND slot_type = 'recovery' ORDER BY slot_id",
            new { userId }, tx)).ToList();
        var revokedRecovery = recoveryRows.Select(r => new RevokedRecoveryKey(r.SlotId, r.OwnerUserId, r.CreatedAt)).ToList();

        if (mirror != null)
        {
            var agentDoors = await conn.QueryAsync<int>(
                "SELECT id FROM tbl_agent WHERE owner_user_id = @userId AND encrypted_dek IS NOT NULL", new { userId }, tx);
            foreach (var agentId in agentDoors) await mirror.RetireDoorAsync(BootDoorKinds.Agent, agentId, ct);
            foreach (var slotId in doors) await mirror.RetireDoorAsync(BootDoorKinds.Slot, slotId, ct);
        }

        foreach (var slotId in doors) await slots.DeleteAsync(slotId, tx);

        // Revoked, and the wrapped master key wiped (as AgentRepository.DeleteAsync and the account transition do): an agent key that survives
        // only as a bearer string must not be a vault key.
        await conn.ExecuteAsync(
            @"UPDATE tbl_agent
                 SET status = 'D', encrypted_dek = NULL, dek_iv = NULL, salt = NULL, kdf_version = 0, door_public_key = NULL, dek_fp = NULL
               WHERE owner_user_id = @userId", new { userId }, tx);
        // A dangling id would make the person look provisioned and suppress re-provisioning at a later login.
        await conn.ExecuteAsync("UPDATE tbl_user SET key_slot_id = NULL WHERE id = @userId", new { userId }, tx);
        return revokedRecovery;
    }

    /// <summary>
    /// Revokes the person's doors in ONE transaction of its own, then rewrites the boot store. A failure rolls everything back and puts the
    /// retired copies back. For a caller that composes the revocation with other writes, use the transaction-aware overload instead.
    /// </summary>
    public static async Task RevokeAllOfAsync(IDbConnectionFactory db, int userId, IKeySlotRepository slots,
        IUnlockMaterialMirror? mirror, CancellationToken ct = default)
    {
        using var conn = db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            ct.ThrowIfCancellationRequested();
            await RevokeAllOfAsync(conn, tx, userId, slots, mirror, ct);
            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { /* SQLite may have rolled back already; do not mask the real failure */ }
            await SlotDoors.RewriteAsync(mirror);   // the doors that were retired are copied back
            throw;
        }
        await SlotDoors.RewriteAsync(mirror);
    }
}
