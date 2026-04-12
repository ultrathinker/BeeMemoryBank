using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class UserRepository(DbConnectionFactory factory) : BaseRepository(factory), IUserRepository
{
    private const string SelectColumns =
        @"id AS Id, username AS Username, display_name AS DisplayName,
          password_hash AS PasswordHash, role AS Role, key_slot_id AS KeySlotId,
          is_active AS IsActive, created_at AS CreatedAt, last_login_at AS LastLoginAt";

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
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>(
            @"INSERT INTO tbl_user (username, display_name, password_hash, role, key_slot_id, is_active, created_at, last_login_at)
              VALUES (@Username, @DisplayName, @PasswordHash, @Role, @KeySlotId, @IsActive, @CreatedAt, @LastLoginAt);
              SELECT last_insert_rowid()",
            user);
    }

    public async Task UpdateAsync(User user)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_user SET display_name = @DisplayName, role = @Role,
              key_slot_id = @KeySlotId, password_hash = @PasswordHash
              WHERE id = @Id",
            user);
    }

    public async Task DeleteAsync(int id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_user SET is_active = 0 WHERE id = @id",
            new { id });
    }

    public async Task UpdateLastLoginAsync(int id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_user SET last_login_at = @now WHERE id = @id",
            new { now = UtcNow(), id });
    }
}
