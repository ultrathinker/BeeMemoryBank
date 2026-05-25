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
