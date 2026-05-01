using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IWhitelistRepository
{
    Task<WhitelistEntry?> GetByNodeIdAsync(Guid nodeId, bool includeDeleted = false);
    Task<List<WhitelistEntry>> GetAllActiveAsync();
    Task<bool> GetAutoAcceptRestoreAsync(string nodeId);
    Task SetAutoAcceptRestoreAsync(string nodeId, bool autoAccept);
    Task<bool> GetAutoAcceptDekRotationAsync(string nodeId);
    Task SetAutoAcceptDekRotationAsync(string nodeId, bool autoAccept);
    Task CreateAsync(WhitelistEntry entry);
    Task UpdateAsync(WhitelistEntry entry);
    Task RevokeAsync(Guid nodeId);
}
