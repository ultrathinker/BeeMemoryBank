using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class WhitelistRepository(DbConnectionFactory factory) : BaseRepository(factory), IWhitelistRepository
{
    public async Task<WhitelistEntry?> GetByNodeIdAsync(Guid nodeId, bool includeDeleted = false)
    {
        using var conn = OpenConnection();
        var sql = includeDeleted
            ? @"SELECT
                node_id                 AS NodeId,
                display_name            AS DisplayName,
                ed25519_public_key      AS Ed25519PublicKey,
                api_address             AS ApiAddress,
                can_generate_embeddings AS CanGenerateEmbeddings,
                status                  AS Status,
                created_at              AS CreatedAt,
                updated_at              AS UpdatedAt,
                deleted_at              AS DeletedAt
              FROM tbl_whitelist WHERE node_id = @nodeId"
            : @"SELECT
                node_id                 AS NodeId,
                display_name            AS DisplayName,
                ed25519_public_key      AS Ed25519PublicKey,
                api_address             AS ApiAddress,
                can_generate_embeddings AS CanGenerateEmbeddings,
                status                  AS Status,
                created_at              AS CreatedAt,
                updated_at              AS UpdatedAt,
                deleted_at              AS DeletedAt
              FROM tbl_whitelist WHERE node_id = @nodeId AND status = 'A'";
        return await conn.QuerySingleOrDefaultAsync<WhitelistEntry>(sql, new { nodeId });
    }

    public async Task<List<WhitelistEntry>> GetAllActiveAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<WhitelistEntry>(
            @"SELECT
                node_id                 AS NodeId,
                display_name            AS DisplayName,
                ed25519_public_key      AS Ed25519PublicKey,
                api_address             AS ApiAddress,
                can_generate_embeddings AS CanGenerateEmbeddings,
                status                  AS Status,
                created_at              AS CreatedAt,
                updated_at              AS UpdatedAt,
                deleted_at              AS DeletedAt
              FROM tbl_whitelist WHERE status = 'A' ORDER BY display_name")).ToList();
    }

    public async Task CreateAsync(WhitelistEntry entry)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_whitelist
              (node_id, display_name, ed25519_public_key, api_address, can_generate_embeddings, status, created_at, updated_at)
              VALUES (@NodeId, @DisplayName, @Ed25519PublicKey, @ApiAddress, @CanGenerateEmbeddings, @Status, @CreatedAt, @UpdatedAt)",
            new
            {
                entry.NodeId,
                entry.DisplayName,
                entry.Ed25519PublicKey,
                entry.ApiAddress,
                entry.CanGenerateEmbeddings,
                entry.Status,
                entry.CreatedAt,
                entry.UpdatedAt
            });
    }

    public async Task UpdateAsync(WhitelistEntry entry)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_whitelist
              SET display_name = @DisplayName,
                  api_address = @ApiAddress,
                  ed25519_public_key = @Ed25519PublicKey,
                  status = @Status,
                  can_generate_embeddings = @CanGenerateEmbeddings,
                  updated_at = @UpdatedAt
              WHERE node_id = @NodeId",
            new
            {
                entry.NodeId,
                entry.DisplayName,
                entry.ApiAddress,
                entry.Ed25519PublicKey,
                entry.Status,
                entry.CanGenerateEmbeddings,
                entry.UpdatedAt
            });
    }

    public async Task RevokeAsync(Guid nodeId)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_whitelist
              SET status = 'R', deleted_at = @now, updated_at = @now
              WHERE node_id = @nodeId",
            new { nodeId, now = DateTime.UtcNow });
    }
}
