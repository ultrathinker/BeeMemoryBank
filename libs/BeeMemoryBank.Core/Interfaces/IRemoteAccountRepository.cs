using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IRemoteAccountRepository
{
    Task<RemoteAccount?> GetByIdAsync(Guid id);
    Task<List<RemoteAccount>> ListAllAsync();
    Task CreateAsync(RemoteAccount account);
    Task UpdateAsync(RemoteAccount account);
    Task UpdateStatusAsync(Guid id, string status, string? error, DateTime? syncedAt);
    Task UpdateTokenAsync(Guid id, byte[] encryptedToken, byte[] tokenIv, DateTime? expiresAt);

    /// <summary>
    /// Inserts <paramref name="account"/> only if <paramref name="sealedUnderCurrentKey"/> accepts
    /// the vault's current sentinel, evaluated INSIDE the write transaction (BEGIN IMMEDIATE, so it
    /// serializes with a DEK rotation's transaction). Returns false, writing nothing, when it does
    /// not — the token was sealed under a master DEK a rotation has just retired.
    /// </summary>
    Task<bool> CreateIfSealedUnderCurrentKeyAsync(RemoteAccount account, Func<byte[]?, bool> sealedUnderCurrentKey);

    /// <summary>Token-update counterpart of <see cref="CreateIfSealedUnderCurrentKeyAsync"/>.</summary>
    Task<bool> UpdateTokenIfSealedUnderCurrentKeyAsync(
        Guid id, byte[] encryptedToken, byte[] tokenIv, DateTime? expiresAt, Func<byte[]?, bool> sealedUnderCurrentKey);

    Task DeleteAsync(Guid id);
}

public interface IRemoteSubscriptionRepository
{
    Task<RemoteSubscription?> GetByIdAsync(Guid id);
    Task<List<RemoteSubscription>> ListByAccountAsync(Guid accountId);
    Task<List<RemoteSubscription>> ListAllAsync();
    Task<RemoteSubscription?> GetByMountPathAsync(string mountPath);
    Task CreateAsync(RemoteSubscription subscription);
    Task UpdateCursorAsync(Guid id, string? cursor, DateTime? lastFullSyncAt);
    Task DeleteAsync(Guid id);
}

public interface IRemoteApiTokenRepository
{
    Task<RemoteApiToken?> GetByTokenHashAsync(string tokenHash);
    Task<List<RemoteApiToken>> ListByUserAsync(int userId);
    Task CreateAsync(RemoteApiToken token);
    Task TouchAsync(Guid id, DateTime lastUsed, DateTime newExpiresAt);
    Task DeleteAsync(Guid id);
    Task DeleteByHashAsync(string tokenHash);
}
