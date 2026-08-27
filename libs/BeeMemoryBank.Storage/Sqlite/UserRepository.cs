using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Storage.Sqlite;

public class UserRepository(DbConnectionFactory factory) : BaseRepository(factory), IUserRepository
{
    private const string SelectColumns =
        @"id AS Id, username AS Username, display_name AS DisplayName,
          password_hash AS PasswordHash, role AS Role, key_slot_id AS KeySlotId,
          is_active AS IsActive, chat_access AS ChatAccess, created_at AS CreatedAt, last_login_at AS LastLoginAt,
          security_stamp AS SecurityStamp";

    public async Task<User?> GetByUsernameAsync(string username)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<User>(
            $"SELECT {SelectColumns} FROM tbl_user WHERE username = @username AND is_active = 1",
            new { username });
    }

    public async Task<User?> GetByIdAsync(int id)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<User>(
            $"SELECT {SelectColumns} FROM tbl_user WHERE id = @id AND is_active = 1",
            new { id });
    }

    public async Task<List<User>> ListActiveAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<User>(
            $"SELECT {SelectColumns} FROM tbl_user WHERE is_active = 1 ORDER BY created_at")).ToList();
    }

    public async Task<int> CreateAsync(User user)
    {
        // Stamp every new user immediately so the login cookie has a claim to validate against.
        if (string.IsNullOrEmpty(user.SecurityStamp))
            user.SecurityStamp = GenerateStamp();

        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>(
            @"INSERT INTO tbl_user (username, display_name, password_hash, role, key_slot_id, is_active, chat_access, created_at, last_login_at, security_stamp)
              VALUES (@Username, @DisplayName, @PasswordHash, @Role, @KeySlotId, @IsActive, @ChatAccess, @CreatedAt, @LastLoginAt, @SecurityStamp);
              SELECT last_insert_rowid()",
            user);
    }

    public async Task UpdateAsync(User user)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_user SET display_name = @DisplayName, role = @Role,
              key_slot_id = @KeySlotId, password_hash = @PasswordHash,
              is_active = @IsActive, chat_access = @ChatAccess
              WHERE id = @Id",
            new
            {
                user.Id, user.DisplayName, user.Role,
                user.KeySlotId, user.PasswordHash,
                IsActive = user.IsActive ? 1 : 0,
                ChatAccess = user.ChatAccess ? 1 : 0
            });
    }

    public async Task DeleteAsync(int id, string releasedUsername)
    {
        using var conn = OpenConnection();
        try
        {
            await conn.ExecuteAsync(
                "UPDATE tbl_user SET is_active = 0, username = @releasedUsername WHERE id = @id",
                new { id, releasedUsername });
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("username_conflict");
        }
    }

    public async Task UpdateLastLoginAsync(int id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_user SET last_login_at = @now WHERE id = @id",
            new { now = UtcNow(), id });
    }

    public async Task RepointKeySlotAsync(int oldSlotId, int newSlotId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_user SET key_slot_id = @newSlotId WHERE key_slot_id = @oldSlotId",
            new { oldSlotId, newSlotId });
    }

    // The WHERE clause re-checks every precondition the caller validated before spending
    // ~100ms in Argon2id, so a concurrent login or an admin demote/deactivate in that window
    // loses the race instead of being silently overwritten. Only key_slot_id is written.
    public async Task<bool> TryAssignKeySlotAsync(int userId, int slotId)
    {
        using var conn = OpenConnection();
        var rows = await conn.ExecuteAsync(
            @"UPDATE tbl_user SET key_slot_id = @slotId
              WHERE id = @userId AND key_slot_id IS NULL AND role = 'superadmin' AND is_active = 1",
            new { userId, slotId });
        return rows > 0;
    }

    public async Task ClearKeySlotAsync(int slotId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_user SET key_slot_id = NULL WHERE key_slot_id = @slotId",
            new { slotId });
    }

    // NOTE: no is_active filter — deletion bumps the stamp, so a pre-deletion cookie must
    // still resolve here to be rejected on mismatch. Only a truly absent row returns null.
    public async Task<string?> GetSecurityStampAsync(int id)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT security_stamp FROM tbl_user WHERE id = @id",
            new { id });
    }

    public async Task<string> BumpSecurityStampAsync(int id)
    {
        var stamp = GenerateStamp();
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_user SET security_stamp = @stamp WHERE id = @id",
            new { stamp, id });
        return stamp;
    }

    /// <summary>32 lowercase hex chars, matching the migration's lower(hex(randomblob(16))).</summary>
    private static string GenerateStamp()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
