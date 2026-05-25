using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Storage.Sqlite;

public class RemoteAccountRepository(DbConnectionFactory factory) : BaseRepository(factory), IRemoteAccountRepository
{
    private const string Cols = @"id AS Id, display_name AS DisplayName, base_url AS BaseUrl,
        remote_username AS RemoteUsername, encrypted_token AS EncryptedToken, token_iv AS TokenIv,
        token_expires_at AS TokenExpiresAt, last_sync_at AS LastSyncAt, last_sync_status AS LastSyncStatus,
        last_error AS LastError, created_at AS CreatedAt, updated_at AS UpdatedAt";

    public async Task<RemoteAccount?> GetByIdAsync(Guid id)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<RemoteAccount>(
            $"SELECT {Cols} FROM tbl_remote_account WHERE id = @id", new { id });
    }

    public async Task<List<RemoteAccount>> ListAllAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<RemoteAccount>(
            $"SELECT {Cols} FROM tbl_remote_account ORDER BY display_name")).ToList();
    }

    public async Task CreateAsync(RemoteAccount account)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_remote_account
              (id, display_name, base_url, remote_username, encrypted_token, token_iv,
               token_expires_at, last_sync_at, last_sync_status, last_error, created_at, updated_at)
              VALUES (@Id, @DisplayName, @BaseUrl, @RemoteUsername, @EncryptedToken, @TokenIv,
                      @TokenExpiresAt, @LastSyncAt, @LastSyncStatus, @LastError, @CreatedAt, @UpdatedAt)",
            account);
    }

    public async Task UpdateAsync(RemoteAccount account)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_remote_account
                 SET display_name = @DisplayName, base_url = @BaseUrl,
                     remote_username = @RemoteUsername,
                     encrypted_token = @EncryptedToken, token_iv = @TokenIv,
                     token_expires_at = @TokenExpiresAt,
                     last_sync_at = @LastSyncAt, last_sync_status = @LastSyncStatus,
                     last_error = @LastError, updated_at = @UpdatedAt
               WHERE id = @Id",
            account);
    }

    public async Task UpdateStatusAsync(Guid id, string status, string? error, DateTime? syncedAt)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_remote_account
                 SET last_sync_status = @status, last_error = @error,
                     last_sync_at = COALESCE(@syncedAt, last_sync_at), updated_at = @now
               WHERE id = @id",
            new { id, status, error, syncedAt, now = DateTime.UtcNow });
    }

    public async Task UpdateTokenAsync(Guid id, byte[] encryptedToken, byte[] tokenIv, DateTime? expiresAt)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_remote_account
                 SET encrypted_token = @encryptedToken, token_iv = @tokenIv,
                     token_expires_at = @expiresAt, updated_at = @now
               WHERE id = @id",
            new { id, encryptedToken, tokenIv, expiresAt, now = DateTime.UtcNow });
    }

    public async Task DeleteAsync(Guid id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_remote_account WHERE id = @id", new { id });
    }
}

public class RemoteSubscriptionRepository(DbConnectionFactory factory) : BaseRepository(factory), IRemoteSubscriptionRepository
{
    private const string Cols = @"id AS Id, remote_account_id AS RemoteAccountId,
        remote_folder_id AS RemoteFolderId, remote_folder_path AS RemoteFolderPath,
        mount_path AS MountPath, sync_cursor AS SyncCursor,
        last_full_sync_at AS LastFullSyncAt, created_at AS CreatedAt";

    public async Task<RemoteSubscription?> GetByIdAsync(Guid id)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<RemoteSubscription>(
            $"SELECT {Cols} FROM tbl_remote_subscription WHERE id = @id", new { id });
    }

    public async Task<List<RemoteSubscription>> ListByAccountAsync(Guid accountId)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<RemoteSubscription>(
            $"SELECT {Cols} FROM tbl_remote_subscription WHERE remote_account_id = @accountId ORDER BY mount_path",
            new { accountId })).ToList();
    }

    public async Task<List<RemoteSubscription>> ListAllAsync()
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<RemoteSubscription>($"SELECT {Cols} FROM tbl_remote_subscription ORDER BY mount_path")).ToList();
    }

    public async Task<RemoteSubscription?> GetByMountPathAsync(string mountPath)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<RemoteSubscription>(
            $"SELECT {Cols} FROM tbl_remote_subscription WHERE mount_path = @mountPath",
            new { mountPath });
    }

    public async Task CreateAsync(RemoteSubscription s)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_remote_subscription
              (id, remote_account_id, remote_folder_id, remote_folder_path, mount_path,
               sync_cursor, last_full_sync_at, created_at)
              VALUES (@Id, @RemoteAccountId, @RemoteFolderId, @RemoteFolderPath, @MountPath,
                      @SyncCursor, @LastFullSyncAt, @CreatedAt)",
            s);
    }

    public async Task UpdateCursorAsync(Guid id, string? cursor, DateTime? lastFullSyncAt)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"UPDATE tbl_remote_subscription
                 SET sync_cursor = @cursor,
                     last_full_sync_at = COALESCE(@lastFullSyncAt, last_full_sync_at)
               WHERE id = @id",
            new { id, cursor, lastFullSyncAt });
    }

    public async Task DeleteAsync(Guid id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_remote_subscription WHERE id = @id", new { id });
    }
}

public class RemoteApiTokenRepository(DbConnectionFactory factory) : BaseRepository(factory), IRemoteApiTokenRepository
{
    private const string Cols = @"id AS Id, user_id AS UserId, token_hash AS TokenHash, label AS Label,
        created_at AS CreatedAt, last_used_at AS LastUsedAt, expires_at AS ExpiresAt";

    public async Task<RemoteApiToken?> GetByTokenHashAsync(string tokenHash)
    {
        using var conn = OpenConnection();
        return await conn.QuerySingleOrDefaultAsync<RemoteApiToken>(
            $"SELECT {Cols} FROM tbl_remote_api_token WHERE token_hash = @tokenHash",
            new { tokenHash });
    }

    public async Task<List<RemoteApiToken>> ListByUserAsync(int userId)
    {
        using var conn = OpenConnection();
        return (await conn.QueryAsync<RemoteApiToken>(
            $"SELECT {Cols} FROM tbl_remote_api_token WHERE user_id = @userId ORDER BY created_at DESC",
            new { userId })).ToList();
    }

    public async Task CreateAsync(RemoteApiToken token)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_remote_api_token (id, user_id, token_hash, label, created_at, last_used_at, expires_at)
              VALUES (@Id, @UserId, @TokenHash, @Label, @CreatedAt, @LastUsedAt, @ExpiresAt)",
            token);
    }

    public async Task TouchAsync(Guid id, DateTime lastUsed, DateTime newExpiresAt)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            "UPDATE tbl_remote_api_token SET last_used_at = @lastUsed, expires_at = @newExpiresAt WHERE id = @id",
            new { id, lastUsed, newExpiresAt });
    }

    public async Task DeleteAsync(Guid id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_remote_api_token WHERE id = @id", new { id });
    }

    public async Task DeleteByHashAsync(string tokenHash)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_remote_api_token WHERE token_hash = @tokenHash",
            new { tokenHash });
    }
}
