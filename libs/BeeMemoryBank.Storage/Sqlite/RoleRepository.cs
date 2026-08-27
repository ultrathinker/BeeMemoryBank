using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class RoleRepository(DbConnectionFactory factory) : BaseRepository(factory), IRoleRepository
{
    private const string SelectCols =
        @"name AS Name, display_name AS DisplayName, description AS Description,
          is_system AS IsSystem, base_policy AS BasePolicy,
          created_at AS CreatedAt, updated_at AS UpdatedAt";

    public async Task<Role?> GetByNameAsync(string name)
    {
        using var conn = OpenConnection();
        return await conn.QueryFirstOrDefaultAsync<Role>(
            $"SELECT {SelectCols} FROM tbl_role WHERE name = @name",
            new { name });
    }

    public async Task<List<Role>> ListAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Role>(
            $"SELECT {SelectCols} FROM tbl_role ORDER BY is_system DESC, name ASC")).ToList();
    }

    public async Task CreateAsync(Role role)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_role (name, display_name, description, is_system, base_policy, created_at, updated_at)
              VALUES (@Name, @DisplayName, @Description, @IsSystem, @BasePolicy, @CreatedAt, @UpdatedAt)",
            new
            {
                role.Name,
                role.DisplayName,
                role.Description,
                IsSystem = role.IsSystem ? 1 : 0,
                role.BasePolicy,
                role.CreatedAt,
                role.UpdatedAt
            });
    }

    public async Task UpdateAsync(string name, string displayName, string? description, string basePolicy)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_role
                 SET display_name = @displayName,
                     description  = @description,
                     base_policy  = @basePolicy,
                     updated_at   = @now
               WHERE name = @name",
            new { name, displayName, description, basePolicy, now = DateTime.UtcNow });
    }

    public async Task<bool> DeleteAsync(string name)
    {
        using var conn = OpenConnection();
        var rows = await conn.ExecuteAsync(
            "DELETE FROM tbl_role WHERE name = @name AND is_system = 0",
            new { name });
        return rows > 0;
    }
}
