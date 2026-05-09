using System.Data;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Services;

public class LegacyPasswordSlotMigrationService(
    IKeySlotRepository keySlotRepo,
    IUserRepository userRepo,
    IDbConnectionFactory dbFactory)
{
    private const string MigrationKey = "legacy_password_unified";
    private static readonly SemaphoreSlim _semaphore = new(1, 1);

    public record MigrationResult(bool Migrated, string? SyntheticAdminUsername);

    public async Task<MigrationResult> MigrateIfNeededAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            using var conn = dbFactory.CreateConnection();
            using (var checkCmd = conn.CreateCommand())
            {
                var p = checkCmd.CreateParameter();
                p.ParameterName = "k";
                p.Value = MigrationKey;
                checkCmd.CommandText = "SELECT COUNT(*) FROM tbl_migration_marker WHERE key = @k";
                checkCmd.Parameters.Add(p);
                var alreadyMigrated = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;
                if (alreadyMigrated) return new MigrationResult(false, null);
            }

            var allSlots = await keySlotRepo.GetAllAsync();
            var passwordSlots = allSlots.Where(s => s.SlotType == "password").ToList();

            if (passwordSlots.Count == 0)
            {
                WriteMarker(conn, null);
                return new MigrationResult(false, null);
            }

            var allUsers = await userRepo.ListActiveAsync();
            var superadminWithSlot = allUsers.FirstOrDefault(u =>
                u.Role == UserRoles.Superadmin && u.KeySlotId != null
                && allSlots.Any(s => s.SlotId == u.KeySlotId.Value && s.SlotType == "user"));

            string? syntheticUsername = null;

            using var tx = conn.BeginTransaction();
            try
            {
                if (superadminWithSlot != null)
                {
                    foreach (var legacy in passwordSlots)
                    {
                        using var delCmd = conn.CreateCommand();
                        delCmd.Transaction = tx;
                        delCmd.CommandText = "DELETE FROM tbl_key_slot WHERE slot_id = @id";
                        var dp = delCmd.CreateParameter();
                        dp.ParameterName = "id";
                        dp.Value = legacy.SlotId;
                        delCmd.Parameters.Add(dp);
                        delCmd.ExecuteNonQuery();
                    }
                }
                else
                {
                    var primary = passwordSlots[0];
                    syntheticUsername = await PickUniqueAdminNameAsync();

                    using (var insCmd = conn.CreateCommand())
                    {
                        insCmd.Transaction = tx;
                        insCmd.CommandText = @"
                            INSERT INTO tbl_user (username, display_name, password_hash, role, key_slot_id, is_active, created_at, last_login_at)
                            VALUES (@u, @dn, '', 'superadmin', @slot, 1, @ts, NULL);
                            SELECT last_insert_rowid();";
                        AddParam(insCmd, "u", syntheticUsername);
                        AddParam(insCmd, "dn", "Migrated Admin");
                        AddParam(insCmd, "slot", primary.SlotId);
                        AddParam(insCmd, "ts", DateTime.UtcNow.ToString("O"));
                        Convert.ToInt32(insCmd.ExecuteScalar());
                    }

                    using (var updCmd = conn.CreateCommand())
                    {
                        updCmd.Transaction = tx;
                        updCmd.CommandText = "UPDATE tbl_key_slot SET slot_type = 'user' WHERE slot_id = @id";
                        AddParam(updCmd, "id", primary.SlotId);
                        updCmd.ExecuteNonQuery();
                    }

                    for (int i = 1; i < passwordSlots.Count; i++)
                    {
                        using var delCmd = conn.CreateCommand();
                        delCmd.Transaction = tx;
                        delCmd.CommandText = "DELETE FROM tbl_key_slot WHERE slot_id = @id";
                        AddParam(delCmd, "id", passwordSlots[i].SlotId);
                        delCmd.ExecuteNonQuery();
                    }
                }

                WriteMarker(conn, tx);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            return new MigrationResult(true, syntheticUsername);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<string> PickUniqueAdminNameAsync()
    {
        var existingUsernames = (await userRepo.ListActiveAsync())
            .Select(u => u.Username.ToLowerInvariant())
            .ToHashSet();
        if (!existingUsernames.Contains("admin")) return "admin";
        for (int i = 1; i < 1000; i++)
        {
            var candidate = $"admin-{i}";
            if (!existingUsernames.Contains(candidate)) return candidate;
        }
        throw new InvalidOperationException("Couldn't find a free synthetic admin username after 1000 attempts");
    }

    private static void WriteMarker(IDbConnection conn, IDbTransaction? tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT OR IGNORE INTO tbl_migration_marker (key, value, set_at)
            VALUES (@k, '1', @ts)";
        AddParam(cmd, "k", MigrationKey);
        AddParam(cmd, "ts", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private static void AddParam(IDbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
