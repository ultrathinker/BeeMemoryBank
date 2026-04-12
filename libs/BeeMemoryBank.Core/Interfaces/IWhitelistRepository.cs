using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IWhitelistRepository
{
    Task<WhitelistEntry?> GetByNodeIdAsync(Guid nodeId, bool includeDeleted = false);
    Task<List<WhitelistEntry>> GetAllActiveAsync();
    Task CreateAsync(WhitelistEntry entry);
    Task UpdateAsync(WhitelistEntry entry);
    Task RevokeAsync(Guid nodeId);
}
