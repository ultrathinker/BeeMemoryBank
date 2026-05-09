using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class WhitelistRepository(DbConnectionFactory factory) : BaseRepository(factory), IWhitelistRepository
{
    public async Task<WhitelistEntry?> GetByNodeIdAsync(Guid nodeId, bool includeDeleted = false)
    {
        using var conn = OpenConnection();
        // auto_accept_restore was missing from the SELECT pre-existing this change — UI checkbox
        // always rendered as OFF after refresh even though the actual flag in DB was honored by
        // EventApplier (which reads via the dedicated GetAutoAcceptRestoreAsync). Fixed both
        // columns in one go.
        var sql = includeDeleted
            ? @"SELECT
                node_id                    AS NodeId,
                display_name               AS DisplayName,
                ed25519_public_key         AS Ed25519PublicKey,
                api_address                AS ApiAddress,
                can_generate_embeddings    AS CanGenerateEmbeddings,
                status                     AS Status,
                created_at                 AS CreatedAt,
                updated_at                 AS UpdatedAt,
                deleted_at                 AS DeletedAt,
                auto_accept_restore        AS AutoAcceptRestore,
                auto_accept_dek_rotation   AS AutoAcceptDekRotation,
                is_superadmin              AS IsSuperadmin
              FROM tbl_whitelist WHERE node_id = @nodeId"
            : @"SELECT
                node_id                    AS NodeId,
                display_name               AS DisplayName,
                ed25519_public_key         AS Ed25519PublicKey,
                api_address                AS ApiAddress,
                can_generate_embeddings    AS CanGenerateEmbeddings,
                status                     AS Status,
                created_at                 AS CreatedAt,
                updated_at                 AS UpdatedAt,
                deleted_at                 AS DeletedAt,
                auto_accept_restore        AS AutoAcceptRestore,
                auto_accept_dek_rotation   AS AutoAcceptDekRotation,
                is_superadmin              AS IsSuperadmin
              FROM tbl_whitelist WHERE node_id = @nodeId COLLATE NOCASE AND status = 'A'";
        return await conn.QuerySingleOrDefaultAsync<WhitelistEntry>(sql, new { nodeId });
    }

    public async Task<List<WhitelistEntry>> GetAllActiveAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<WhitelistEntry>(
            @"SELECT
                node_id                    AS NodeId,
                display_name               AS DisplayName,
                ed25519_public_key         AS Ed25519PublicKey,
                api_address                AS ApiAddress,
                can_generate_embeddings    AS CanGenerateEmbeddings,
                status                     AS Status,
                created_at                 AS CreatedAt,
                updated_at                 AS UpdatedAt,
                deleted_at                 AS DeletedAt,
                auto_accept_restore        AS AutoAcceptRestore,
                auto_accept_dek_rotation   AS AutoAcceptDekRotation,
                is_superadmin              AS IsSuperadmin
              FROM tbl_whitelist WHERE status = 'A' ORDER BY (substr(display_name,1,1)='_') DESC, display_name")).ToList();
    }

    public async Task CreateAsync(WhitelistEntry entry)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_whitelist
              (node_id, display_name, ed25519_public_key, api_address, can_generate_embeddings, status, created_at, updated_at, is_superadmin)
              VALUES (@NodeId, @DisplayName, @Ed25519PublicKey, @ApiAddress, @CanGenerateEmbeddings, @Status, @CreatedAt, @UpdatedAt, @IsSuperadmin)",
            new
            {
                entry.NodeId,
                entry.DisplayName,
                entry.Ed25519PublicKey,
                entry.ApiAddress,
                entry.CanGenerateEmbeddings,
                entry.Status,
                entry.CreatedAt,
                entry.UpdatedAt,
                IsSuperadmin = entry.IsSuperadmin ? 1 : 0
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
                  is_superadmin = @IsSuperadmin,
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
                IsSuperadmin = entry.IsSuperadmin ? 1 : 0,
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

    public async Task<bool> GetAutoAcceptRestoreAsync(string nodeId)
    {
        using var conn = OpenConnection();
        // status = 'A' filter: a revoked peer ('R') with auto_accept_restore=1 from before
        // revocation must NOT trigger auto-apply on incoming events. Their Ed25519 key still
        // passes signature verification in EventApplier, so without this filter a revoked
        // peer could force a destructive restore. (Found by Kilo R1 security review CRIT-3.)
        var val = await conn.ExecuteScalarAsync<long?>(
            "SELECT auto_accept_restore FROM tbl_whitelist WHERE node_id = @nodeId COLLATE NOCASE AND status = 'A'",
            new { nodeId });
        return val.GetValueOrDefault() != 0;
    }

    public async Task SetAutoAcceptRestoreAsync(string nodeId, bool autoAccept)
    {
        using var conn = OpenConnection();
        // node_id stored UPPERCASE; callers pass lowercase Guid.ToString(). COLLATE NOCASE so
        // UPDATE actually matches. Same fix in SetAutoAcceptDekRotationAsync below. (Found
        // when E2E test couldn't enable auto-accept on a peer.)
        await conn.ExecuteAsync(
            @"UPDATE tbl_whitelist
              SET auto_accept_restore = @autoAccept, updated_at = @now
              WHERE node_id = @nodeId COLLATE NOCASE",
            new { nodeId, autoAccept = autoAccept ? 1 : 0, now = DateTime.UtcNow });
    }

    public async Task<bool> GetAutoAcceptDekRotationAsync(string nodeId)
    {
        using var conn = OpenConnection();
        // status = 'A' filter — same rationale as GetAutoAcceptRestoreAsync above.
        var val = await conn.ExecuteScalarAsync<long?>(
            "SELECT auto_accept_dek_rotation FROM tbl_whitelist WHERE node_id = @nodeId COLLATE NOCASE AND status = 'A'",
            new { nodeId });
        return val.GetValueOrDefault() != 0;
    }

    public async Task SetAutoAcceptDekRotationAsync(string nodeId, bool autoAccept)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_whitelist
              SET auto_accept_dek_rotation = @autoAccept, updated_at = @now
              WHERE node_id = @nodeId COLLATE NOCASE",
            new { nodeId, autoAccept = autoAccept ? 1 : 0, now = DateTime.UtcNow });
    }
}
