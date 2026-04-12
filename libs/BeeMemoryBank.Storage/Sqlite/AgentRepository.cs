using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class AgentRepository(DbConnectionFactory factory) : BaseRepository(factory), IAgentRepository
{
    public async Task<Agent?> GetByKeyHashAsync(string keyHash)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<Agent>(
            @"SELECT id AS Id, name AS Name, description AS Description,
                     key_prefix AS KeyPrefix, key_hash AS KeyHash,
                     encrypted_dek AS EncryptedDek, dek_iv AS DekIV,
                     status AS Status, created_at AS CreatedAt,
                     last_accessed_at AS LastAccessedAt, request_count AS RequestCount
              FROM tbl_agent
              WHERE key_hash = @keyHash AND status = 'A'",
            new { keyHash });
    }

    public async Task<List<Agent>> ListActiveAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<Agent>(
            @"SELECT id AS Id, name AS Name, description AS Description,
                     key_prefix AS KeyPrefix, key_hash AS KeyHash,
                     encrypted_dek AS EncryptedDek, dek_iv AS DekIV,
                     status AS Status, created_at AS CreatedAt,
                     last_accessed_at AS LastAccessedAt, request_count AS RequestCount
              FROM tbl_agent
              WHERE status = 'A'
              ORDER BY created_at DESC")).ToList();
    }

    public async Task<int> CreateAsync(Agent agent)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<int>(
            @"INSERT INTO tbl_agent (name, description, key_prefix, key_hash, encrypted_dek, dek_iv, status, created_at)
              VALUES (@Name, @Description, @KeyPrefix, @KeyHash, @EncryptedDek, @DekIV, @Status, @CreatedAt);
              SELECT last_insert_rowid()",
            agent);
    }

    public async Task DeleteAsync(int id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_agent SET status = 'D' WHERE id = @id",
            new { id });
    }

    public async Task UpdateAccessAsync(int id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_agent SET last_accessed_at = @now, request_count = request_count + 1 WHERE id = @id",
            new { now = DateTime.UtcNow.ToString("o"), id });
    }

}
