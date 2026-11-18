using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.DekRotation;

/// <summary>One v1 door the bridge exists for: the slot, its kind, and whose it is (null when it belongs to nobody known).</summary>
public sealed record DekBridgeDoor(int SlotId, string Kind, string? OwnerName);

/// <summary>
/// What the Master key page shows about the bridge: whether chain material is kept, the v1 doors it is kept for, and the oldest rotation it
/// still bridges (its commit id and when this node applied it).
/// </summary>
public sealed record DekBridgeReport(bool BridgePresent, int V1DoorCount, IReadOnlyList<DekBridgeDoor> Doors, string? SinceRotation, string? SinceAppliedAt);

/// <summary>The owner removed the bridge: how many v1 doors went and whose they were.</summary>
public sealed record DekBridgeRemoval(int DoorsRemoved, int ChainRowsCleared, IReadOnlyList<DekBridgeDoor> Doors);

/// <summary>
/// The bridge from an old master DEK on this node (release 2.12, plan WS-A3; <see cref="DekBridge"/> for the rule). It exists only while a v1
/// door exists and ends by itself when the last one is upgraded or deleted. The owner can end it at once: <see cref="RemoveNowAsync"/> deletes
/// every v1 door and the chain, and the people those doors belonged to set their passwords again, as after a rotation before 2.12. Refused
/// when it would leave the node without a sealed door of an active superadmin, which is the one way a person opens it.
/// </summary>
public sealed class DekBridgeService(
    IDbConnectionFactory db,
    IAuditLogRepository? audit = null,
    IUnlockMaterialMirror? mirror = null,
    ILogger<DekBridgeService>? logger = null,
    IRecoveryBoxPublisher? recoveryBoxes = null,
    SessionService? session = null) : IDekBridgeStatus
{
    /// <summary>The refusal of <see cref="RemoveNowAsync"/> when no active superadmin has a sealed door; shown as it is.</summary>
    public const string NobodyLeftRefusal =
        "The bridge cannot be removed now: no active superadmin has signed in on this node since the update, so removing the old doors would leave nobody who can open it. Sign in first.";

    public async Task<int> GetV1DoorCountAsync()
    {
        using var conn = db.CreateConnection();
        return DekBridge.HasV1Doors(conn) ? (int)await conn.ExecuteScalarAsync<long>(DekBridge.CountV1DoorsSql) : 0;
    }

    public async Task<bool> IsBridgePresentAsync()
    {
        using var conn = db.CreateConnection();
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM tbl_dek_rotation_state WHERE chain_encrypted_new_dek IS NOT NULL OR chain_iv IS NOT NULL") > 0;
    }

    public async Task<DekBridgeReport> GetReportAsync()
    {
        using var conn = db.CreateConnection();
        var doors = await V1DoorsAsync(conn, null);
        var since = await conn.QueryFirstOrDefaultAsync<(string EventId, string? AppliedAt)?>(
            @"SELECT event_id, applied_at FROM tbl_dek_rotation_state
               WHERE chain_encrypted_new_dek IS NOT NULL OR chain_iv IS NOT NULL ORDER BY created_at, event_id LIMIT 1");
        return new DekBridgeReport(since is not null, doors.Count, doors, since?.EventId, since?.AppliedAt);
    }

    /// <summary>Deletes every v1 door and every chain row. <paramref name="actor"/> names who did it in the audit entry.</summary>
    /// <exception cref="InvalidOperationException">The node would be left without a sealed door of an active superadmin.</exception>
    public async Task<DekBridgeRemoval> RemoveNowAsync(string actor)
    {
        DekBridgeRemoval removal;
        try
        {
            using var conn = db.CreateConnection();
            using var tx = conn.BeginTransaction();
            var doors = await V1DoorsAsync(conn, tx);
            var keeps = await conn.ExecuteScalarAsync<long>(
                @"SELECT COUNT(*) FROM tbl_key_slot s JOIN tbl_user u ON u.key_slot_id = s.slot_id
                   WHERE s.slot_type = 'user' AND s.door_version = 2 AND u.role = 'superadmin' AND u.is_active = 1", transaction: tx);
            if (doors.Count > 0 && keeps == 0)
                throw new InvalidOperationException(NobodyLeftRefusal);

            // Rule 2 of the write protocol: the copies of a door are retired before the delete commits.
            if (mirror is not null)
                foreach (var door in doors) await mirror.RetireDoorAsync(BootDoorKinds.Slot, door.SlotId);
            foreach (var door in doors)
            {
                await conn.ExecuteAsync("UPDATE tbl_user SET key_slot_id = NULL WHERE key_slot_id = @id", new { id = door.SlotId }, tx);
                await conn.ExecuteAsync("DELETE FROM tbl_key_slot WHERE slot_id = @id", new { id = door.SlotId }, tx);
            }
            var cleared = await conn.ExecuteAsync(DekBridge.ClearAllSql, transaction: tx);
            tx.Commit();
            removal = new DekBridgeRemoval(doors.Count, cleared, doors);
        }
        finally
        {
            // Rule 3 (and the copies of a delete that did not happen come back).
            await SlotDoors.RewriteAsync(mirror, logger);
        }

        // Doors were deleted: this node's device box may have copied one, so it is published again from the door it should copy
        // now, as after every other delete of a door.
        if (removal.DoorsRemoved > 0) await RepublishDeviceBoxAsync();

        if (removal.DoorsRemoved > 0 || removal.ChainRowsCleared > 0)
        {
            var people = string.Join(", ", removal.Doors.Select(d => d.OwnerName ?? d.Kind).Distinct());
            logger?.LogWarning(
                "The bridge from the old master key was removed by {Actor}: {Doors} old-format door(s) deleted ({People}), {Chain} chain row(s) cleared. Those people set their passwords again.",
                actor, removal.DoorsRemoved, people, removal.ChainRowsCleared);
            if (audit is not null)
                await audit.LogAsync("dek_rotation", "bridge", "dek_bridge_removed", "web",
                    $"Bridge from the old master key removed by {actor}; old-format doors deleted={removal.DoorsRemoved} ({people}); chain rows cleared={removal.ChainRowsCleared}");
        }
        return removal;
    }

    private async Task RepublishDeviceBoxAsync()
    {
        if (recoveryBoxes is null || session is not { IsUnlocked: true }) return;
        byte[]? dek = null;
        try
        {
            dek = session.GetMasterDek();
            await recoveryBoxes.RepublishForDoorsAsync(dek);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning("The device box could not be published again after the bridge was removed ({Type}); the next sign-in publishes it.",
                ex.GetType().Name);
        }
        finally
        {
            if (dek is not null) Array.Clear(dek);
        }
    }

    private static async Task<List<DekBridgeDoor>> V1DoorsAsync(System.Data.IDbConnection conn, System.Data.IDbTransaction? tx)
    {
        if (!DekBridge.HasV1Doors(conn, tx)) return [];
        return (await conn.QueryAsync<(long SlotId, string Kind, string? Owner)>(
            @"SELECT s.slot_id, s.slot_type,
                     COALESCE((SELECT display_name FROM tbl_user WHERE id = s.owner_user_id),
                              (SELECT display_name FROM tbl_user WHERE key_slot_id = s.slot_id LIMIT 1))
                FROM tbl_key_slot s WHERE s.door_version = 1 ORDER BY s.slot_id", transaction: tx))
            .Select(r => new DekBridgeDoor((int)r.SlotId, r.Kind, r.Owner)).ToList();
    }
}
