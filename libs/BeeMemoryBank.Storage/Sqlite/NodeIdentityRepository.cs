using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class NodeIdentityRepository(DbConnectionFactory factory) : BaseRepository(factory), INodeIdentityRepository
{
    public async Task<NodeIdentity?> GetAsync()
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<NodeIdentity>(
            @"SELECT
                node_id                  AS NodeId,
                display_name             AS DisplayName,
                ed25519_public_key       AS Ed25519PublicKey,
                ed25519_private_key      AS Ed25519PrivateKey,
                ed25519_private_key_iv   AS Ed25519PrivateKeyIV,
                ed25519_private_key_v    AS Ed25519PrivateKeyV,
                can_generate_embeddings  AS CanGenerateEmbeddings,
                initial_sync_completed   AS InitialSyncCompleted,
                created_at               AS CreatedAt
              FROM tbl_node_identity LIMIT 1");
    }

    public async Task CreateAsync(NodeIdentity identity)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_node_identity
              (node_id, display_name, ed25519_public_key, ed25519_private_key,
               ed25519_private_key_iv, ed25519_private_key_v,
               can_generate_embeddings, initial_sync_completed, created_at)
              VALUES (@NodeId, @DisplayName, @Ed25519PublicKey, @Ed25519PrivateKey,
                      @Ed25519PrivateKeyIV, @Ed25519PrivateKeyV,
                      @CanGenerateEmbeddings, @InitialSyncCompleted, @CreatedAt)",
            new
            {
                identity.NodeId,
                identity.DisplayName,
                identity.Ed25519PublicKey,
                identity.Ed25519PrivateKey,
                identity.Ed25519PrivateKeyIV,
                identity.Ed25519PrivateKeyV,
                identity.CanGenerateEmbeddings,
                identity.InitialSyncCompleted,
                identity.CreatedAt
            });
    }

    public async Task MarkInitialSyncCompletedAsync()
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("UPDATE tbl_node_identity SET initial_sync_completed = 1 WHERE rowid = (SELECT rowid FROM tbl_node_identity LIMIT 1)");
    }


    public async Task StoreSentinelAsync(byte[] sentinelValue)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_node_identity SET sentinel_value = @sentinelValue WHERE rowid = (SELECT rowid FROM tbl_node_identity LIMIT 1)",
            new { sentinelValue });
    }

    public async Task<byte[]?> GetSentinelAsync()
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<byte[]?>(
            "SELECT sentinel_value FROM tbl_node_identity LIMIT 1");
    }

    public async Task UpgradePrivateKeyToV1Async(Guid nodeId, byte[] wrappedPrivateKey, byte[] iv)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_node_identity
              SET ed25519_private_key = @wrappedPrivateKey,
                  ed25519_private_key_iv = @iv,
                  ed25519_private_key_v = 1
              WHERE node_id = @nodeId AND ed25519_private_key_v = 0",
            new { nodeId, wrappedPrivateKey, iv });
    }

    public async Task<(int ExpireHours, bool SlidingExpiration)> GetSessionSettingsAsync()
    {
        using var conn = OpenConnection();
        var row = await conn.QuerySingleOrDefaultAsync<SessionSettingsRow>(
            @"SELECT session_expire_hours AS ExpireHours, session_sliding_expiration AS SlidingExpiration
              FROM tbl_node_identity LIMIT 1");
        return row is null ? (48, true) : (row.ExpireHours, row.SlidingExpiration);
    }

    public async Task<(DateTime ChangedAt, string ByNode)?> GetMasterPasswordNoticeAsync()
    {
        using var conn = OpenConnection();
        var row = await conn.QuerySingleOrDefaultAsync<MasterPasswordNoticeRow>(
            @"SELECT master_password_changed_elsewhere_at AS ChangedAt,
                     master_password_changed_by_node      AS ByNode
              FROM tbl_node_identity LIMIT 1");
        if (row?.ChangedAt is null) return null;
        return (DateTime.Parse(row.ChangedAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
                row.ByNode ?? "another node");
    }

    public async Task SetMasterPasswordNoticeAsync(DateTime changedAt, string byNode)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_node_identity
              SET master_password_changed_elsewhere_at = @changedAt,
                  master_password_changed_by_node      = @byNode
              WHERE rowid = (SELECT rowid FROM tbl_node_identity LIMIT 1)",
            new { changedAt = changedAt.ToString("O"), byNode });
    }

    public async Task ClearMasterPasswordNoticeAsync()
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_node_identity
              SET master_password_changed_elsewhere_at = NULL,
                  master_password_changed_by_node      = NULL
              WHERE rowid = (SELECT rowid FROM tbl_node_identity LIMIT 1)");
    }

    public async Task<DateTime?> GetMasterPasswordChangedLocallyAtAsync()
    {
        using var conn = OpenConnection();
        var value = await conn.ExecuteScalarAsync<string?>(
            "SELECT master_password_changed_locally_at FROM tbl_node_identity LIMIT 1");
        return value is null
            ? null
            : DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    public async Task SetMasterPasswordChangedLocallyAtAsync(DateTime at)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_node_identity
              SET master_password_changed_locally_at = @at
              WHERE rowid = (SELECT rowid FROM tbl_node_identity LIMIT 1)",
            new { at = at.ToString("O") });
    }

    private sealed class MasterPasswordNoticeRow
    {
        public string? ChangedAt { get; set; }
        public string? ByNode { get; set; }
    }

    public async Task SetSessionSettingsAsync(int expireHours, bool slidingExpiration)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_node_identity
              SET session_expire_hours = @expireHours,
                  session_sliding_expiration = @slidingExpiration
              WHERE rowid = (SELECT rowid FROM tbl_node_identity LIMIT 1)",
            new { expireHours, slidingExpiration });
    }

    public async Task<string?> GetBrandNameAsync()
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT brand_name FROM tbl_node_identity LIMIT 1");
    }

    public async Task SetBrandNameAsync(string? brandName)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_node_identity SET brand_name = @brandName WHERE rowid = (SELECT rowid FROM tbl_node_identity LIMIT 1)",
            new { brandName = string.IsNullOrWhiteSpace(brandName) ? null : brandName.Trim() });
    }

    public async Task SetCanGenerateEmbeddingsAsync(bool enabled)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_node_identity SET can_generate_embeddings = @enabled WHERE rowid = (SELECT rowid FROM tbl_node_identity LIMIT 1)",
            new { enabled });
    }

    private sealed class SessionSettingsRow
    {
        public int ExpireHours { get; set; }
        public bool SlidingExpiration { get; set; }
    }
}
