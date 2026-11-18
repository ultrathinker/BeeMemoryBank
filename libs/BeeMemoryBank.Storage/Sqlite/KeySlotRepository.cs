using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class KeySlotRepository(DbConnectionFactory factory) : BaseRepository(factory), IKeySlotRepository
{
    /// <summary>Every column of a door, as <see cref="MasterKeyStore"/> names them.</summary>
    public const string SelectColumns =
        @"slot_id           AS SlotId,
          slot_type         AS SlotType,
          encrypted_master_dek AS EncryptedMasterDek,
          iv                AS IV,
          salt              AS Salt,
          argon_memory      AS ArgonMemory,
          argon_iterations  AS ArgonIterations,
          argon_parallelism AS ArgonParallelism,
          created_at        AS CreatedAt,
          door_version      AS DoorVersion,
          door_public_key   AS DoorPublicKey,
          dek_fp            AS DekFingerprint,
          owner_user_id     AS OwnerUserId,
          created_by_user_id AS CreatedByUserId,
          label             AS Label";

    public async Task<List<MasterKeyStore>> GetAllAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<MasterKeyStore>($"SELECT {SelectColumns} FROM tbl_key_slot ORDER BY slot_id")).ToList();
    }

    public async Task<int> CreateAsync(MasterKeyStore slot, IDbTransaction? transaction = null)
    {
        const string sql = @"INSERT INTO tbl_key_slot
              (slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism, created_at,
               door_version, door_public_key, dek_fp, owner_user_id, created_by_user_id, label)
              VALUES (@SlotType, @EncryptedMasterDek, @IV, @Salt, @ArgonMemory, @ArgonIterations, @ArgonParallelism, @CreatedAt,
                      @DoorVersion, @DoorPublicKey, @DekFingerprint, @OwnerUserId, @CreatedByUserId, @Label);
              SELECT last_insert_rowid()";
        var args = new
        {
            slot.SlotType,
            slot.EncryptedMasterDek,
            slot.IV,
            slot.Salt,
            slot.ArgonMemory,
            slot.ArgonIterations,
            slot.ArgonParallelism,
            slot.CreatedAt,
            slot.DoorVersion,
            slot.DoorPublicKey,
            slot.DekFingerprint,
            slot.OwnerUserId,
            slot.CreatedByUserId,
            slot.Label,
        };

        if (transaction != null)
            return await transaction.Connection!.ExecuteScalarAsync<int>(sql, args, transaction);

        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>(sql, args);
    }

    public async Task DeleteAsync(int slotId, IDbTransaction? transaction = null)
    {
        const string sql = "DELETE FROM tbl_key_slot WHERE slot_id = @slotId";

        // The delete of the node's last v1 door ends the bridge (DekBridge) in the same transaction.
        if (transaction != null)
        {
            await transaction.Connection!.ExecuteAsync(sql, new { slotId }, transaction);
            DekBridge.EndIfUnused(transaction.Connection!, transaction);
            return;
        }

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(sql, new { slotId }, tx);
        DekBridge.EndIfUnused(conn, tx);
        tx.Commit();
    }

    public async Task UpdateSlotKeyAsync(int slotId, byte[] encryptedDek, byte[] iv)
    {
        // A v1 wrap whose DEK this call does not name: the fingerprint becomes unknown (the safe value for the boot store's pruning rule).
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_key_slot SET encrypted_master_dek = @encryptedDek, iv = @iv, door_version = 1, door_public_key = NULL, dek_fp = NULL
              WHERE slot_id = @slotId",
            new { slotId, encryptedDek, iv });
    }

    public async Task<bool> ReplaceDoorAsync(MasterKeyStore door, byte[]? expectedEncryptedMasterDek, IDbTransaction? transaction = null)
    {
        const string sql = @"UPDATE tbl_key_slot
              SET encrypted_master_dek = @EncryptedMasterDek, iv = @IV, salt = @Salt, argon_memory = @ArgonMemory,
                  argon_iterations = @ArgonIterations, argon_parallelism = @ArgonParallelism, door_version = @DoorVersion,
                  door_public_key = @DoorPublicKey, dek_fp = @DekFingerprint
              WHERE slot_id = @SlotId AND (@expected IS NULL OR encrypted_master_dek = @expected)";
        var args = new
        {
            door.SlotId,
            door.EncryptedMasterDek,
            door.IV,
            door.Salt,
            door.ArgonMemory,
            door.ArgonIterations,
            door.ArgonParallelism,
            door.DoorVersion,
            door.DoorPublicKey,
            door.DekFingerprint,
            expected = expectedEncryptedMasterDek,
        };
        // A replacement that upgrades the node's last v1 door ends the bridge (DekBridge) in the same transaction.
        if (transaction != null)
        {
            var changed = await transaction.Connection!.ExecuteAsync(sql, args, transaction) == 1;
            if (changed) DekBridge.EndIfUnused(transaction.Connection!, transaction);
            return changed;
        }
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        var replaced = await conn.ExecuteAsync(sql, args, tx) == 1;
        if (replaced) DekBridge.EndIfUnused(conn, tx);
        tx.Commit();
        return replaced;
    }

    public async Task SetDekFingerprintIfUnknownAsync(int slotId, byte[] encryptedMasterDek, string dekFingerprint)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_key_slot SET dek_fp = @dekFingerprint WHERE slot_id = @slotId AND dek_fp IS NULL AND encrypted_master_dek = @encryptedMasterDek",
            new { slotId, encryptedMasterDek, dekFingerprint });
    }

    public async Task EnsureOsAutoUnlockUniqueIndexAsync()
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(@"CREATE UNIQUE INDEX IF NOT EXISTS ux_key_slot_os_auto_unlock
            ON tbl_key_slot(slot_type)
            WHERE slot_type = 'os_auto_unlock'");
    }
}
