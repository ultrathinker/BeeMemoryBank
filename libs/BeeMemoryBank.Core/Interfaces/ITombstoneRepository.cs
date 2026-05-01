using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface ITombstoneRepository
{
    Task CreateAsync(Tombstone tombstone);
    Task<bool> ExistsAsync(Guid articleId);
    Task<Tombstone?> GetByEntityIdAsync(Guid articleId);
    /// <summary>Deletes expired tombstone records.</summary>
    Task<int> DeleteExpiredAsync(DateTime now);
}
