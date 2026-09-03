using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class KeySlotRepository(DbConnectionFactory factory) : BaseRepository(factory), IKeySlotRepository
{
    public async Task<List<MasterKeyStore>> GetAllAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<MasterKeyStore>(
            @"SELECT
                slot_id           AS SlotId,
                slot_type         AS SlotType,
                encrypted_master_dek AS EncryptedMasterDek,
                iv                AS IV,
                salt              AS Salt,
                argon_memory      AS ArgonMemory,
                argon_iterations  AS ArgonIterations,
                argon_parallelism AS ArgonParallelism,
                created_at        AS CreatedAt
              FROM tbl_key_slot ORDER BY slot_id")).ToList();
    }

    public async Task<int> CreateAsync(MasterKeyStore slot, IDbTransaction? transaction = null)
    {
        const string sql = @"INSERT INTO tbl_key_slot
              (slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism, created_at)
              VALUES (@SlotType, @EncryptedMasterDek, @IV, @Salt, @ArgonMemory, @ArgonIterations, @ArgonParallelism, @CreatedAt);
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
            slot.CreatedAt
        };

        if (transaction != null)
            return await transaction.Connection!.ExecuteScalarAsync<int>(sql, args, transaction);

        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>(sql, args);
    }

    public async Task DeleteAsync(int slotId, IDbTransaction? transaction = null)
    {
        const string sql = "DELETE FROM tbl_key_slot WHERE slot_id = @slotId";

        if (transaction != null)
        {
            await transaction.Connection!.ExecuteAsync(sql, new { slotId }, transaction);
            return;
        }

        using var conn = OpenConnection();
        await conn.ExecuteAsync(sql, new { slotId });
    }

    public async Task UpdateSlotKeyAsync(int slotId, byte[] encryptedDek, byte[] iv)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_key_slot SET encrypted_master_dek = @encryptedDek, iv = @iv WHERE slot_id = @slotId",
            new { slotId, encryptedDek, iv });
    }
}
