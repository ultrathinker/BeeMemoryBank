using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// The write protocol of docs/design/step-b-spec.md 4.3 for the code that creates and deletes key slots: a pending door is written
/// BEFORE the sealed transaction that adds the slot commits (rule 1), the copies of a door are retired BEFORE the delete commits
/// (rule 2), and the callers rewrite the boot store after the operation (rule 3, <see cref="RewriteAsync"/>). With no mirror, or a mirror of
/// a plain node (it does nothing), the calls are the plain repository calls they replace.
/// </summary>
public static class SlotDoors
{
    /// <summary>
    /// Serialises the check-then-act of the last-person-door guard across every revoke path (one key, all ownerless keys, the unknown-owner
    /// set). Without it, two concurrent revokes of the last two person doors each pass the guard while the other key still exists, both delete,
    /// and nobody can open the node. One gate for the process: a revoke is quick, so holding it briefly never blocks a node for long.
    /// </summary>
    public static readonly SemaphoreSlim RevokeGate = new(1, 1);

    /// <summary>
    /// The door a slot row becomes. <paramref name="masterDek"/>, when the writer holds it, names the DEK the door yields (spec 3.4); without it
    /// the row's own <c>dek_fp</c> does (null = unknown).
    /// </summary>
    public static PendingDoor PendingFor(MasterKeyStore slot, byte[]? masterDek) =>
        new(BootDoorKinds.Slot, slot.SlotId, slot.SlotType, slot.EncryptedMasterDek, slot.IV, slot.Salt, slot.ArgonMemory, slot.ArgonIterations,
            slot.ArgonParallelism, null, null, masterDek is { Length: CryptoConstants.KeySize } ? DekFingerprint.Of(masterDek) : slot.DekFingerprint,
            slot.DoorVersion, slot.DoorPublicKey);

    /// <summary>
    /// Replaces the door of an existing slot in place (same id, type and owner) in the door-replacement order of docs/encryption.md: the new
    /// door is written to the boot store as pending BEFORE the sealed commit, the row is changed only while it still holds
    /// <paramref name="expectedEncryptedMasterDek"/> (a concurrent change wins), and the boot store is rewritten afterwards. A crash at any
    /// point leaves a node that opens with the old door or the new one. True when the row was replaced.
    /// </summary>
    public static async Task<bool> ReplaceSlotDoorAsync(IKeySlotRepository slots, IUnlockMaterialMirror? mirror, MasterKeyStore replacement,
        byte[]? expectedEncryptedMasterDek, ILogger? logger = null)
    {
        if (mirror is not null) await mirror.AddPendingDoorAsync(PendingFor(replacement, null));
        bool replaced;
        try
        {
            replaced = await slots.ReplaceDoorAsync(replacement, expectedEncryptedMasterDek);
        }
        catch
        {
            await RewriteAsync(mirror, logger);   // the pending copy of a replacement that did not happen goes again
            throw;
        }
        await RewriteAsync(mirror, logger);
        return replaced;
    }

    /// <summary>
    /// Inserts <paramref name="slot"/> and returns its id; <c>slot.SlotId</c> is set. With a mirror and a connection factory the insert and
    /// the pending door happen inside one sealed transaction: the door is written before the commit.
    /// </summary>
    public static async Task<int> CreateSlotAsync(IKeySlotRepository slots, IDbConnectionFactory? factory, IUnlockMaterialMirror? mirror,
        MasterKeyStore slot, byte[]? masterDek)
    {
        if (mirror is null || factory is null)
        {
            slot.SlotId = await slots.CreateAsync(slot);
            return slot.SlotId;
        }

        try
        {
            using var connection = factory.CreateConnection();
            using var transaction = connection.BeginTransaction();
            try
            {
                slot.SlotId = await slots.CreateAsync(slot, transaction);
                await mirror.AddPendingDoorAsync(PendingFor(slot, masterDek));
                transaction.Commit();
            }
            catch
            {
                try { transaction.Rollback(); } catch { /* SQLite may have rolled back already; do not mask the real failure */ }
                throw;
            }
        }
        catch
        {
            await RewriteAsync(mirror);   // drops the pending copy of the insert that did not happen
            throw;
        }
        return slot.SlotId;
    }

    /// <summary>Deletes a slot; the copies of its door are retired first (rule 2).</summary>
    public static async Task DeleteSlotAsync(IKeySlotRepository slots, IUnlockMaterialMirror? mirror, int slotId)
    {
        if (mirror is not null) await mirror.RetireDoorAsync(BootDoorKinds.Slot, slotId);
        try
        {
            await slots.DeleteAsync(slotId);
        }
        catch
        {
            if (mirror is not null) await RewriteAsync(mirror);   // the delete did not happen: the door is copied back
            throw;
        }
    }

    /// <summary>
    /// The upgrade of ADR 0012 for a slot whose secret has just been proved: <paramref name="row"/> (the slot as the database holds it now) is
    /// opened with <paramref name="kek"/>; when it yields <paramref name="currentDek"/> a v1 door is sealed again as a v2 door for the same secret
    /// (same salt and Argon2 parameters, same id) in the door-replacement order, and a door whose DEK was unknown gets its fingerprint. A door
    /// that yields another DEK (an older one, waiting for its lazy rewrap) and the legacy single slot (its type changes at its migration) stay
    /// as they are. Throws only what the writes throw; the callers treat it as best effort. Neither buffer is cleared here.
    /// </summary>
    /// <returns>True when the row was replaced by a v2 door.</returns>
    public static async Task<bool> UpgradeOpenedSlotAsync(IKeySlotRepository slots, IUnlockMaterialMirror? mirror, MasterKeyStore row, byte[] kek,
        byte[] currentDek, ILogger? logger = null)
    {
        byte[] yielded;
        try
        {
            yielded = Doors.OpenSlot(row, kek);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
        var current = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(yielded, currentDek);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(yielded);
        if (!current) return false;

        if (!Doors.NeedsUpgrade(row))
        {
            if (row.DekFingerprint is null)
            {
                await slots.SetDekFingerprintIfUnknownAsync(row.SlotId, row.EncryptedMasterDek, DekFingerprint.Of(currentDek));
                // The boot copy names the DEK too (rule 3), so the wrappers no door needs can be pruned.
                await RewriteAsync(mirror, logger);
            }
            return false;
        }
        var replacement = Doors.WithDoor(row, Doors.Seal(row.SlotType, kek, row.Salt, currentDek));
        if (!await ReplaceSlotDoorAsync(slots, mirror, replacement, row.EncryptedMasterDek, logger)) return false;
        logger?.LogInformation("A {Type} door was sealed again in the door format of 2.12 at its first use.", row.SlotType);
        return true;
    }

    /// <summary>
    /// Rule 3: replaces every door copy with the sealed database's current set. Best effort - the operation it follows has committed, and
    /// the watcher (4.3) repeats a failed rewrite within seconds - so a failure here is logged and never turned into a failure of the operation.
    /// </summary>
    public static async Task RewriteAsync(IUnlockMaterialMirror? mirror, ILogger? logger = null)
    {
        if (mirror is null) return;
        try
        {
            await mirror.RewriteAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "The boot store could not be rewritten after a change of unlock material; the watcher repeats it.");
        }
    }

    /// <summary>
    /// The last-door guard of revoking ways in (release 2.12): after the named slots are gone, at least one door a PERSON can open must remain —
    /// a recovery key, the legacy password slot, or an active superadmin's password slot. Auto-unlock doors (OS, server, quick unlock) are machine
    /// secrets, not a way a person types their way back in, so they do not count. Throws <see cref="InvalidOperationException"/> when none remains.
    /// </summary>
    public static async Task EnsureAPersonDoorRemainsAsync(IKeySlotRepository slots, IUserRepository users, IReadOnlyCollection<int> removingSlotIds,
        bool recoveryKey = true)
    {
        var remaining = (await slots.GetAllAsync()).Where(s => !removingSlotIds.Contains(s.SlotId)).ToList();
        var superadminSlotIds = (await users.ListActiveAsync())
            .Where(u => u.Role == UserRoles.Superadmin && u.KeySlotId.HasValue)
            .Select(u => u.KeySlotId!.Value).ToHashSet();
        var personDoor = remaining.Any(s =>
            s.SlotType == KeySlotTypes.Recovery
            || s.SlotType == KeySlotTypes.Password
            || (s.SlotType == KeySlotTypes.User && superadminSlotIds.Contains(s.SlotId)));
        if (!personDoor)
            throw new InvalidOperationException(recoveryKey
                ? "Cannot revoke this recovery key — it is the last door a person can open. Issue another key, or make sure a superadmin " +
                  "can still sign in with a password, before revoking it."
                : "Cannot delete this slot — it is the last door a person can open. Issue a recovery key, or make sure a superadmin " +
                  "can still sign in with a password, before deleting it.");
    }

    /// <summary>
    /// Writes one <c>recovery_key_revoked</c> audit entry INSIDE the caller's transaction. The audit
    /// repository opens its own connection, which SQLite's single writer cannot nest inside an open write transaction, so a revocation that
    /// must commit together with its audit writes the row here instead. The entry names the slot, its owner and the actor in the same words as
    /// a single-key revoke (<see cref="KeyManagementService.RemoveSlotAsync"/>) — never any key material.
    /// </summary>
    public static async Task AuditRecoveryKeyRevokedAsync(IDbConnection conn, IDbTransaction tx, int slotId, int? ownerUserId, DateTime createdAt, string? actor)
    {
        var actorType = actor is null ? "system" : "web";
        var by = actor is null ? "a local caller" : "user " + actor;
        await AuditAsync(conn, tx, "key_slot", slotId.ToString(), "recovery_key_revoked", actorType,
            $"Recovery key (slot #{slotId}, {KeyManagementService.OwnerWords(ownerUserId)}, issued {createdAt:yyyy-MM-dd HH:mm} UTC) revoked by {by}");
    }

    /// <summary>
    /// Writes one audit row INSIDE the caller's transaction, with the same columns <c>AuditLogRepository.LogAsync</c> writes. Used where the audit
    /// must commit (or roll back) together with the effect it records — the repository opens its own connection, which SQLite's single writer
    /// cannot nest inside an open write transaction. No entry holds key material.
    /// </summary>
    public static async Task AuditAsync(IDbConnection conn, IDbTransaction tx, string entityType, string entityId, string action, string actorType, string details)
    {
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_audit_log (entity_type, entity_id, action, actor_type, created_at, details)
              VALUES (@entityType, @entityId, @action, @actorType, @createdAt, @details)",
            new { entityType, entityId, action, actorType, createdAt = DateTime.UtcNow.ToString("O"), details }, tx);
    }
}
